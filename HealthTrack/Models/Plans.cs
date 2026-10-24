using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Models;

// A workout programme created by a trainer (or a system-wide template created by an administrator).
public class WorkoutPlan
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Title { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [StringLength(200)]
    public string? Goal { get; set; }

    public IntensityLevel Intensity { get; set; } = IntensityLevel.Moderate;

    // Number of sessions the client is expected to complete per week (drives adherence).
    [Range(1, 14)]
    public int SessionsPerWeek { get; set; } = 3;

    [Range(1, 104)]
    public int DurationWeeks { get; set; } = 4;

    // User id of the trainer/admin who created the plan. Null if that account was deleted.
    [StringLength(450)]
    public string? CreatedById { get; set; }

    // Templates appear in the template library and can be cloned into new plans.
    public bool IsTemplate { get; set; }

    // System templates are managed by administrators and visible to every trainer.
    public bool IsSystemTemplate { get; set; }

    public bool IsFlagged { get; set; }

    [StringLength(500)]
    public string? FlagReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<WorkoutExercise> Exercises { get; set; } = new();

    public List<PlanAssignment> Assignments { get; set; } = new();
}

// One exercise inside a workout plan.
public class WorkoutExercise
{
    public int Id { get; set; }

    public int WorkoutPlanId { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    // Day the exercise is scheduled for. Null means "any training day".
    public DayOfWeek? DayOfWeek { get; set; }

    [Range(0, 50)]
    public int Sets { get; set; }

    [Range(0, 500)]
    public int Reps { get; set; }

    [Range(0, 600)]
    public int DurationMinutes { get; set; }

    [StringLength(300)]
    public string? Notes { get; set; }

    public int SortOrder { get; set; }
}

// A nutrition plan with daily calorie and macronutrient targets.
public class DietPlan
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Title { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [Range(800, 6000)]
    public int DailyCalorieTarget { get; set; } = 2000;

    [Range(0, 600)]
    public int ProteinTargetG { get; set; }

    [Range(0, 1000)]
    public int CarbsTargetG { get; set; }

    [Range(0, 400)]
    public int FatTargetG { get; set; }

    [StringLength(1000)]
    public string? Guidelines { get; set; }

    [StringLength(450)]
    public string? CreatedById { get; set; }

    public bool IsTemplate { get; set; }

    public bool IsSystemTemplate { get; set; }

    public bool IsFlagged { get; set; }

    [StringLength(500)]
    public string? FlagReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<DietMeal> Meals { get; set; } = new();

    public List<PlanAssignment> Assignments { get; set; } = new();
}

// One meal inside a diet plan.
public class DietMeal
{
    public int Id { get; set; }

    public int DietPlanId { get; set; }

    public MealType MealType { get; set; }

    [Required, StringLength(300)]
    public string FoodItems { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Portion { get; set; }

    [Range(0, 5000)]
    public int Calories { get; set; }

    [Range(0, 500)]
    public int ProteinG { get; set; }

    [Range(0, 800)]
    public int CarbsG { get; set; }

    [Range(0, 300)]
    public int FatG { get; set; }

    public int SortOrder { get; set; }
}

// Links a client to a workout plan or a diet plan for a date range.
// Exactly one of WorkoutPlanId and DietPlanId is set.
public class PlanAssignment
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    [StringLength(450)]
    public string? AssignedById { get; set; }

    public int? WorkoutPlanId { get; set; }

    public WorkoutPlan? WorkoutPlan { get; set; }

    public int? DietPlanId { get; set; }

    public DietPlan? DietPlan { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;

    // True when the assignment covers the given calendar day.
    public bool CoversDate(DateTime day) =>
        IsActive && StartDate.Date <= day.Date && (EndDate == null || EndDate.Value.Date >= day.Date);
}
