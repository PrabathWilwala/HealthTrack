using System.ComponentModel.DataAnnotations;
using HealthTrack.Models;
using HealthTrack.Services;

namespace HealthTrack.ViewModels;

/// <summary>One entry in an activity feed (client logs, system events).</summary>
public sealed record ActivityItem(string Who, string Icon, string Description, DateTime WhenUtc, string? Link);

public class TrainerDashboardViewModel
{
    public string FullName { get; set; } = string.Empty;
    public List<ClientSummaryRow> Clients { get; set; } = new();
    public List<AppointmentListItem> PendingRequests { get; set; } = new();
    public List<AppointmentListItem> UpcomingSessions { get; set; } = new();
    public List<ActivityItem> RecentActivity { get; set; } = new();
    public List<Notification> Notifications { get; set; } = new();
    public List<TrendAlert> Alerts { get; set; } = new();

    public decimal? AverageAdherence { get; set; }
    public int ActiveClients7Days { get; set; }
    public int LogsThisWeek { get; set; }
    public int SessionsCompletedThisMonth { get; set; }
    public int ActivePlans { get; set; }
    public decimal? AverageWeightChange { get; set; }

    public ChartData AdherenceChart { get; set; } = new();
}

public class TrainerClientViewModel
{
    public ClientProfile Profile { get; set; } = new();
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public ClientSummaryRow Summary { get; set; } = new();
    public ClientProgressReport Progress { get; set; } = new();
    public List<PlanAssignment> ActiveAssignments { get; set; } = new();
    public List<WorkoutLog> RecentWorkouts { get; set; } = new();
    public List<MealLog> RecentMeals { get; set; } = new();
    public List<HealthMeasurement> RecentMeasurements { get; set; } = new();
    public List<TrainerFeedback> Feedback { get; set; } = new();
    public List<Appointment> Appointments { get; set; } = new();
    public FeedbackFormViewModel FeedbackForm { get; set; } = new();
}

public class FeedbackFormViewModel
{
    [Required]
    public string? ClientId { get; set; }

    public FeedbackTarget Target { get; set; } = FeedbackTarget.General;

    public int? TargetId { get; set; }

    [Required, StringLength(1000, MinimumLength = 2)]
    [Display(Name = "Feedback")]
    public string? Message { get; set; }
}
