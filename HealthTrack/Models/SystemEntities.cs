using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Models;

/// <summary>An in-app notification shown in the notification centre.</summary>
public class Notification
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string UserId { get; set; } = string.Empty;

    public NotificationType Type { get; set; }

    [Required, StringLength(150)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(1000)]
    public string Message { get; set; } = string.Empty;

    [StringLength(300)]
    public string? LinkUrl { get; set; }

    public bool IsRead { get; set; }

    /// <summary>Optional key that prevents the same automated reminder being sent twice.</summary>
    [StringLength(150)]
    public string? DedupKey { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-user notification settings. A missing row means "use the defaults".</summary>
public class NotificationPreference
{
    [Required, StringLength(450)]
    public string UserId { get; set; } = string.Empty;

    public bool WorkoutReminders { get; set; } = true;
    public bool MealReminders { get; set; } = true;
    public bool AppointmentAlerts { get; set; } = true;
    public bool PlanUpdates { get; set; } = true;
    public bool FeedbackAlerts { get; set; } = true;
    public bool AdherenceAlerts { get; set; } = true;
    public bool SystemAlerts { get; set; } = true;

    public ClientLogAlertFrequency ClientLogAlerts { get; set; } = ClientLogAlertFrequency.DailyDigest;

    /// <summary>Also deliver notifications by email.</summary>
    public bool EmailNotifications { get; set; }

    /// <summary>Time of day after which a workout reminder is sent if nothing was logged.</summary>
    public TimeSpan WorkoutReminderTime { get; set; } = new(18, 0, 0);

    /// <summary>Time of day after which a meal reminder is sent if nothing was logged.</summary>
    public TimeSpan MealReminderTime { get; set; } = new(13, 0, 0);

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Decides whether a notification of the given type should be delivered.</summary>
    public bool Allows(NotificationType type) => type switch
    {
        NotificationType.WorkoutReminder => WorkoutReminders,
        NotificationType.MealReminder => MealReminders,
        NotificationType.AppointmentReminder => AppointmentAlerts,
        NotificationType.AppointmentUpdate => AppointmentAlerts,
        NotificationType.PlanUpdate => PlanUpdates,
        NotificationType.Feedback => FeedbackAlerts,
        NotificationType.ClientLog => ClientLogAlerts != ClientLogAlertFrequency.Off,
        NotificationType.AdherenceAlert => AdherenceAlerts,
        NotificationType.SystemAlert => SystemAlerts,
        NotificationType.ApprovalPending => SystemAlerts,
        // Security alerts, assignments and broadcasts are always delivered.
        _ => true
    };
}

/// <summary>Editable text used for automated notifications. Tokens look like {ClientName}.</summary>
public class NotificationTemplate
{
    public int Id { get; set; }

    [Required, StringLength(60)]
    public string Key { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string TitleTemplate { get; set; } = string.Empty;

    [Required, StringLength(1000)]
    public string BodyTemplate { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Security and activity audit trail entry.</summary>
public class AuditLog
{
    public int Id { get; set; }

    [StringLength(450)]
    public string? UserId { get; set; }

    [StringLength(256)]
    public string? UserName { get; set; }

    /// <summary>Feature area, e.g. "Auth", "Workout", "Admin". Used for usage statistics.</summary>
    [Required, StringLength(50)]
    public string Category { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Action { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Details { get; set; }

    [StringLength(64)]
    public string? IpAddress { get; set; }

    public bool IsSecurityEvent { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Simple key/value store for administrator-managed policies.</summary>
public class SystemSetting
{
    [Required, StringLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required, StringLength(500)]
    public string Value { get; set; } = string.Empty;
}
