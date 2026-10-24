using System.Net;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

// Default text for every automated notification. Administrators can edit these
// in Admin → Notification templates; the defaults are seeded on first run.
public static class NotificationTemplateKeys
{
    public const string PlanAssigned = "PlanAssigned";
    public const string PlanUpdated = "PlanUpdated";
    public const string FeedbackReceived = "FeedbackReceived";
    public const string AppointmentRequested = "AppointmentRequested";
    public const string AppointmentApproved = "AppointmentApproved";
    public const string AppointmentDeclined = "AppointmentDeclined";
    public const string AppointmentCancelled = "AppointmentCancelled";
    public const string RescheduleRequested = "RescheduleRequested";
    public const string AppointmentRescheduled = "AppointmentRescheduled";
    public const string ConsultationNotesAdded = "ConsultationNotesAdded";
    public const string AppointmentReminder = "AppointmentReminder";
    public const string WorkoutReminder = "WorkoutReminder";
    public const string MealReminder = "MealReminder";
    public const string ClientAssigned = "ClientAssigned";
    public const string TrainerAssigned = "TrainerAssigned";
    public const string ClientLogged = "ClientLogged";
    public const string ClientDailyDigest = "ClientDailyDigest";
    public const string AdherenceAlert = "AdherenceAlert";
    public const string TrainerApprovalPending = "TrainerApprovalPending";
    public const string TrainerApproved = "TrainerApproved";
    public const string SecurityAlert = "SecurityAlert";

    // (Key, Name, Title, Body) for every template.
    public static readonly IReadOnlyList<(string Key, string Name, string Title, string Body)> Defaults = new[]
    {
        (PlanAssigned, "Plan assigned (client)", "New {PlanType} plan: {PlanTitle}", "{TrainerName} assigned you the {PlanType} plan \"{PlanTitle}\" starting {StartDate}."),
        (PlanUpdated, "Plan updated (client)", "Your {PlanType} plan was updated", "{TrainerName} updated \"{PlanTitle}\". Please review the latest version."),
        (FeedbackReceived, "Trainer feedback (client)", "New feedback from {TrainerName}", "{TrainerName}: \"{Message}\""),
        (AppointmentRequested, "Appointment requested (trainer)", "New appointment request", "{ClientName} requested a consultation on {Start}. Purpose: {Purpose}"),
        (AppointmentApproved, "Appointment approved (client)", "Appointment confirmed", "Your consultation with {TrainerName} on {Start} has been approved."),
        (AppointmentDeclined, "Appointment declined (client)", "Appointment request declined", "{TrainerName} declined your request for {Start}. Reason: {Reason}"),
        (AppointmentCancelled, "Appointment cancelled", "Appointment cancelled", "The consultation on {Start} was cancelled by {CancelledBy}. Reason: {Reason}"),
        (RescheduleRequested, "Reschedule requested", "New time proposed", "{RequesterName} proposed moving the consultation on {Start} to {ProposedStart}."),
        (AppointmentRescheduled, "Appointment rescheduled", "Appointment rescheduled", "The consultation has been moved to {Start}."),
        (ConsultationNotesAdded, "Consultation notes (client)", "Consultation notes available", "{TrainerName} added notes for your consultation on {Start}."),
        (AppointmentReminder, "Appointment reminder", "Upcoming appointment {When}", "Reminder: consultation with {OtherName} on {Start}."),
        (WorkoutReminder, "Workout reminder (client)", "Time for your workout", "You have not logged a workout today. Your plan \"{PlanTitle}\" is waiting!"),
        (MealReminder, "Meal reminder (client)", "Remember to log your meals", "You have not logged any meals today. Keep your diet schedule on track."),
        (ClientAssigned, "Client assigned (trainer)", "New client assigned", "{ClientName} has been assigned to you by an administrator."),
        (TrainerAssigned, "Trainer assigned (client)", "Your trainer: {TrainerName}", "{TrainerName} is now your trainer. You can book consultations from Appointments."),
        (ClientLogged, "Client logged activity (trainer)", "{ClientName} logged a {LogType}", "{ClientName} logged: {Summary}"),
        (ClientDailyDigest, "Client daily summary (trainer)", "Today's client activity", "Your clients logged {Count} entries today."),
        (AdherenceAlert, "Low adherence (trainer)", "Low adherence: {ClientName}", "{ClientName}'s workout adherence over the last 7 days is {Percent}% (threshold {Threshold}%)."),
        (TrainerApprovalPending, "Trainer awaiting approval (admin)", "Trainer awaiting approval", "{TrainerName} registered as a trainer and is waiting for approval."),
        (TrainerApproved, "Trainer approved (trainer)", "Your trainer account is approved", "Welcome to HealthTrack! You can now sign in and manage clients."),
        (SecurityAlert, "Security alert (admin)", "Security alert: {Subject}", "{Details}")
    };
}

public interface INotificationService
{
    Task NotifyAsync(string userId, NotificationType type, string title, string message,
        string? linkUrl = null, string? dedupKey = null);

    Task NotifyFromTemplateAsync(string userId, NotificationType type, string templateKey,
        IDictionary<string, string> tokens, string? linkUrl = null, string? dedupKey = null);

    Task NotifyRoleAsync(string role, NotificationType type, string templateKey,
        IDictionary<string, string> tokens, string? linkUrl = null, string? dedupKey = null);

    Task<int> BroadcastAsync(string? role, string title, string message, string? linkUrl);

    Task<NotificationPreference> GetPreferencesAsync(string userId);

    Task NotifyTrainerOfClientLogAsync(string clientId, string logType, string summary);

    Task<int> GetUnreadCountAsync(string userId);
}

