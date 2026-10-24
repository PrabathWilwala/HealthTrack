using System.ComponentModel.DataAnnotations;
using HealthTrack.Models;
using HealthTrack.Services;

namespace HealthTrack.ViewModels;

public sealed record AppointmentListItem(Appointment Appointment, string OtherPartyName);

public sealed record FeedbackItem(TrainerFeedback Feedback, string TrainerName, string? TargetDescription);

public class ClientDashboardViewModel
{
    public string FullName { get; set; } = string.Empty;
    public string? ProfilePicturePath { get; set; }
    public string? TrainerName { get; set; }

    public decimal CurrentWeight { get; set; }
    public decimal Bmi { get; set; }
    public string BmiCategory { get; set; } = string.Empty;
    public GoalProgress Goal { get; set; } = new();

    public int CaloriesBurnedToday { get; set; }
    public int CaloriesBurnedWeek { get; set; }
    public int CaloriesConsumedToday { get; set; }
    public int CalorieTarget { get; set; }
    public decimal ProteinToday { get; set; }
    public decimal CarbsToday { get; set; }
    public decimal FatToday { get; set; }
    public int ProteinTarget { get; set; }
    public int CarbsTarget { get; set; }
    public int FatTarget { get; set; }

    public int WorkoutsThisWeek { get; set; }
    public int SessionsPerWeekTarget { get; set; }

    public WorkoutPlan? WorkoutPlan { get; set; }
    public List<WorkoutExercise> TodaysExercises { get; set; } = new();
    public bool WorkoutDoneToday { get; set; }
    public List<WorkoutLog> TodaysWorkouts { get; set; } = new();

    public DietPlan? DietPlan { get; set; }
    public List<MealLog> TodaysMeals { get; set; } = new();

    public List<AppointmentListItem> UpcomingAppointments { get; set; } = new();
    public List<Notification> RecentNotifications { get; set; } = new();
    public List<FeedbackItem> RecentFeedback { get; set; } = new();
    public AdherenceResult Adherence { get; set; } = new();

    public ChartData WeightChart7 { get; set; } = new();
    public ChartData WeightChart30 { get; set; } = new();
}

public class ClientPlansViewModel
{
    public PlanAssignment? WorkoutAssignment { get; set; }
    public PlanAssignment? DietAssignment { get; set; }
    public AdherenceResult Adherence30 { get; set; } = new();
    public AdherenceResult Adherence7 { get; set; } = new();
    public List<PlanAssignment> History { get; set; } = new();
    public string? TrainerName { get; set; }
    public Dictionary<DayOfWeek, bool> WorkoutDaysThisWeek { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Logging forms
// ---------------------------------------------------------------------------

public class WorkoutLogFormViewModel : IValidatableObject
{
    public int? Id { get; set; }

    [Required, StringLength(120)]
    [Display(Name = "Exercise / activity")]
    public string? ExerciseType { get; set; }

    [Range(1, 600, ErrorMessage = "Duration must be between 1 and 600 minutes.")]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 30;

    public IntensityLevel Intensity { get; set; } = IntensityLevel.Moderate;

    [Range(0, 5000)]
    [Display(Name = "Calories burned")]
    public int? CaloriesBurned { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Date")]
    public DateTime LogDate { get; set; } = DateTime.Today;

    [StringLength(500)]
    public string? Notes { get; set; }

    [Display(Name = "Part of my assigned workout plan")]
    public bool LinkToPlan { get; set; } = true;

    public string? ActivePlanTitle { get; set; }
    public List<string> SuggestedExercises { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LogDate.Date > DateTime.Today)
            yield return new ValidationResult("You cannot log a workout in the future.", new[] { nameof(LogDate) });
        if (LogDate.Date < DateTime.Today.AddYears(-1))
            yield return new ValidationResult("The date is too far in the past.", new[] { nameof(LogDate) });
    }
}

public class MealLogFormViewModel : IValidatableObject
{
    public int? Id { get; set; }

    [Display(Name = "Meal")]
    public MealType MealType { get; set; } = MealType.Breakfast;

    [Required, StringLength(300)]
    [Display(Name = "Food items")]
    public string? FoodItems { get; set; }

    [StringLength(100)]
    public string? Portion { get; set; }

    [Range(0, 5000)]
    public int Calories { get; set; }

    [Range(0, 500)]
    [Display(Name = "Protein (g)")]
    public decimal ProteinG { get; set; }

    [Range(0, 800)]
    [Display(Name = "Carbs (g)")]
    public decimal CarbsG { get; set; }

    [Range(0, 300)]
    [Display(Name = "Fat (g)")]
    public decimal FatG { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Date")]
    public DateTime LogDate { get; set; } = DateTime.Today;

    [StringLength(500)]
    public string? Notes { get; set; }

    [Display(Name = "Part of my assigned diet plan")]
    public bool LinkToPlan { get; set; } = true;

    public string? ActivePlanTitle { get; set; }
    public List<DietMeal> PlanMeals { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LogDate.Date > DateTime.Today)
            yield return new ValidationResult("You cannot log a meal in the future.", new[] { nameof(LogDate) });
        if (LogDate.Date < DateTime.Today.AddYears(-1))
            yield return new ValidationResult("The date is too far in the past.", new[] { nameof(LogDate) });
    }
}

public class MeasurementFormViewModel : IValidatableObject
{
    public int? Id { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Date")]
    public DateTime MeasuredOn { get; set; } = DateTime.Today;

    [Range(typeof(decimal), "10", "500", ErrorMessage = "Weight must be between 10 and 500 kg.")]
    [Display(Name = "Weight (kg)")]
    public decimal WeightKg { get; set; }

    [Range(typeof(decimal), "1", "75")]
    [Display(Name = "Body fat (%)")]
    public decimal? BodyFatPercent { get; set; }

    [Range(typeof(decimal), "30", "300")]
    [Display(Name = "Waist (cm)")]
    public decimal? WaistCm { get; set; }

    [Range(60, 260)]
    [Display(Name = "Blood pressure – systolic")]
    public int? SystolicBp { get; set; }

    [Range(30, 160)]
    [Display(Name = "Blood pressure – diastolic")]
    public int? DiastolicBp { get; set; }

    [Range(25, 250)]
    [Display(Name = "Resting heart rate (bpm)")]
    public int? RestingHeartRate { get; set; }

    [StringLength(1000)]
    [Display(Name = "Private notes")]
    public string? Notes { get; set; }

    public decimal HeightCm { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MeasuredOn.Date > DateTime.Today)
            yield return new ValidationResult("You cannot record a measurement in the future.", new[] { nameof(MeasuredOn) });
        if (SystolicBp.HasValue != DiastolicBp.HasValue)
            yield return new ValidationResult("Enter both systolic and diastolic values, or neither.", new[] { nameof(DiastolicBp) });
        if (SystolicBp.HasValue && DiastolicBp.HasValue && DiastolicBp >= SystolicBp)
            yield return new ValidationResult("Diastolic pressure must be lower than systolic pressure.", new[] { nameof(DiastolicBp) });
    }
}

// History page for any log type with a date-range filter.
public class LogHistoryViewModel<T>
{
    [DataType(DataType.Date)]
    public DateTime From { get; set; }

    [DataType(DataType.Date)]
    public DateTime To { get; set; }

    public List<T> Items { get; set; } = new();

    // Entries created after this moment can still be edited or deleted.
    public DateTime EditableAfterUtc { get; set; }

    public Dictionary<int, int> FeedbackCounts { get; set; } = new();
}

// Model for the shared date-range filter partial.
public sealed record DateRange(DateTime From, DateTime To);
