using System.ComponentModel.DataAnnotations;
using HealthTrack.Models;

namespace HealthTrack.ViewModels;

public class ExerciseRow
{
    [Required(ErrorMessage = "Exercise name is required."), StringLength(120)]
    public string? Name { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }

    [Range(0, 50)]
    public int Sets { get; set; }

    [Range(0, 500)]
    public int Reps { get; set; }

    [Range(0, 600)]
    [Display(Name = "Minutes")]
    public int DurationMinutes { get; set; }

    [StringLength(300)]
    public string? Notes { get; set; }
}

public class WorkoutPlanFormViewModel : IValidatableObject
{
    public int? Id { get; set; }

    [Required, StringLength(120, MinimumLength = 3)]
    public string? Title { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [StringLength(200)]
    public string? Goal { get; set; }

    public IntensityLevel Intensity { get; set; } = IntensityLevel.Moderate;

    [Range(1, 14)]
    [Display(Name = "Sessions per week")]
    public int SessionsPerWeek { get; set; } = 3;

    [Range(1, 104)]
    [Display(Name = "Duration (weeks)")]
    public int DurationWeeks { get; set; } = 4;

    [Display(Name = "Save as a reusable template")]
    public bool IsTemplate { get; set; }

    public bool IsSystemTemplate { get; set; }

    public List<ExerciseRow> Exercises { get; set; } = new() { new ExerciseRow() };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Exercises.Count == 0)
            yield return new ValidationResult("Add at least one exercise.", new[] { nameof(Exercises) });
        if (Exercises.Count > 50)
            yield return new ValidationResult("A plan can contain at most 50 exercises.", new[] { nameof(Exercises) });
        for (var i = 0; i < Exercises.Count; i++)
        {
            var e = Exercises[i];
            if (e.Sets == 0 && e.Reps == 0 && e.DurationMinutes == 0)
                yield return new ValidationResult($"Exercise {i + 1}: enter sets/reps or a duration.", new[] { $"Exercises[{i}].Sets" });
        }
    }
}

public class MealRow
{
    public MealType MealType { get; set; } = MealType.Breakfast;

    [Required(ErrorMessage = "Food items are required."), StringLength(300)]
    [Display(Name = "Food items")]
    public string? FoodItems { get; set; }

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
}

public class DietPlanFormViewModel : IValidatableObject
{
    public int? Id { get; set; }

    [Required, StringLength(120, MinimumLength = 3)]
    public string? Title { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [Range(800, 6000)]
    [Display(Name = "Daily calorie target")]
    public int DailyCalorieTarget { get; set; } = 2000;

    [Range(0, 600)]
    [Display(Name = "Protein target (g)")]
    public int ProteinTargetG { get; set; } = 120;

    [Range(0, 1000)]
    [Display(Name = "Carbs target (g)")]
    public int CarbsTargetG { get; set; } = 220;

    [Range(0, 400)]
    [Display(Name = "Fat target (g)")]
    public int FatTargetG { get; set; } = 65;

    [StringLength(1000)]
    [Display(Name = "Nutritional guidelines")]
    public string? Guidelines { get; set; }

    [Display(Name = "Save as a reusable template")]
    public bool IsTemplate { get; set; }

    public bool IsSystemTemplate { get; set; }

    public List<MealRow> Meals { get; set; } = new() { new MealRow() };

    public int TotalMealCalories => Meals.Sum(m => m.Calories);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Meals.Count == 0)
            yield return new ValidationResult("Add at least one meal.", new[] { nameof(Meals) });
        if (Meals.Count > 30)
            yield return new ValidationResult("A plan can contain at most 30 meals.", new[] { nameof(Meals) });
    }
}

public class PlanListItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool IsTemplate { get; set; }
    public bool IsSystemTemplate { get; set; }
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
    public bool CanEdit { get; set; }
    public int ActiveAssignments { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class PlanIndexViewModel
{
    public string PlanType { get; set; } = "Workout";
    public List<PlanListItem> Plans { get; set; } = new();
    public List<PlanListItem> Templates { get; set; } = new();
    public bool IsAdministrator { get; set; }
}

public class ClientChoice
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CurrentPlan { get; set; }
}

public class PlanAssignViewModel : IValidatableObject
{
    public int PlanId { get; set; }
    public string PlanType { get; set; } = "Workout";
    public string PlanTitle { get; set; } = string.Empty;
    public int DurationWeeks { get; set; }

    [Display(Name = "Clients")]
    public List<string> ClientIds { get; set; } = new();

    [DataType(DataType.Date)]
    [Display(Name = "Start date")]
    public DateTime StartDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "End date (optional)")]
    public DateTime? EndDate { get; set; }

    public List<ClientChoice> Clients { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ClientIds.Count == 0)
            yield return new ValidationResult("Select at least one client.", new[] { nameof(ClientIds) });
        if (StartDate.Date < DateTime.Today.AddDays(-30))
            yield return new ValidationResult("The start date cannot be more than 30 days in the past.", new[] { nameof(StartDate) });
        if (EndDate.HasValue && EndDate.Value.Date < StartDate.Date)
            yield return new ValidationResult("The end date must be after the start date.", new[] { nameof(EndDate) });
    }
}
