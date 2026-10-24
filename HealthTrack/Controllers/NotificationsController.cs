using System.ComponentModel.DataAnnotations;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

public class NotificationCenterViewModel
{
    public string Filter { get; set; } = "all";
    public NotificationType? Type { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<Notification> Items { get; set; } = new();
}

public class NotificationPreferencesViewModel
{
    public string Role { get; set; } = string.Empty;

    [Display(Name = "Workout reminders")] public bool WorkoutReminders { get; set; }
    [Display(Name = "Meal reminders")] public bool MealReminders { get; set; }
    [Display(Name = "Appointment reminders and updates")] public bool AppointmentAlerts { get; set; }
    [Display(Name = "Plan updates")] public bool PlanUpdates { get; set; }
    [Display(Name = "Trainer feedback")] public bool FeedbackAlerts { get; set; }
    [Display(Name = "Low-adherence alerts")] public bool AdherenceAlerts { get; set; }
    [Display(Name = "System alerts and approvals")] public bool SystemAlerts { get; set; }
    [Display(Name = "Client log alerts")] public ClientLogAlertFrequency ClientLogAlerts { get; set; }
    [Display(Name = "Also send notifications by email")] public bool EmailNotifications { get; set; }

    [Display(Name = "Workout reminder after")]
    public TimeSpan WorkoutReminderTime { get; set; }

    [Display(Name = "Meal reminder after")]
    public TimeSpan MealReminderTime { get; set; }
}

/// <summary>In-app notification centre and per-user notification preferences.</summary>
[Authorize]
public class NotificationsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;

    public NotificationsController(ApplicationDbContext db, INotificationService notifications, IAuditService audit)
    {
        _db = db;
        _notifications = notifications;
        _audit = audit;
    }

    /// <summary>Notification centre with read/unread filter, type filter and paging.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(string filter = "all", NotificationType? type = null, int page = 1)
    {
        var userId = User.GetUserId();
        var query = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (filter == "unread") query = query.Where(n => !n.IsRead);
        if (type.HasValue) query = query.Where(n => n.Type == type.Value);

        var model = new NotificationCenterViewModel { Filter = filter == "unread" ? "unread" : "all", Type = type, Page = Math.Max(1, page) };
        model.TotalCount = await query.CountAsync();
        model.UnreadCount = await _notifications.GetUnreadCountAsync(userId);
        model.Items = await query.OrderByDescending(n => n.CreatedAtUtc)
            .Skip((model.Page - 1) * model.PageSize).Take(model.PageSize).ToListAsync();
        return View(model);
    }

    /// <summary>Marks a notification as read and follows its link.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Open(int id)
    {
        var userId = User.GetUserId();
        var notification = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (notification == null) return RedirectToAction(nameof(Index));

        notification.IsRead = true;
        await _db.SaveChangesAsync();

        // Only follow relative links inside the application (prevents open redirects).
        if (!string.IsNullOrEmpty(notification.LinkUrl) && Url.IsLocalUrl(notification.LinkUrl))
            return LocalRedirect(notification.LinkUrl);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Marks a notification read or unread.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(int id, bool read = true)
    {
        var userId = User.GetUserId();
        await _db.Notifications.Where(n => n.Id == id && n.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, read));
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Marks every notification as read.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = User.GetUserId();
        var count = await _db.Notifications.Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        TempData["Success"] = count == 0 ? "No unread notifications." : $"{count} notification(s) marked as read.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Deletes a notification.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = User.GetUserId();
        await _db.Notifications.Where(n => n.Id == id && n.UserId == userId).ExecuteDeleteAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Lightweight JSON endpoint polled by the navigation bar badge.</summary>
    [HttpGet]
    public async Task<IActionResult> UnreadCount()
    {
        return Json(new { count = await _notifications.GetUnreadCountAsync(User.GetUserId()) });
    }

    /// <summary>Shows / saves notification preferences.</summary>
    [HttpGet]
    public async Task<IActionResult> Preferences()
    {
        var prefs = await _notifications.GetPreferencesAsync(User.GetUserId());
        return View(new NotificationPreferencesViewModel
        {
            Role = CurrentRole(),
            WorkoutReminders = prefs.WorkoutReminders,
            MealReminders = prefs.MealReminders,
            AppointmentAlerts = prefs.AppointmentAlerts,
            PlanUpdates = prefs.PlanUpdates,
            FeedbackAlerts = prefs.FeedbackAlerts,
            AdherenceAlerts = prefs.AdherenceAlerts,
            SystemAlerts = prefs.SystemAlerts,
            ClientLogAlerts = prefs.ClientLogAlerts,
            EmailNotifications = prefs.EmailNotifications,
            WorkoutReminderTime = prefs.WorkoutReminderTime,
            MealReminderTime = prefs.MealReminderTime
        });
    }

    /// <summary>Shows / saves notification preferences.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preferences(NotificationPreferencesViewModel model)
    {
        var userId = User.GetUserId();
        var prefs = await _db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        if (prefs == null)
        {
            prefs = new NotificationPreference { UserId = userId };
            _db.NotificationPreferences.Add(prefs);
        }

        prefs.WorkoutReminders = model.WorkoutReminders;
        prefs.MealReminders = model.MealReminders;
        prefs.AppointmentAlerts = model.AppointmentAlerts;
        prefs.PlanUpdates = model.PlanUpdates;
        prefs.FeedbackAlerts = model.FeedbackAlerts;
        prefs.AdherenceAlerts = model.AdherenceAlerts;
        prefs.SystemAlerts = model.SystemAlerts;
        prefs.ClientLogAlerts = model.ClientLogAlerts;
        prefs.EmailNotifications = model.EmailNotifications;
        prefs.WorkoutReminderTime = ClampTime(model.WorkoutReminderTime);
        prefs.MealReminderTime = ClampTime(model.MealReminderTime);
        prefs.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("Notifications", "Preferences.Updated");
        TempData["Success"] = "Notification preferences saved.";
        return RedirectToAction(nameof(Preferences));
    }

    private string CurrentRole() =>
        User.IsInRole(RoleNames.Administrator) ? RoleNames.Administrator :
        User.IsInRole(RoleNames.Trainer) ? RoleNames.Trainer : RoleNames.Client;

    private static TimeSpan ClampTime(TimeSpan value) =>
        value < TimeSpan.Zero || value >= TimeSpan.FromDays(1) ? new TimeSpan(12, 0, 0) : value;
}
