using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

// Shared rules for client log controllers.
public abstract class ClientLogControllerBase : Controller
{
    // Clients can correct or remove an entry for this many days after creating it.
    public const int EditWindowDays = 7;

    protected static DateTime EditableAfterUtc => DateTime.UtcNow.AddDays(-EditWindowDays);

    protected static bool IsEditable(DateTime createdAtUtc) => createdAtUtc >= EditableAfterUtc;

    // Normalises an optional date range (defaults to the last 30 days).
    protected static (DateTime From, DateTime To) ResolveRange(DateTime? from, DateTime? to)
    {
        var end = (to ?? DateTime.Today).Date;
        var start = (from ?? end.AddDays(-29)).Date;
        if (start > end) (start, end) = (end, start);
        return (start, end);
    }
}

// ============================================================================
// Workouts
// ============================================================================

[Authorize(Roles = RoleNames.Client)]
public class WorkoutLogsController : ClientLogControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;

    public WorkoutLogsController(ApplicationDbContext db, IAuditService audit, INotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
    }

    // Log history for a date range.
    [HttpGet]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var clientId = User.GetUserId();
        var (start, end) = ResolveRange(from, to);

        var items = await _db.WorkoutLogs.AsNoTracking().Include(w => w.WorkoutPlan)
            .Where(w => w.ClientId == clientId && w.LogDate >= start && w.LogDate <= end)
            .OrderByDescending(w => w.LogDate).ThenByDescending(w => w.CreatedAtUtc)
            .ToListAsync();

        return View(new LogHistoryViewModel<WorkoutLog>
        {
            From = start,
            To = end,
            Items = items,
            EditableAfterUtc = EditableAfterUtc,
            FeedbackCounts = await FeedbackCountsAsync(clientId, items.Select(i => i.Id).ToList())
        });
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpGet]
    public async Task<IActionResult> Create(string? exercise, int? duration)
    {
        var model = new WorkoutLogFormViewModel { ExerciseType = exercise };
        if (duration is > 0 and <= 600) model.DurationMinutes = duration.Value;
        await FillPlanInfoAsync(model);
        return View("Form", model);
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(WorkoutLogFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await FillPlanInfoAsync(model);
            return View("Form", model);
        }

        var clientId = User.GetUserId();
        var log = new WorkoutLog { ClientId = clientId };
        await ApplyAsync(log, model, clientId);
        _db.WorkoutLogs.Add(log);
        await _db.SaveChangesAsync();

        await _audit.LogAsync("Workout", "Workout.Logged", $"{log.ExerciseType}, {log.DurationMinutes} min");
        await _notifications.NotifyTrainerOfClientLogAsync(clientId, "workout",
            $"{log.ExerciseType} for {log.DurationMinutes} min ({log.CaloriesBurned} kcal)");

        TempData["Success"] = $"Workout logged: {log.ExerciseType}, {log.CaloriesBurned} kcal burned.";
        return RedirectToAction(nameof(Index));
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        var model = new WorkoutLogFormViewModel
        {
            Id = log.Id,
            ExerciseType = log.ExerciseType,
            DurationMinutes = log.DurationMinutes,
            Intensity = log.Intensity,
            CaloriesBurned = log.CaloriesBurned,
            LogDate = log.LogDate,
            Notes = log.Notes,
            LinkToPlan = log.WorkoutPlanId != null
        };
        await FillPlanInfoAsync(model);
        return View("Form", model);
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, WorkoutLogFormViewModel model)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        if (!ModelState.IsValid)
        {
            model.Id = id;
            await FillPlanInfoAsync(model);
            return View("Form", model);
        }

        await ApplyAsync(log, model, log.ClientId);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Workout", "Workout.Edited", $"Workout #{id}");
        TempData["Success"] = "Workout updated.";
        return RedirectToAction(nameof(Index));
    }

    // Deletes an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        _db.WorkoutLogs.Remove(log);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Workout", "Workout.Deleted", $"Workout #{id}");
        TempData["Success"] = "Workout deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<WorkoutLog?> FindEditableAsync(int id)
    {
        var clientId = User.GetUserId();
        var log = await _db.WorkoutLogs.FirstOrDefaultAsync(w => w.Id == id && w.ClientId == clientId);
        return log != null && IsEditable(log.CreatedAtUtc) ? log : null;
    }

    private IActionResult NotFoundOrExpired()
    {
        TempData["Error"] = $"That entry was not found or is older than {EditWindowDays} days and can no longer be changed.";
        return RedirectToAction(nameof(Index));
    }

    // Copies form values onto the entity, estimating calories when none were entered.
    private async Task ApplyAsync(WorkoutLog log, WorkoutLogFormViewModel model, string clientId)
    {
        log.ExerciseType = model.ExerciseType!.Trim();
        log.DurationMinutes = model.DurationMinutes;
        log.Intensity = model.Intensity;
        log.LogDate = model.LogDate.Date;
        log.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        if (model.CaloriesBurned is > 0)
        {
            log.CaloriesBurned = model.CaloriesBurned.Value;
        }
        else
        {
            var weight = await _db.ClientProfiles.Where(p => p.UserId == clientId).Select(p => p.WeightKg).FirstOrDefaultAsync();
            log.CaloriesBurned = CalorieEstimator.Estimate(model.Intensity, model.DurationMinutes, weight > 0 ? weight : 70);
        }

        log.WorkoutPlanId = model.LinkToPlan ? await ActivePlanIdAsync(clientId, log.LogDate) : null;
    }

    private Task<int?> ActivePlanIdAsync(string clientId, DateTime date) =>
        _db.PlanAssignments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.WorkoutPlanId != null && a.StartDate <= date && (a.EndDate == null || a.EndDate >= date))
            .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.StartDate)
            .Select(a => a.WorkoutPlanId)
            .FirstOrDefaultAsync();

    private async Task FillPlanInfoAsync(WorkoutLogFormViewModel model)
    {
        var clientId = User.GetUserId();
        var today = DateTime.Today;
        var planId = await _db.PlanAssignments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.IsActive && a.WorkoutPlanId != null && a.StartDate <= today && (a.EndDate == null || a.EndDate >= today))
            .OrderByDescending(a => a.StartDate)
            .Select(a => a.WorkoutPlanId)
            .FirstOrDefaultAsync();
        var plan = planId == null ? null : await _db.WorkoutPlans.AsNoTracking()
            .Include(p => p.Exercises)
            .FirstOrDefaultAsync(p => p.Id == planId);

        model.ActivePlanTitle = plan?.Title;
        model.SuggestedExercises = plan?.Exercises.OrderBy(e => e.SortOrder).Select(e => e.Name).Distinct().ToList() ?? new List<string>();
        foreach (var common in new[] { "Walking", "Jogging", "Cycling", "Swimming", "Strength training", "Yoga", "HIIT" })
            if (!model.SuggestedExercises.Contains(common)) model.SuggestedExercises.Add(common);
    }

    private Task<Dictionary<int, int>> FeedbackCountsAsync(string clientId, List<int> ids) =>
        _db.TrainerFeedback.AsNoTracking()
            .Where(f => f.ClientId == clientId && f.Target == FeedbackTarget.Workout && f.TargetId != null && ids.Contains(f.TargetId.Value))
            .GroupBy(f => f.TargetId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
}

