using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;

namespace HealthTrack.ViewModels;

public class TrainerWorkloadRow
{
    public string TrainerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Specializations { get; set; } = string.Empty;
    public int Clients { get; set; }
    public int UpcomingAppointments { get; set; }
    public decimal? AverageAdherence { get; set; }
}

public class AdminDashboardViewModel
{
    public int TotalUsers { get; set; }
    public int Clients { get; set; }
    public int Trainers { get; set; }
    public int Administrators { get; set; }
    public int ActiveUsers7Days { get; set; }
    public int NewRegistrations30Days { get; set; }
    public int LoginsToday { get; set; }
    public int FailedLogins24Hours { get; set; }
    public int UnassignedClients { get; set; }
    public int FlaggedPlans { get; set; }
    public int AppointmentConflicts { get; set; }
    public int NotificationsSent24Hours { get; set; }

    public bool DatabaseHealthy { get; set; }
    public long DatabaseLatencyMs { get; set; }
    public TimeSpan Uptime { get; set; }
    public bool CurrentAdminHasTwoFactor { get; set; }

    public List<TrainerProfile> PendingTrainers { get; set; } = new();
    public List<TrainerWorkloadRow> TrainerWorkload { get; set; } = new();
    public List<AuditLog> SecurityAlerts { get; set; } = new();
    public List<AuditLog> RecentActivity { get; set; } = new();

    public ChartData LoginChart { get; set; } = new();
    public ChartData FeatureUsageChart { get; set; } = new();
    public ChartData PeakHoursChart { get; set; } = new();
}

public class UserRow
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public bool IsDeactivated { get; set; }
    public bool IsLockedOut { get; set; }
    public bool? TrainerApproved { get; set; }
    public bool TrainerRejected { get; set; }
    public string? AssignedTrainer { get; set; }
    public DateTime? JoinedUtc { get; set; }
    public DateTime? LastActiveUtc { get; set; }
}

public class UserListViewModel
{
    public string? Search { get; set; }
    public string? Role { get; set; }
    // active, deactivated, pending (trainer approval) or unconfirmed (email).
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 15;
    public int TotalCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<UserRow> Users { get; set; } = new();
}

public class CreateUserViewModel : IValidatableObject
{
    [Required]
    public string? Role { get; set; } = RoleNames.Client;

    [Required, StringLength(100, MinimumLength = 2)]
    [Display(Name = "Full name")]
    public string? FullName { get; set; }

    [Required, StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Use letters, numbers, dots, underscores or hyphens only.")]
    [Display(Name = "Username")]
    public string? UserName { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string? Email { get; set; }

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    [Display(Name = "Initial password")]
    public string? Password { get; set; }

    // Client
    [Range(1, 120)] public int? Age { get; set; }
    [StringLength(30)] public string? Gender { get; set; }
    [Range(typeof(decimal), "50", "300")] [Display(Name = "Height (cm)")] public decimal? HeightCm { get; set; }
    [Range(typeof(decimal), "10", "500")] [Display(Name = "Weight (kg)")] public decimal? WeightKg { get; set; }
    [StringLength(1000)] [Display(Name = "Fitness goals")] public string? FitnessGoals { get; set; }

    // Trainer
    [StringLength(500)] public string? Qualifications { get; set; }
    [StringLength(500)] public string? Specializations { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!RoleNames.All.Contains(Role))
            yield return new ValidationResult("Choose a valid role.", new[] { nameof(Role) });

        if (Role == RoleNames.Client)
        {
            if (!Age.HasValue) yield return new ValidationResult("Age is required for clients.", new[] { nameof(Age) });
            if (string.IsNullOrWhiteSpace(Gender)) yield return new ValidationResult("Gender is required for clients.", new[] { nameof(Gender) });
            if (!HeightCm.HasValue) yield return new ValidationResult("Height is required for clients.", new[] { nameof(HeightCm) });
            if (!WeightKg.HasValue) yield return new ValidationResult("Weight is required for clients.", new[] { nameof(WeightKg) });
        }
        else if (Role == RoleNames.Trainer)
        {
            if (string.IsNullOrWhiteSpace(Qualifications)) yield return new ValidationResult("Qualifications are required for trainers.", new[] { nameof(Qualifications) });
            if (string.IsNullOrWhiteSpace(Specializations)) yield return new ValidationResult("Specializations are required for trainers.", new[] { nameof(Specializations) });
        }
    }
}

public class EditUserViewModel
{
    public string Id { get; set; } = string.Empty;

