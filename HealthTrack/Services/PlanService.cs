using HealthTrack.Data;
using HealthTrack.Models;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

public interface IPlanService
{
    Task<int> AssignWorkoutPlanAsync(int planId, IEnumerable<string> clientIds, DateTime startDate, DateTime? endDate, string assignedById);
    Task<int> AssignDietPlanAsync(int planId, IEnumerable<string> clientIds, DateTime startDate, DateTime? endDate, string assignedById);
    Task NotifyPlanUpdatedAsync(int? workoutPlanId, int? dietPlanId, string updatedById);
    Task<WorkoutPlan> CloneWorkoutPlanAsync(WorkoutPlan source, string ownerId, bool asTemplate);
    Task<DietPlan> CloneDietPlanAsync(DietPlan source, string ownerId, bool asTemplate);
}

/// <summary>
/// Plan assignment and template logic. A client has at most one active workout plan and
/// one active diet plan; assigning a new one ends the previous assignment of the same type.
/// </summary>
public sealed class PlanService : IPlanService
{
    private readonly ApplicationDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IUserDirectory _directory;

    public PlanService(ApplicationDbContext db, INotificationService notifications, IUserDirectory directory)
    {
        _db = db;
        _notifications = notifications;
        _directory = directory;
    }

    /// <summary>Assigns a workout plan to clients for a date range, ending their previous workout assignment, and notifies them.</summary>
    public async Task<int> AssignWorkoutPlanAsync(int planId, IEnumerable<string> clientIds, DateTime startDate, DateTime? endDate, string assignedById)
    {
        var plan = await _db.WorkoutPlans.AsNoTracking().FirstAsync(p => p.Id == planId);
        var ids = clientIds.Distinct().ToList();

        // End any currently active workout assignment for these clients.
        var previous = await _db.PlanAssignments
            .Where(a => ids.Contains(a.ClientId) && a.WorkoutPlanId != null && a.IsActive)
            .ToListAsync();
        foreach (var old in previous)
        {
            old.IsActive = false;
            if (old.EndDate == null || old.EndDate > startDate.AddDays(-1))
                old.EndDate = startDate.AddDays(-1) < old.StartDate ? old.StartDate : startDate.AddDays(-1);
        }

        foreach (var clientId in ids)
        {
            _db.PlanAssignments.Add(new PlanAssignment
            {
                ClientId = clientId,
                AssignedById = assignedById,
                WorkoutPlanId = planId,
                StartDate = startDate.Date,
                EndDate = endDate?.Date,
                IsActive = true
            });
        }
        await _db.SaveChangesAsync();

        await NotifyAssignedAsync(ids, "workout", plan.Title, startDate, assignedById);
        return ids.Count;
    }

    /// <summary>Assigns a diet plan to clients for a date range, ending their previous diet assignment, and notifies them.</summary>
    public async Task<int> AssignDietPlanAsync(int planId, IEnumerable<string> clientIds, DateTime startDate, DateTime? endDate, string assignedById)
    {
        var plan = await _db.DietPlans.AsNoTracking().FirstAsync(p => p.Id == planId);
        var ids = clientIds.Distinct().ToList();

        var previous = await _db.PlanAssignments
            .Where(a => ids.Contains(a.ClientId) && a.DietPlanId != null && a.IsActive)
            .ToListAsync();
        foreach (var old in previous)
        {
            old.IsActive = false;
            if (old.EndDate == null || old.EndDate > startDate.AddDays(-1))
                old.EndDate = startDate.AddDays(-1) < old.StartDate ? old.StartDate : startDate.AddDays(-1);
        }

        foreach (var clientId in ids)
        {
            _db.PlanAssignments.Add(new PlanAssignment
            {
                ClientId = clientId,
                AssignedById = assignedById,
                DietPlanId = planId,
                StartDate = startDate.Date,
                EndDate = endDate?.Date,
                IsActive = true
            });
        }
        await _db.SaveChangesAsync();

        await NotifyAssignedAsync(ids, "diet", plan.Title, startDate, assignedById);
        return ids.Count;
    }

