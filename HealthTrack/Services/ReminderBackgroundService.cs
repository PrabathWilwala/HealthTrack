using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

/// <summary>
/// Hosted background worker that periodically generates automated notifications:
/// appointment reminders (24 h and 1 h before), workout and meal reminders,
/// low-adherence alerts and daily client-activity summaries for trainers.
/// Every reminder carries a dedup key, so running the job often never creates duplicates.
/// </summary>
public sealed class ReminderBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public ReminderBackgroundService(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILogger<ReminderBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue("Notifications:ReminderIntervalMinutes", 5);
        _interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the application a moment to finish migrating/seeding before the first run.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder job failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs every reminder rule once. Public so it can be triggered manually by an administrator.</summary>
    public async Task RunOnceAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        await SendAppointmentRemindersAsync(db, notifications, directory);
        await SendWorkoutAndMealRemindersAsync(db, notifications);
        await SendTrainerAlertsAsync(db, notifications, analytics, settings, directory);
    }

    private static async Task SendAppointmentRemindersAsync(ApplicationDbContext db, INotificationService notifications, IUserDirectory directory)
    {
        var now = DateTime.Now;
        var upcoming = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Approved && a.StartTime > now && a.StartTime <= now.AddHours(24))
            .ToListAsync();
        if (upcoming.Count == 0) return;

        var names = await directory.GetDisplayNamesAsync(upcoming.SelectMany(a => new[] { a.ClientId, a.TrainerId }));

        foreach (var appointment in upcoming)
        {
            var withinOneHour = appointment.StartTime <= now.AddHours(1);
            var key = withinOneHour ? $"appt1h:{appointment.Id}" : $"appt24h:{appointment.Id}";
            var when = withinOneHour ? "in 1 hour" : "tomorrow";
            var link = "/Appointments/Details/" + appointment.Id;

            await notifications.NotifyFromTemplateAsync(appointment.ClientId, NotificationType.AppointmentReminder,
                NotificationTemplateKeys.AppointmentReminder,
                new Dictionary<string, string>
                {
                    ["When"] = when,
                    ["OtherName"] = names[appointment.TrainerId],
                    ["Start"] = NotificationFormat.When(appointment.StartTime)
                }, link, key);

            await notifications.NotifyFromTemplateAsync(appointment.TrainerId, NotificationType.AppointmentReminder,
                NotificationTemplateKeys.AppointmentReminder,
                new Dictionary<string, string>
                {
                    ["When"] = when,
                    ["OtherName"] = names[appointment.ClientId],
                    ["Start"] = NotificationFormat.When(appointment.StartTime)
                }, link, key);
        }
    }

    private static async Task SendWorkoutAndMealRemindersAsync(ApplicationDbContext db, INotificationService notifications)
    {
        var today = DateTime.Today;
        var nowTime = DateTime.Now.TimeOfDay;
        var dateKey = today.ToString("yyyyMMdd");

        var clientIds = await db.ClientProfiles.AsNoTracking().Select(c => c.UserId).ToListAsync();
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .Where(p => clientIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId);

        var activeWorkoutPlans = await db.PlanAssignments.AsNoTracking()
            .Where(a => a.WorkoutPlanId != null && a.IsActive && a.StartDate <= today && (a.EndDate == null || a.EndDate >= today))
            .Select(a => new { a.ClientId, Title = a.WorkoutPlan!.Title })
            .ToListAsync();
        var workedOutToday = (await db.WorkoutLogs.AsNoTracking().Where(w => w.LogDate == today).Select(w => w.ClientId).ToListAsync()).ToHashSet();
        var ateToday = (await db.MealLogs.AsNoTracking().Where(m => m.LogDate == today).Select(m => m.ClientId).ToListAsync()).ToHashSet();

        foreach (var clientId in clientIds)
        {
            var prefs = preferences.TryGetValue(clientId, out var p) ? p : new NotificationPreference { UserId = clientId };

            var plan = activeWorkoutPlans.FirstOrDefault(a => a.ClientId == clientId);
            if (plan != null && prefs.WorkoutReminders && nowTime >= prefs.WorkoutReminderTime && !workedOutToday.Contains(clientId))
            {
                await notifications.NotifyFromTemplateAsync(clientId, NotificationType.WorkoutReminder,
                    NotificationTemplateKeys.WorkoutReminder,
                    new Dictionary<string, string> { ["PlanTitle"] = plan.Title },
                    "/WorkoutLogs/Create", "workout:" + dateKey);
            }

            if (prefs.MealReminders && nowTime >= prefs.MealReminderTime && !ateToday.Contains(clientId))
            {
                await notifications.NotifyFromTemplateAsync(clientId, NotificationType.MealReminder,
                    NotificationTemplateKeys.MealReminder, new Dictionary<string, string>(),
                    "/MealLogs/Create", "meal:" + dateKey);
            }
        }
    }

    private static async Task SendTrainerAlertsAsync(ApplicationDbContext db, INotificationService notifications,
        IAnalyticsService analytics, ISettingsService settings, IUserDirectory directory)
    {
        var today = DateTime.Today;
        var dateKey = today.ToString("yyyyMMdd");
        var threshold = await settings.GetIntAsync(SettingKeys.AdherenceAlertThreshold);

        var assignments = await db.ClientProfiles.AsNoTracking().Where(c => c.TrainerId != null)
            .Select(c => new { c.UserId, TrainerId = c.TrainerId! }).ToListAsync();
        if (assignments.Count == 0) return;

        var clientIds = assignments.Select(a => a.UserId).ToList();
        var names = await directory.GetDisplayNamesAsync(clientIds);

        // Low adherence alerts (evaluated after midday so the day has had a chance to happen).
        if (DateTime.Now.Hour >= 12)
        {
            var adherence = await analytics.GetAdherenceAsync(clientIds, today.AddDays(-6), today);
            foreach (var a in assignments)
            {
                var percent = adherence[a.UserId].WorkoutPercent;
                if (percent.HasValue && percent.Value < threshold)
                {
                    await notifications.NotifyFromTemplateAsync(a.TrainerId, NotificationType.AdherenceAlert,
                        NotificationTemplateKeys.AdherenceAlert,
                        new Dictionary<string, string>
                        {
                            ["ClientName"] = names[a.UserId],
                            ["Percent"] = percent.Value.ToString("0"),
                            ["Threshold"] = threshold.ToString()
                        },
                        "/Trainer/Client/" + a.UserId, $"adherence:{a.UserId}:{dateKey}");
                }
            }
        }

        // Daily digest of client log entries for trainers who chose that frequency.
        if (DateTime.Now.Hour >= 20)
        {
            var trainerIds = assignments.Select(a => a.TrainerId).Distinct().ToList();
            var digestTrainers = await db.NotificationPreferences.AsNoTracking()
                .Where(p => trainerIds.Contains(p.UserId) && p.ClientLogAlerts == ClientLogAlertFrequency.DailyDigest)
                .Select(p => p.UserId).ToListAsync();
            // Trainers without a preference row use the default (daily digest).
            var withPreferences = await db.NotificationPreferences.AsNoTracking()
                .Where(p => trainerIds.Contains(p.UserId)).Select(p => p.UserId).ToListAsync();
            digestTrainers.AddRange(trainerIds.Except(withPreferences));

            var workouts = await db.WorkoutLogs.AsNoTracking().Where(w => w.LogDate == today).Select(w => w.ClientId).ToListAsync();
            var meals = await db.MealLogs.AsNoTracking().Where(m => m.LogDate == today).Select(m => m.ClientId).ToListAsync();
            var measurements = await db.HealthMeasurements.AsNoTracking().Where(m => m.MeasuredOn == today).Select(m => m.ClientId).ToListAsync();
            var allLogs = workouts.Concat(meals).Concat(measurements).ToList();

            foreach (var trainerId in digestTrainers.Distinct())
            {
                var myClients = assignments.Where(a => a.TrainerId == trainerId).Select(a => a.UserId).ToHashSet();
                var count = allLogs.Count(myClients.Contains);
                if (count == 0) continue;

                await notifications.NotifyFromTemplateAsync(trainerId, NotificationType.ClientLog,
                    NotificationTemplateKeys.ClientDailyDigest,
                    new Dictionary<string, string> { ["Count"] = count.ToString() },
                    "/Trainer/Dashboard", "digest:" + dateKey);
            }
        }
    }
}