// ============================================================================
// Meals
// ============================================================================

[Authorize(Roles = RoleNames.Client)]
public class MealLogsController : ClientLogControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;

    public MealLogsController(ApplicationDbContext db, IAuditService audit, INotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
    }

    // Log history for a date range.
    [HttpGet]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var clientId = User.GetUserId();
        var (start, end) = ResolveRange(from, to);

        var items = await _db.MealLogs.AsNoTracking()
            .Where(m => m.ClientId == clientId && m.LogDate >= start && m.LogDate <= end)
            .OrderByDescending(m => m.LogDate).ThenBy(m => m.MealType)
            .ToListAsync();

        var ids = items.Select(i => i.Id).ToList();
        return View(new LogHistoryViewModel<MealLog>
        {
            From = start,
            To = end,
            Items = items,
            EditableAfterUtc = EditableAfterUtc,
            FeedbackCounts = await _db.TrainerFeedback.AsNoTracking()
                .Where(f => f.ClientId == clientId && f.Target == FeedbackTarget.Meal && f.TargetId != null && ids.Contains(f.TargetId.Value))
                .GroupBy(f => f.TargetId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count)
        });
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpGet]
    public async Task<IActionResult> Create(int? fromPlanMeal)
    {
        var model = new MealLogFormViewModel();
        await FillPlanInfoAsync(model);

        // "Log this meal" from the dashboard pre-fills the planned meal.
        var planned = model.PlanMeals.FirstOrDefault(m => m.Id == fromPlanMeal);
        if (planned != null)
        {
            model.MealType = planned.MealType;
            model.FoodItems = planned.FoodItems;
            model.Portion = planned.Portion;
            model.Calories = planned.Calories;
            model.ProteinG = planned.ProteinG;
            model.CarbsG = planned.CarbsG;
            model.FatG = planned.FatG;
        }
        return View("Form", model);
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MealLogFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await FillPlanInfoAsync(model);
            return View("Form", model);
        }

        var clientId = User.GetUserId();
        var log = new MealLog { ClientId = clientId };
        await ApplyAsync(log, model, clientId);
        _db.MealLogs.Add(log);
        await _db.SaveChangesAsync();

        await _audit.LogAsync("Meal", "Meal.Logged", $"{log.MealType}: {log.Calories} kcal");
        await _notifications.NotifyTrainerOfClientLogAsync(clientId, "meal", $"{log.MealType} – {log.FoodItems} ({log.Calories} kcal)");

        TempData["Success"] = $"{log.MealType} logged ({log.Calories} kcal).";
        return RedirectToAction(nameof(Index));
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        var model = new MealLogFormViewModel
        {
            Id = log.Id,
            MealType = log.MealType,
            FoodItems = log.FoodItems,
            Portion = log.Portion,
            Calories = log.Calories,
            ProteinG = log.ProteinG,
            CarbsG = log.CarbsG,
            FatG = log.FatG,
            LogDate = log.LogDate,
            Notes = log.Notes,
            LinkToPlan = log.DietPlanId != null
        };
        await FillPlanInfoAsync(model);
        return View("Form", model);
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, MealLogFormViewModel model)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        if (!ModelState.IsValid)
        {
            model.Id = id;
            await FillPlanInfoAsync(model);
            return View("Form", model);
        }

        await ApplyAsync(log, model, log.ClientId);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Meal", "Meal.Edited", $"Meal #{id}");
        TempData["Success"] = "Meal updated.";
        return RedirectToAction(nameof(Index));
    }

    // Deletes an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var log = await FindEditableAsync(id);
        if (log == null) return NotFoundOrExpired();

        _db.MealLogs.Remove(log);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Meal", "Meal.Deleted", $"Meal #{id}");
        TempData["Success"] = "Meal deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<MealLog?> FindEditableAsync(int id)
    {
        var clientId = User.GetUserId();
        var log = await _db.MealLogs.FirstOrDefaultAsync(m => m.Id == id && m.ClientId == clientId);
        return log != null && IsEditable(log.CreatedAtUtc) ? log : null;
    }

    private IActionResult NotFoundOrExpired()
    {
        TempData["Error"] = $"That entry was not found or is older than {EditWindowDays} days and can no longer be changed.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ApplyAsync(MealLog log, MealLogFormViewModel model, string clientId)
    {
        log.MealType = model.MealType;
        log.FoodItems = model.FoodItems!.Trim();
        log.Portion = string.IsNullOrWhiteSpace(model.Portion) ? null : model.Portion.Trim();
        log.Calories = model.Calories;
        log.ProteinG = model.ProteinG;
        log.CarbsG = model.CarbsG;
        log.FatG = model.FatG;
        log.LogDate = model.LogDate.Date;
        log.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

        var date = log.LogDate;
        log.DietPlanId = model.LinkToPlan
            ? await _db.PlanAssignments.AsNoTracking()
                .Where(a => a.ClientId == clientId && a.DietPlanId != null && a.StartDate <= date && (a.EndDate == null || a.EndDate >= date))
                .OrderByDescending(a => a.IsActive).ThenByDescending(a => a.StartDate)
                .Select(a => a.DietPlanId).FirstOrDefaultAsync()
            : null;
    }

    private async Task FillPlanInfoAsync(MealLogFormViewModel model)
    {
        var clientId = User.GetUserId();
        var today = DateTime.Today;
        var planId = await _db.PlanAssignments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.IsActive && a.DietPlanId != null && a.StartDate <= today && (a.EndDate == null || a.EndDate >= today))
            .OrderByDescending(a => a.StartDate)
            .Select(a => a.DietPlanId)
            .FirstOrDefaultAsync();
        var plan = planId == null ? null : await _db.DietPlans.AsNoTracking()
            .Include(p => p.Meals)
            .FirstOrDefaultAsync(p => p.Id == planId);

        model.ActivePlanTitle = plan?.Title;
        model.PlanMeals = plan?.Meals.OrderBy(m => m.SortOrder).ToList() ?? new List<DietMeal>();
    }
}

