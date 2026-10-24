using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

// Trainer dashboard, client monitoring, feedback and availability management.
[Authorize(Roles = RoleNames.Trainer)]
public class TrainerController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAnalyticsService _analytics;
    private readonly IUserDirectory _directory;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;
    private readonly UserManager<IdentityUser> _userManager;

    public TrainerController(ApplicationDbContext db, IAnalyticsService analytics, IUserDirectory directory,
        INotificationService notifications, IAuditService audit, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _analytics = analytics;
        _directory = directory;
        _notifications = notifications;
        _audit = audit;
        _userManager = userManager;
    }

    // Trainer dashboard: client overview, pending requests, alerts, activity feed, notifications and metrics.
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        var trainerId = User.GetUserId();
        var clientIds = await _directory.GetClientIdsForTrainerAsync(trainerId);
        var today = DateTime.Today;
        var weekStart = DateHelpers.StartOfWeek(today);
        var monthStart = DateHelpers.StartOfMonth(today);

        var summaries = await _analytics.GetClientSummariesAsync(clientIds);
        var model = new TrainerDashboardViewModel
        {
            FullName = await _directory.GetDisplayNameAsync(trainerId),
            Clients = summaries,
            Alerts = await _analytics.GetTrendAlertsAsync(clientIds),
            AverageAdherence = Average(summaries.Select(s => s.AdherencePercent)),
            AverageWeightChange = Average(summaries.Select(s => s.WeightChange30Days)),
            ActiveClients7Days = summaries.Count(s => s.LastActiveUtc >= DateTime.UtcNow.AddDays(-7))
        };

        // Appointments needing attention and the next confirmed sessions.
        var now = DateTime.Now;
        var pending = await _db.Appointments.AsNoTracking()
            .Where(a => a.TrainerId == trainerId && a.StartTime >= now &&
                        (a.Status == AppointmentStatus.Pending || (a.ProposedStartTime != null && a.RescheduleRequestedBy == RoleNames.Client)))
            .OrderBy(a => a.StartTime).Take(8).ToListAsync();
        var upcoming = await _db.Appointments.AsNoTracking()
            .Where(a => a.TrainerId == trainerId && a.Status == AppointmentStatus.Approved && a.StartTime >= now)
            .OrderBy(a => a.StartTime).Take(5).ToListAsync();
        var names = await _directory.GetDisplayNamesAsync(pending.Concat(upcoming).Select(a => a.ClientId));
        model.PendingRequests = pending.Select(a => new AppointmentListItem(a, names[a.ClientId])).ToList();
        model.UpcomingSessions = upcoming.Select(a => new AppointmentListItem(a, names[a.ClientId])).ToList();

        model.SessionsCompletedThisMonth = await _db.Appointments.CountAsync(a =>
            a.TrainerId == trainerId && a.Status == AppointmentStatus.Completed && a.StartTime >= monthStart);
        model.ActivePlans = await _db.PlanAssignments.CountAsync(a =>
            clientIds.Contains(a.ClientId) && a.IsActive && (a.EndDate == null || a.EndDate >= today));

        // Recent client activity feed.
        var clientNames = await _directory.GetDisplayNamesAsync(clientIds);
        var since = today.AddDays(-7);
        var workouts = await _db.WorkoutLogs.AsNoTracking().Where(w => clientIds.Contains(w.ClientId) && w.LogDate >= since)
            .OrderByDescending(w => w.CreatedAtUtc).Take(15).ToListAsync();
        var meals = await _db.MealLogs.AsNoTracking().Where(m => clientIds.Contains(m.ClientId) && m.LogDate >= since)
            .OrderByDescending(m => m.CreatedAtUtc).Take(15).ToListAsync();
        var measurements = await _db.HealthMeasurements.AsNoTracking().Where(m => clientIds.Contains(m.ClientId) && m.MeasuredOn >= since)
            .OrderByDescending(m => m.CreatedAtUtc).Take(10).ToListAsync();

        model.LogsThisWeek = await _db.WorkoutLogs.CountAsync(w => clientIds.Contains(w.ClientId) && w.LogDate >= weekStart)
                           + await _db.MealLogs.CountAsync(m => clientIds.Contains(m.ClientId) && m.LogDate >= weekStart)
                           + await _db.HealthMeasurements.CountAsync(m => clientIds.Contains(m.ClientId) && m.MeasuredOn >= weekStart);

        model.RecentActivity = workouts
            .Select(w => new ActivityItem(clientNames[w.ClientId], "bi-activity", $"{w.ExerciseType} · {w.DurationMinutes} min · {w.CaloriesBurned} kcal", w.CreatedAtUtc, $"/Trainer/Client/{w.ClientId}#workouts"))
            .Concat(meals.Select(m => new ActivityItem(clientNames[m.ClientId], "bi-egg-fried", $"{m.MealType}: {m.FoodItems} · {m.Calories} kcal", m.CreatedAtUtc, $"/Trainer/Client/{m.ClientId}#meals")))
            .Concat(measurements.Select(m => new ActivityItem(clientNames[m.ClientId], "bi-heart-pulse", $"Weight {m.WeightKg:0.0} kg · BMI {m.Bmi:0.0}", m.CreatedAtUtc, $"/Trainer/Client/{m.ClientId}#measurements")))
            .OrderByDescending(a => a.WhenUtc).Take(15).ToList();

        model.Notifications = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == trainerId).OrderByDescending(n => n.CreatedAtUtc).Take(6).ToListAsync();

        model.AdherenceChart.Labels = summaries.Select(s => s.Name).ToList();
        model.AdherenceChart.Datasets.Add(new ChartDataset { Label = "Workout adherence %", Data = summaries.Select(s => s.WorkoutAdherence).ToList() });
        model.AdherenceChart.Datasets.Add(new ChartDataset { Label = "Diet adherence %", Data = summaries.Select(s => s.DietAdherence).ToList() });

        return View(model);
    }

    // All assigned clients with progress summaries.
    [HttpGet]
    public async Task<IActionResult> Clients(string? search)
    {
        var trainerId = User.GetUserId();
        var clientIds = await _directory.GetClientIdsForTrainerAsync(trainerId);
        var rows = await _analytics.GetClientSummariesAsync(clientIds);
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(r => r.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        ViewData["Search"] = search;
        return View(rows);
    }

    // Detailed view of one client: charts, logs, plans, feedback and appointments.
    [HttpGet("Trainer/Client/{id}")]
    public async Task<IActionResult> Client(string id)
    {
        var trainerId = User.GetUserId();
        var profile = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == id && c.TrainerId == trainerId);
        if (profile == null) return NotFound();

        var user = await _userManager.FindByIdAsync(id);
        var today = DateTime.Today;

        var model = new TrainerClientViewModel
        {
            Profile = profile,
            Email = user?.Email,
            Phone = user?.PhoneNumber,
            Summary = (await _analytics.GetClientSummariesAsync(new[] { id })).First(),
            Progress = await _analytics.GetClientProgressAsync(id, today.AddDays(-29), today, ReportGrouping.Day),
            ActiveAssignments = await _db.PlanAssignments.AsNoTracking().Include(a => a.WorkoutPlan).Include(a => a.DietPlan)
                .Where(a => a.ClientId == id && a.IsActive).ToListAsync(),
            RecentWorkouts = await _db.WorkoutLogs.AsNoTracking().Where(w => w.ClientId == id)
                .OrderByDescending(w => w.LogDate).ThenByDescending(w => w.Id).Take(20).ToListAsync(),
            RecentMeals = await _db.MealLogs.AsNoTracking().Where(m => m.ClientId == id)
                .OrderByDescending(m => m.LogDate).ThenBy(m => m.MealType).Take(20).ToListAsync(),
            RecentMeasurements = await _db.HealthMeasurements.AsNoTracking().Where(m => m.ClientId == id)
                .OrderByDescending(m => m.MeasuredOn).ThenByDescending(m => m.Id).Take(15).ToListAsync(),
            Feedback = await _db.TrainerFeedback.AsNoTracking().Where(f => f.ClientId == id)
                .OrderByDescending(f => f.CreatedAtUtc).Take(20).ToListAsync(),
            Appointments = await _db.Appointments.AsNoTracking().Where(a => a.ClientId == id && a.TrainerId == trainerId)
                .OrderByDescending(a => a.StartTime).Take(10).ToListAsync(),
            FeedbackForm = new FeedbackFormViewModel { ClientId = id }
        };

        await _audit.LogAsync("Trainer", "Client.Viewed", $"Viewed client record {profile.FullName}");
        return View(model);
    }

    // Saves feedback on a client log (or general feedback) and notifies the client.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GiveFeedback(FeedbackFormViewModel model)
    {
        var trainerId = User.GetUserId();
        var isMyClient = await _db.ClientProfiles.AnyAsync(c => c.UserId == model.ClientId && c.TrainerId == trainerId);
        if (!isMyClient) return NotFound();

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please write a message between 2 and 1000 characters.";
            return Redirect($"/Trainer/Client/{model.ClientId}#feedback");
        }

        // A target id must belong to this client.
        if (model.TargetId.HasValue && !await TargetBelongsToClientAsync(model.Target, model.TargetId.Value, model.ClientId!))
        {
            model.Target = FeedbackTarget.General;
            model.TargetId = null;
        }

        var feedback = new TrainerFeedback
        {
            TrainerId = trainerId,
            ClientId = model.ClientId!,
            Target = model.Target,
            TargetId = model.TargetId,
            Message = model.Message!.Trim()
        };
        _db.TrainerFeedback.Add(feedback);
        await _db.SaveChangesAsync();

        await _notifications.NotifyFromTemplateAsync(model.ClientId!, NotificationType.Feedback,
            NotificationTemplateKeys.FeedbackReceived,
            new Dictionary<string, string>
            {
                ["TrainerName"] = await _directory.GetDisplayNameAsync(trainerId),
                ["Message"] = feedback.Message.Length > 140 ? feedback.Message.Substring(0, 140) + "…" : feedback.Message
            },
            "/Client/Feedback");

        await _audit.LogAsync("Feedback", "Feedback.Given", $"Feedback for {model.ClientId} on {model.Target}");
        TempData["Success"] = "Feedback sent to the client.";
        return Redirect($"/Trainer/Client/{model.ClientId}#feedback");
    }

    // Ends an active plan assignment for one of the trainer's clients.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAssignment(int id)
    {
        var trainerId = User.GetUserId();
        var assignment = await _db.PlanAssignments.FirstOrDefaultAsync(a => a.Id == id);
        if (assignment == null || !await _db.ClientProfiles.AnyAsync(c => c.UserId == assignment.ClientId && c.TrainerId == trainerId))
            return NotFound();

        assignment.IsActive = false;
        assignment.EndDate = DateTime.Today;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Plans", "Assignment.Ended", $"Assignment #{id}");
        TempData["Success"] = "The plan assignment has been ended.";
        return Redirect($"/Trainer/Client/{assignment.ClientId}");
    }

    // =====================================================================
    // Availability
    // =====================================================================

    // Shows / adds weekly consultation hours.
    [HttpGet]
    public async Task<IActionResult> Availability()
    {
        var trainerId = User.GetUserId();
        return View(new TrainerAvailabilityViewModel
        {
            Slots = await _db.TrainerAvailabilities.AsNoTracking().Where(s => s.TrainerId == trainerId).ToListAsync()
        });
    }

    // Shows / adds weekly consultation hours.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Availability(TrainerAvailabilityViewModel model)
    {
        var trainerId = User.GetUserId();
        var slots = await _db.TrainerAvailabilities.Where(s => s.TrainerId == trainerId).ToListAsync();

        if (model.StartTime.HasValue && model.EndTime.HasValue)
        {
            if (model.EndTime <= model.StartTime)
                ModelState.AddModelError(nameof(model.EndTime), "The end time must be after the start time.");
            else if (slots.Any(s => s.DayOfWeek == model.DayOfWeek && s.StartTime < model.EndTime && model.StartTime < s.EndTime))
                ModelState.AddModelError(nameof(model.StartTime), "This overlaps an existing slot on the same day.");
        }

        if (!ModelState.IsValid)
        {
            model.Slots = slots;
            return View(model);
        }

        _db.TrainerAvailabilities.Add(new TrainerAvailability
        {
            TrainerId = trainerId,
            DayOfWeek = model.DayOfWeek,
            StartTime = model.StartTime!.Value,
            EndTime = model.EndTime!.Value
        });
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Availability added for {model.DayOfWeek}.";
        return RedirectToAction(nameof(Availability));
    }

    // Removes an availability slot.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSlot(int id)
    {
        var trainerId = User.GetUserId();
        var slot = await _db.TrainerAvailabilities.FirstOrDefaultAsync(s => s.Id == id && s.TrainerId == trainerId);
        if (slot != null)
        {
            _db.TrainerAvailabilities.Remove(slot);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Availability slot removed.";
        }
        return RedirectToAction(nameof(Availability));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<bool> TargetBelongsToClientAsync(FeedbackTarget target, int targetId, string clientId) => target switch
    {
        FeedbackTarget.Workout => await _db.WorkoutLogs.AnyAsync(w => w.Id == targetId && w.ClientId == clientId),
        FeedbackTarget.Meal => await _db.MealLogs.AnyAsync(m => m.Id == targetId && m.ClientId == clientId),
        FeedbackTarget.Measurement => await _db.HealthMeasurements.AnyAsync(m => m.Id == targetId && m.ClientId == clientId),
        _ => false
    };

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }
}
