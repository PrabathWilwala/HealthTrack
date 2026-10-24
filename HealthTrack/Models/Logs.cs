using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;

namespace HealthTrack.Models;

// A workout session recorded by a client.
public class WorkoutLog
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    // The assigned plan this session was part of, if any.
    public int? WorkoutPlanId { get; set; }

    public WorkoutPlan? WorkoutPlan { get; set; }

    [Required, StringLength(120)]
    public string ExerciseType { get; set; } = string.Empty;

    [Range(1, 600)]
    public int DurationMinutes { get; set; }

    public IntensityLevel Intensity { get; set; } = IntensityLevel.Moderate;

    [Range(0, 5000)]
    public int CaloriesBurned { get; set; }

    public DateTime LogDate { get; set; } = DateTime.Today;

    [StringLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// A meal recorded by a client.
public class MealLog
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    public int? DietPlanId { get; set; }

    public DietPlan? DietPlan { get; set; }

    public MealType MealType { get; set; }

    [Required, StringLength(300)]
    public string FoodItems { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Portion { get; set; }

    [Range(0, 5000)]
    public int Calories { get; set; }

    [Range(0, 500)]
    public decimal ProteinG { get; set; }

    [Range(0, 800)]
    public decimal CarbsG { get; set; }

    [Range(0, 300)]
    public decimal FatG { get; set; }

    public DateTime LogDate { get; set; } = DateTime.Today;

    [StringLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// A body/health measurement recorded by a client. BMI is computed on save.
public class HealthMeasurement
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    public DateTime MeasuredOn { get; set; } = DateTime.Today;

    [Range(typeof(decimal), "10", "500")]
    public decimal WeightKg { get; set; }

    public decimal Bmi { get; set; }

    [Range(typeof(decimal), "1", "75")]
    public decimal? BodyFatPercent { get; set; }

    [Range(typeof(decimal), "30", "300")]
    public decimal? WaistCm { get; set; }

    [Range(60, 260)]
    public int? SystolicBp { get; set; }

    [Range(30, 160)]
    public int? DiastolicBp { get; set; }

    [Range(25, 250)]
    public int? RestingHeartRate { get; set; }

    // Private notes about this measurement. Encrypted at rest.
    [Encrypted]
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// A comment from a trainer on a client's log entry (or general coaching feedback).
public class TrainerFeedback
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string TrainerId { get; set; } = string.Empty;

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    public FeedbackTarget Target { get; set; } = FeedbackTarget.General;

    // Id of the workout/meal/measurement the feedback refers to (null for general feedback).
    public int? TargetId { get; set; }

    [Required, StringLength(1000)]
    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
