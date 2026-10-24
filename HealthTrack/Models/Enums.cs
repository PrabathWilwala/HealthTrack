using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Models;

/// <summary>Effort level used by workout plans and workout logs.</summary>
public enum IntensityLevel
{
    Low = 1,
    Moderate = 2,
    High = 3
}

/// <summary>Meal slots used by diet plans and meal logs.</summary>
public enum MealType
{
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
    Snack = 4
}

/// <summary>Lifecycle of an appointment between a client and a trainer.</summary>
public enum AppointmentStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Cancelled = 3,
    Completed = 4
}

/// <summary>Which kind of client record a piece of trainer feedback refers to.</summary>
public enum FeedbackTarget
{
    General = 0,
    Workout = 1,
    Meal = 2,
    Measurement = 3
}

/// <summary>Category of an in-app notification. Used to apply user preferences.</summary>
public enum NotificationType
{
    [Display(Name = "Workout reminder")] WorkoutReminder = 1,
    [Display(Name = "Meal reminder")] MealReminder = 2,
    [Display(Name = "Appointment reminder")] AppointmentReminder = 3,
    [Display(Name = "Appointment update")] AppointmentUpdate = 4,
    [Display(Name = "Plan update")] PlanUpdate = 5,
    [Display(Name = "Feedback")] Feedback = 6,
    [Display(Name = "Client assignment")] ClientAssignment = 7,
    [Display(Name = "Client activity")] ClientLog = 8,
    [Display(Name = "Adherence alert")] AdherenceAlert = 9,
    [Display(Name = "System alert")] SystemAlert = 10,
    [Display(Name = "Security alert")] SecurityAlert = 11,
    [Display(Name = "Pending approval")] ApprovalPending = 12,
    [Display(Name = "Announcement")] Broadcast = 13
}

/// <summary>How often a trainer wants to hear about client log entries.</summary>
public enum ClientLogAlertFrequency
{
    [Display(Name = "Off")] Off = 0,
    [Display(Name = "Every new log")] EveryLog = 1,
    [Display(Name = "Daily summary")] DailyDigest = 2
}