    [Required, StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Use letters, numbers, dots, underscores or hyphens only.")]
    [Display(Name = "Username")]
    public string? UserName { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string? Email { get; set; }

    [Phone, StringLength(30)]
    [Display(Name = "Phone number")]
    public string? PhoneNumber { get; set; }

    [StringLength(100)]
    [Display(Name = "Full name")]
    public string? FullName { get; set; }

    [Required]
    public string? Role { get; set; }

    [Display(Name = "Email confirmed")]
    public bool EmailConfirmed { get; set; }

    public string CurrentRole { get; set; } = string.Empty;
    public bool IsDeactivated { get; set; }
    public bool TwoFactorEnabled { get; set; }
}

public class AdminSetPasswordViewModel
{
    public string Id { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    [Display(Name = "New password")]
    public string? NewPassword { get; set; }
}

public class AssignTrainerViewModel
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? FitnessGoals { get; set; }
    public string? CurrentTrainerId { get; set; }

    [Display(Name = "Trainer")]
    public string? TrainerId { get; set; }

    public List<TrainerOption> Trainers { get; set; } = new();
    // Trainer ids whose specialisations share words with the client's goals.
    public List<string> SuggestedTrainerIds { get; set; } = new();
}

public class UserActivityViewModel
{
    public UserRow User { get; set; } = new();
    public List<AuditLog> Entries { get; set; } = new();
    public int Workouts { get; set; }
    public int Meals { get; set; }
    public int Measurements { get; set; }
    public int Appointments { get; set; }
}

public class AdminPlanRow
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsTemplate { get; set; }
    public bool IsSystemTemplate { get; set; }
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
    public int Items { get; set; }
    public int TimesAssigned { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class AdminPlansViewModel
{
    public string? Filter { get; set; }
    public List<AdminPlanRow> Plans { get; set; } = new();
    public List<RoutineStatsRow> Popular { get; set; } = new();
}

public class AdminAppointmentsViewModel
{
    public AppointmentStatus? Status { get; set; }
    public string? TrainerId { get; set; }

    [DataType(DataType.Date)]
    public DateTime From { get; set; }

    [DataType(DataType.Date)]
    public DateTime To { get; set; }

    public List<(Appointment Appointment, string Client, string Trainer)> Items { get; set; } = new();
    public List<(AppointmentConflict Conflict, string Trainer)> Conflicts { get; set; } = new();
    public List<TrainerOption> Trainers { get; set; } = new();

    public int Total { get; set; }
    public decimal? CancellationRate { get; set; }
    public ChartData VolumeChart { get; set; } = new();
    public ChartData StatusChart { get; set; } = new();
    public ChartData HoursChart { get; set; } = new();
    public ChartData WeekdayChart { get; set; } = new();
    public PolicyViewModel Policy { get; set; } = new();
}

public class PolicyViewModel
{
    [Range(0, 168)]
    [Display(Name = "Minimum booking notice (hours)")]
    public int MinBookingNoticeHours { get; set; }

    [Range(0, 168)]
    [Display(Name = "Minimum cancellation notice for approved appointments (hours)")]
    public int MinCancellationNoticeHours { get; set; }

    [Range(0, 50)]
    [Display(Name = "Maximum client cancellations per month (0 = unlimited)")]
    public int MaxCancellationsPerMonth { get; set; }

    [Range(1, 100)]
    [Display(Name = "Low-adherence alert threshold (%)")]
    public int AdherenceAlertThreshold { get; set; }
}

public class BroadcastViewModel
{
    [Display(Name = "Audience")]
    public string? Role { get; set; }

    [Required, StringLength(150, MinimumLength = 3)]
    public string? Title { get; set; }

    [Required, StringLength(1000, MinimumLength = 3)]
    public string? Message { get; set; }

    [StringLength(300)]
    [RegularExpression(@"^/[^\s]*$", ErrorMessage = "Links must be relative paths inside HealthTrack, starting with /.")]
    [Display(Name = "Link (optional)")]
    public string? LinkUrl { get; set; }

    public List<(string Title, string Message, DateTime SentAtUtc, int Recipients)> History { get; set; } = new();
}

public class TemplateEditViewModel
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string? Name { get; set; }

    [Required, StringLength(150)]
    [Display(Name = "Title")]
    public string? TitleTemplate { get; set; }

    [Required, StringLength(1000)]
    [Display(Name = "Message")]
    public string? BodyTemplate { get; set; }

    public List<string> AvailableTokens { get; set; } = new();
}

public class AuditLogViewModel
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public bool SecurityOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;
    public int TotalCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<string> Categories { get; set; } = new();
    public List<AuditLog> Entries { get; set; } = new();
}