// Creates in-app notifications (and optional emails) while honouring each user's preferences.
public sealed class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IAppEmailSender _emailSender;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(ApplicationDbContext db, UserManager<IdentityUser> userManager,
        IAppEmailSender emailSender, ILogger<NotificationService> logger)
    {
        _db = db;
        _userManager = userManager;
        _emailSender = emailSender;
        _logger = logger;
    }

    // Creates an in-app notification if the user's preferences allow it (and emails it when enabled). Dedup keys prevent duplicate reminders.
    public async Task NotifyAsync(string userId, NotificationType type, string title, string message,
        string? linkUrl = null, string? dedupKey = null)
    {
        try
        {
            var preferences = await GetPreferencesAsync(userId);
            if (!preferences.Allows(type))
                return;

            // Automated reminders carry a dedup key so they are only ever sent once.
            if (dedupKey != null &&
                await _db.Notifications.AnyAsync(n => n.UserId == userId && n.DedupKey == dedupKey))
                return;

            _db.Notifications.Add(new Notification
            {
                UserId = userId,
                Type = type,
                Title = Limit(title, 150),
                Message = Limit(message, 1000),
                LinkUrl = linkUrl,
                DedupKey = dedupKey,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            if (preferences.EmailNotifications)
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (!string.IsNullOrEmpty(user?.Email))
                {
                    var body = $"<p>{WebUtility.HtmlEncode(message)}</p><p style='color:#667085'>You receive this email because email notifications are enabled in your HealthTrack preferences.</p>";
                    await _emailSender.SendAsync(user.Email, "HealthTrack: " + title, body);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed notification must not roll back the business action that triggered it.
            _logger.LogError(ex, "Could not create notification '{Title}' for user {UserId}", title, userId);
        }
    }

    // Renders an administrator-editable template with tokens and sends it as a notification.
    public async Task NotifyFromTemplateAsync(string userId, NotificationType type, string templateKey,
        IDictionary<string, string> tokens, string? linkUrl = null, string? dedupKey = null)
    {
        var (title, body) = await RenderTemplateAsync(templateKey, tokens);
        await NotifyAsync(userId, type, title, body, linkUrl, dedupKey);
    }

    // Sends a templated notification to every user in a role (e.g. all administrators).
    public async Task NotifyRoleAsync(string role, NotificationType type, string templateKey,
        IDictionary<string, string> tokens, string? linkUrl = null, string? dedupKey = null)
    {
        var users = await _userManager.GetUsersInRoleAsync(role);
        foreach (var user in users)
            await NotifyFromTemplateAsync(user.Id, type, templateKey, tokens, linkUrl, dedupKey);
    }

    // Sends an announcement to every user, or every user in one role.
    public async Task<int> BroadcastAsync(string? role, string title, string message, string? linkUrl)
    {
        List<string> userIds;
        if (string.IsNullOrEmpty(role))
            userIds = await _db.Users.Select(u => u.Id).ToListAsync();
        else
            userIds = (await _userManager.GetUsersInRoleAsync(role)).Select(u => u.Id).ToList();

        foreach (var id in userIds)
        {
            _db.Notifications.Add(new Notification
            {
                UserId = id,
                Type = NotificationType.Broadcast,
                Title = Limit(title, 150),
                Message = Limit(message, 1000),
                LinkUrl = linkUrl,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
        return userIds.Count;
    }

    // Returns the user's notification preferences, or the defaults if none were saved.
    public async Task<NotificationPreference> GetPreferencesAsync(string userId)
    {
        return await _db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId)
               ?? new NotificationPreference { UserId = userId };
    }

    // Immediate alert to the client's trainer, only if they chose "every new log".
    public async Task NotifyTrainerOfClientLogAsync(string clientId, string logType, string summary)
    {
        var client = await _db.ClientProfiles.AsNoTracking()
            .Where(c => c.UserId == clientId)
            .Select(c => new { c.FullName, c.TrainerId })
            .FirstOrDefaultAsync();
        if (client?.TrainerId == null)
            return;

        var trainerPreferences = await GetPreferencesAsync(client.TrainerId);
        if (trainerPreferences.ClientLogAlerts != ClientLogAlertFrequency.EveryLog)
            return;

        await NotifyFromTemplateAsync(client.TrainerId, NotificationType.ClientLog, NotificationTemplateKeys.ClientLogged,
            new Dictionary<string, string> { ["ClientName"] = client.FullName, ["LogType"] = logType, ["Summary"] = summary },
            "/Trainer/Client/" + clientId);
    }

    // Counts the user's unread notifications (navigation bell badge).
    public Task<int> GetUnreadCountAsync(string userId) =>
        _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    // Loads the editable template (or the built-in default) and replaces {Tokens}.
    private async Task<(string Title, string Body)> RenderTemplateAsync(string key, IDictionary<string, string> tokens)
    {
        var template = await _db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Key == key);
        string title, body;
        if (template != null)
        {
            title = template.TitleTemplate;
            body = template.BodyTemplate;
        }
        else
        {
            var fallback = NotificationTemplateKeys.Defaults.FirstOrDefault(d => d.Key == key);
            title = fallback.Title ?? key;
            body = fallback.Body ?? string.Empty;
        }

        foreach (var token in tokens)
        {
            title = title.Replace("{" + token.Key + "}", token.Value);
            body = body.Replace("{" + token.Key + "}", token.Value);
        }
        return (title, body);
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 1) + "…";
}

// Formatting helpers used when building notification tokens.
public static class NotificationFormat
{
    // Formats a local date/time for notification text.
    public static string When(DateTime local) => local.ToString("ddd dd MMM yyyy, HH:mm");
}