// ============================================================================
// Health measurements
// ============================================================================

[Authorize(Roles = RoleNames.Client)]
public class MeasurementsController : ClientLogControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;

    public MeasurementsController(ApplicationDbContext db, IAuditService audit, INotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
    }

    // Log history for a date range.
    [HttpGet]
    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var clientId = User.GetUserId();
        var (start, end) = ResolveRange(from ?? DateTime.Today.AddDays(-89), to);

        var items = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => m.ClientId == clientId && m.MeasuredOn >= start && m.MeasuredOn <= end)
            .OrderByDescending(m => m.MeasuredOn).ThenByDescending(m => m.Id)
            .ToListAsync();

        return View(new LogHistoryViewModel<HealthMeasurement>
        {
            From = start,
            To = end,
            Items = items,
            EditableAfterUtc = EditableAfterUtc
        });
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var clientId = User.GetUserId();
        var profile = await _db.ClientProfiles.AsNoTracking().FirstAsync(p => p.UserId == clientId);
        return View("Form", new MeasurementFormViewModel { WeightKg = profile.WeightKg, HeightCm = profile.HeightCm });
    }

    // Shows the form / saves a new log entry and alerts the trainer if they asked for it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MeasurementFormViewModel model)
    {
        var clientId = User.GetUserId();
        var profile = await _db.ClientProfiles.FirstAsync(p => p.UserId == clientId);
        if (!ModelState.IsValid)
        {
            model.HeightCm = profile.HeightCm;
            return View("Form", model);
        }

        var measurement = new HealthMeasurement { ClientId = clientId };
        Apply(measurement, model, profile.HeightCm);
        _db.HealthMeasurements.Add(measurement);
        await _db.SaveChangesAsync();
        await RefreshCurrentWeightAsync(profile);

        await _audit.LogAsync("Measurement", "Measurement.Logged", $"{measurement.WeightKg} kg, BMI {measurement.Bmi}");
        await _notifications.NotifyTrainerOfClientLogAsync(clientId, "measurement",
            $"Weight {measurement.WeightKg:0.0} kg (BMI {measurement.Bmi:0.0})");

        TempData["Success"] = $"Measurement saved. Your BMI is {measurement.Bmi:0.0} ({BmiCalculator.Category(measurement.Bmi)}).";
        return RedirectToAction(nameof(Index));
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var m = await FindEditableAsync(id);
        if (m == null) return NotFoundOrExpired();

        var clientId = User.GetUserId();
        var height = await _db.ClientProfiles.Where(p => p.UserId == clientId).Select(p => p.HeightCm).FirstAsync();
        return View("Form", new MeasurementFormViewModel
        {
            Id = m.Id,
            MeasuredOn = m.MeasuredOn,
            WeightKg = m.WeightKg,
            BodyFatPercent = m.BodyFatPercent,
            WaistCm = m.WaistCm,
            SystolicBp = m.SystolicBp,
            DiastolicBp = m.DiastolicBp,
            RestingHeartRate = m.RestingHeartRate,
            Notes = m.Notes,
            HeightCm = height
        });
    }

    // Shows the form / updates an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, MeasurementFormViewModel model)
    {
        var measurement = await FindEditableAsync(id);
        if (measurement == null) return NotFoundOrExpired();

        var profile = await _db.ClientProfiles.FirstAsync(p => p.UserId == measurement.ClientId);
        if (!ModelState.IsValid)
        {
            model.Id = id;
            model.HeightCm = profile.HeightCm;
            return View("Form", model);
        }

        Apply(measurement, model, profile.HeightCm);
        await _db.SaveChangesAsync();
        await RefreshCurrentWeightAsync(profile);
        await _audit.LogAsync("Measurement", "Measurement.Edited", $"Measurement #{id}");
        TempData["Success"] = "Measurement updated.";
        return RedirectToAction(nameof(Index));
    }

    // Deletes an entry (only within the edit window).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var measurement = await FindEditableAsync(id);
        if (measurement == null) return NotFoundOrExpired();

        _db.HealthMeasurements.Remove(measurement);
        await _db.SaveChangesAsync();
        var profile = await _db.ClientProfiles.FirstAsync(p => p.UserId == measurement.ClientId);
        await RefreshCurrentWeightAsync(profile);
        await _audit.LogAsync("Measurement", "Measurement.Deleted", $"Measurement #{id}");
        TempData["Success"] = "Measurement deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<HealthMeasurement?> FindEditableAsync(int id)
    {
        var clientId = User.GetUserId();
        var m = await _db.HealthMeasurements.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId);
        return m != null && IsEditable(m.CreatedAtUtc) ? m : null;
    }

    private IActionResult NotFoundOrExpired()
    {
        TempData["Error"] = $"That entry was not found or is older than {EditWindowDays} days and can no longer be changed.";
        return RedirectToAction(nameof(Index));
    }

    private static void Apply(HealthMeasurement m, MeasurementFormViewModel model, decimal heightCm)
    {
        m.MeasuredOn = model.MeasuredOn.Date;
        m.WeightKg = model.WeightKg;
        m.Bmi = BmiCalculator.Calculate(model.WeightKg, heightCm);
        m.BodyFatPercent = model.BodyFatPercent;
        m.WaistCm = model.WaistCm;
        m.SystolicBp = model.SystolicBp;
        m.DiastolicBp = model.DiastolicBp;
        m.RestingHeartRate = model.RestingHeartRate;
        m.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
    }

    // The profile's current weight always mirrors the most recent measurement.
    private async Task RefreshCurrentWeightAsync(ClientProfile profile)
    {
        var latest = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => m.ClientId == profile.UserId)
            .OrderByDescending(m => m.MeasuredOn).ThenByDescending(m => m.Id)
            .Select(m => (decimal?)m.WeightKg)
            .FirstOrDefaultAsync();
        if (latest.HasValue && latest.Value != profile.WeightKg)
        {
            profile.WeightKg = latest.Value;
            await _db.SaveChangesAsync();
        }
    }
}