    /// <summary>Tells every client with an active assignment that the plan changed.</summary>
    public async Task NotifyPlanUpdatedAsync(int? workoutPlanId, int? dietPlanId, string updatedById)
    {
        var query = _db.PlanAssignments.AsNoTracking().Where(a => a.IsActive);
        query = workoutPlanId != null
            ? query.Where(a => a.WorkoutPlanId == workoutPlanId)
            : query.Where(a => a.DietPlanId == dietPlanId);

        var clientIds = await query.Select(a => a.ClientId).Distinct().ToListAsync();
        if (clientIds.Count == 0) return;

        var title = workoutPlanId != null
            ? (await _db.WorkoutPlans.AsNoTracking().Where(p => p.Id == workoutPlanId).Select(p => p.Title).FirstAsync())
            : (await _db.DietPlans.AsNoTracking().Where(p => p.Id == dietPlanId).Select(p => p.Title).FirstAsync());
        var trainerName = await _directory.GetDisplayNameAsync(updatedById);
        var planType = workoutPlanId != null ? "workout" : "diet";

        foreach (var clientId in clientIds)
        {
            await _notifications.NotifyFromTemplateAsync(clientId, NotificationType.PlanUpdate,
                NotificationTemplateKeys.PlanUpdated,
                new Dictionary<string, string> { ["TrainerName"] = trainerName, ["PlanTitle"] = title, ["PlanType"] = planType },
                "/Client/Plans");
        }
    }

    /// <summary>Copies a workout plan with all exercises (used by templates).</summary>
    public async Task<WorkoutPlan> CloneWorkoutPlanAsync(WorkoutPlan source, string ownerId, bool asTemplate)
    {
        var copy = new WorkoutPlan
        {
            Title = asTemplate ? source.Title : source.Title + " (copy)",
            Description = source.Description,
            Goal = source.Goal,
            Intensity = source.Intensity,
            SessionsPerWeek = source.SessionsPerWeek,
            DurationWeeks = source.DurationWeeks,
            CreatedById = ownerId,
            IsTemplate = asTemplate,
            Exercises = source.Exercises.OrderBy(e => e.SortOrder).Select(e => new WorkoutExercise
            {
                Name = e.Name,
                DayOfWeek = e.DayOfWeek,
                Sets = e.Sets,
                Reps = e.Reps,
                DurationMinutes = e.DurationMinutes,
                Notes = e.Notes,
                SortOrder = e.SortOrder
            }).ToList()
        };
        _db.WorkoutPlans.Add(copy);
        await _db.SaveChangesAsync();
        return copy;
    }

    /// <summary>Copies a diet plan with all meals (used by templates).</summary>
    public async Task<DietPlan> CloneDietPlanAsync(DietPlan source, string ownerId, bool asTemplate)
    {
        var copy = new DietPlan
        {
            Title = asTemplate ? source.Title : source.Title + " (copy)",
            Description = source.Description,
            DailyCalorieTarget = source.DailyCalorieTarget,
            ProteinTargetG = source.ProteinTargetG,
            CarbsTargetG = source.CarbsTargetG,
            FatTargetG = source.FatTargetG,
            Guidelines = source.Guidelines,
            CreatedById = ownerId,
            IsTemplate = asTemplate,
            Meals = source.Meals.OrderBy(m => m.SortOrder).Select(m => new DietMeal
            {
                MealType = m.MealType,
                FoodItems = m.FoodItems,
                Portion = m.Portion,
                Calories = m.Calories,
                ProteinG = m.ProteinG,
                CarbsG = m.CarbsG,
                FatG = m.FatG,
                SortOrder = m.SortOrder
            }).ToList()
        };
        _db.DietPlans.Add(copy);
        await _db.SaveChangesAsync();
        return copy;
    }

    private async Task NotifyAssignedAsync(IEnumerable<string> clientIds, string planType, string planTitle, DateTime startDate, string assignedById)
    {
        var trainerName = await _directory.GetDisplayNameAsync(assignedById);
        foreach (var clientId in clientIds)
        {
            await _notifications.NotifyFromTemplateAsync(clientId, NotificationType.PlanUpdate,
                NotificationTemplateKeys.PlanAssigned,
                new Dictionary<string, string>
                {
                    ["TrainerName"] = trainerName,
                    ["PlanTitle"] = planTitle,
                    ["PlanType"] = planType,
                    ["StartDate"] = startDate.ToString("dd MMM yyyy")
                },
                "/Client/Plans");
        }
    }
}
