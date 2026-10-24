using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Models;

// Effort level used by workout plans and workout logs.
public enum IntensityLevel
{
    Low = 1,
    Moderate = 2,
    High = 3
}

// Meal slots used by diet plans and meal logs.
public enum MealType
{
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
    Snack = 4
}

// Lifecycle of an appointment between a client and a trainer.
public enum AppointmentStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
    Cancelled = 3,
    Completed = 4
}

// Which kind of client record a piece of trainer feedback refers to.
public enum FeedbackTarget
{
    General = 0,
    Workout = 1,
    Meal = 2,
    Measurement = 3
}

// Category of an in-app notification. Used to apply user preferences.
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

// How often a trainer wants to hear about client log entries.
public enum ClientLogAlertFrequency
{
    [Display(Name = "Off")] Off = 0,
    [Display(Name = "Every new log")] EveryLog = 1,
    [Display(Name = "Daily summary")] DailyDigest = 2
}
