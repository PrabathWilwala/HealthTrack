using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

/// <summary>
/// Trainers create, edit, template and assign workout plans. Administrators manage
/// system-wide templates here (oversight of all plans is in AdminController).
/// </summary>
[Authorize(Roles = RoleNames.TrainerOrAdmin)]
public class WorkoutPlansController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPlanService _plans;
    private readonly IAuditService _audit;

    public WorkoutPlansController(ApplicationDbContext db, IPlanService plans, IAuditService audit)
    {
        _db = db;
        _plans = plans;
        _audit = audit;
    }

    private bool IsAdmin => User.IsInRole(RoleNames.Administrator);

    /// <summary>Lists the trainer's plans and template library (administrators: system templates).</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = User.GetUserId();
        var query = _db.WorkoutPlans.AsNoTracking().Include(p => p.Exercises).AsQueryable();
        query = IsAdmin
            ? query.Where(p => p.IsSystemTemplate)
            : query.Where(p => p.CreatedById == userId || p.IsSystemTemplate);
        var plans = await query.OrderByDescending(p => p.UpdatedAtUtc).ToListAsync();

        var today = DateTime.Today;
        var planIds = plans.Select(p => p.Id).ToList();
        var activeCounts = await _db.PlanAssignments.AsNoTracking()
            .Where(a => a.WorkoutPlanId != null && planIds.Contains(a.WorkoutPlanId.Value) && a.IsActive && (a.EndDate == null || a.EndDate >= today))
            .GroupBy(a => a.WorkoutPlanId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        PlanListItem ToItem(WorkoutPlan p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Description = p.Description,
            Summary = $"{p.Exercises.Count} exercises · {p.SessionsPerWeek}×/week · {p.DurationWeeks} weeks · {p.Intensity}",
            IsTemplate = p.IsTemplate,
            IsSystemTemplate = p.IsSystemTemplate,
            IsFlagged = p.IsFlagged,
            FlagReason = p.FlagReason,
            CanEdit = CanEdit(p, userId),
            ActiveAssignments = activeCounts.TryGetValue(p.Id, out var n) ? n : 0,
            UpdatedAtUtc = p.UpdatedAtUtc
        };

        return View(new PlanIndexViewModel
        {
            PlanType = "Workout",
            IsAdministrator = IsAdmin,
            Plans = plans.Where(p => !p.IsTemplate).Select(ToItem).ToList(),
            Templates = plans.Where(p => p.IsTemplate).Select(ToItem).ToList()
        });
    }

    /// <summary>Shows one plan.</summary>
    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var plan = await _db.WorkoutPlans.AsNoTracking().Include(p => p.Exercises).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanView(plan, User.GetUserId())) return NotFound();
        ViewData["CanEdit"] = CanEdit(plan, User.GetUserId());
        return View(plan);
    }

    /// <summary>Shows the editor / creates a plan with its rows.</summary>
    [HttpGet]
    public IActionResult Create() => View("Form", new WorkoutPlanFormViewModel { IsTemplate = IsAdmin, IsSystemTemplate = IsAdmin });

    /// <summary>Shows the editor / creates a plan with its rows.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(WorkoutPlanFormViewModel model)
    {
        if (!ModelState.IsValid) return View("Form", model);

        var plan = new WorkoutPlan { CreatedById = User.GetUserId() };
        Apply(plan, model);
        _db.WorkoutPlans.Add(plan);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Plans", "WorkoutPlan.Created", plan.Title);

        TempData["Success"] = plan.IsTemplate ? "Template saved to your library." : "Workout plan created. You can now assign it to clients.";
        return plan.IsTemplate ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Assign), new { id = plan.Id });
    }

    /// <summary>Shows the editor / replaces the plan and notifies clients who follow it.</summary>
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var plan = await _db.WorkoutPlans.AsNoTracking().Include(p => p.Exercises).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        return View("Form", new WorkoutPlanFormViewModel
        {
            Id = plan.Id,
            Title = plan.Title,
            Description = plan.Description,
            Goal = plan.Goal,
            Intensity = plan.Intensity,
            SessionsPerWeek = plan.SessionsPerWeek,
            DurationWeeks = plan.DurationWeeks,
            IsTemplate = plan.IsTemplate,
            IsSystemTemplate = plan.IsSystemTemplate,
            Exercises = plan.Exercises.OrderBy(e => e.SortOrder).Select(e => new ExerciseRow
            {
                Name = e.Name,
                DayOfWeek = e.DayOfWeek,
                Sets = e.Sets,
                Reps = e.Reps,
                DurationMinutes = e.DurationMinutes,
                Notes = e.Notes
            }).ToList()
        });
    }

    /// <summary>Shows the editor / replaces the plan and notifies clients who follow it.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, WorkoutPlanFormViewModel model)
    {
        var plan = await _db.WorkoutPlans.Include(p => p.Exercises).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("Form", model);
        }

        // Replace the exercise list with the submitted rows.
        _db.WorkoutExercises.RemoveRange(plan.Exercises);
        plan.Exercises.Clear();
        Apply(plan, model);
        plan.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _plans.NotifyPlanUpdatedAsync(plan.Id, null, User.GetUserId());
        await _audit.LogAsync("Plans", "WorkoutPlan.Updated", plan.Title);
        TempData["Success"] = "Plan updated. Clients following it have been notified.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Deletes a plan the user owns.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var plan = await _db.WorkoutPlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        _db.WorkoutPlans.Remove(plan);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Plans", "WorkoutPlan.Deleted", plan.Title);
        TempData["Success"] = $"\"{plan.Title}\" was deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Creates an editable copy of a template (own or system).</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UseTemplate(int id)
    {
        var template = await _db.WorkoutPlans.AsNoTracking().Include(p => p.Exercises).FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);
        if (template == null || !CanView(template, User.GetUserId())) return NotFound();

        var copy = await _plans.CloneWorkoutPlanAsync(template, User.GetUserId(), asTemplate: false);
        copy.Title = template.Title.Replace("(template)", string.Empty).Trim();
        await _db.SaveChangesAsync();
        TempData["Success"] = "A new plan was created from the template. Personalise it below.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    /// <summary>Saves a copy of a plan in the template library.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAsTemplate(int id)
    {
        var plan = await _db.WorkoutPlans.AsNoTracking().Include(p => p.Exercises).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanView(plan, User.GetUserId())) return NotFound();

        await _plans.CloneWorkoutPlanAsync(plan, User.GetUserId(), asTemplate: true);
        TempData["Success"] = "Saved to your template library.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Shows the client picker / assigns the plan to the selected clients.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpGet]
    public async Task<IActionResult> Assign(int id)
    {
        var plan = await _db.WorkoutPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || plan.CreatedById != User.GetUserId() || plan.IsTemplate) return NotFound();

        var model = new PlanAssignViewModel
        {
            PlanId = plan.Id,
            PlanType = "Workout",
            PlanTitle = plan.Title,
            DurationWeeks = plan.DurationWeeks,
            EndDate = DateTime.Today.AddDays(plan.DurationWeeks * 7 - 1)
        };
        await FillClientsAsync(model);
        return View("~/Views/Shared/AssignPlan.cshtml", model);
    }

    /// <summary>Shows the client picker / assigns the plan to the selected clients.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(PlanAssignViewModel model)
    {
        var trainerId = User.GetUserId();
        var plan = await _db.WorkoutPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.PlanId);
        if (plan == null || plan.CreatedById != trainerId || plan.IsTemplate) return NotFound();

        // Trainers may only assign plans to their own clients.
        var myClients = await _db.ClientProfiles.Where(c => c.TrainerId == trainerId).Select(c => c.UserId).ToListAsync();
        if (model.ClientIds.Any(id => !myClients.Contains(id)))
            ModelState.AddModelError(nameof(model.ClientIds), "You can only assign plans to your own clients.");

        if (!ModelState.IsValid)
        {
            model.PlanTitle = plan.Title;
            model.PlanType = "Workout";
            await FillClientsAsync(model);
            return View("~/Views/Shared/AssignPlan.cshtml", model);
        }

        var count = await _plans.AssignWorkoutPlanAsync(plan.Id, model.ClientIds, model.StartDate, model.EndDate, trainerId);
        await _audit.LogAsync("Plans", "WorkoutPlan.Assigned", $"{plan.Title} → {count} client(s)");
        TempData["Success"] = $"\"{plan.Title}\" assigned to {count} client(s). They have been notified.";
        return RedirectToAction(nameof(Details), new { id = plan.Id });
    }

    // ------------------------------------------------------------------ helpers

    private bool CanView(WorkoutPlan plan, string userId) => IsAdmin || plan.CreatedById == userId || plan.IsSystemTemplate;

    private bool CanEdit(WorkoutPlan plan, string userId) => IsAdmin ? plan.IsSystemTemplate : plan.CreatedById == userId && !plan.IsSystemTemplate;

    private void Apply(WorkoutPlan plan, WorkoutPlanFormViewModel model)
    {
        plan.Title = model.Title!.Trim();
        plan.Description = model.Description?.Trim();
        plan.Goal = model.Goal?.Trim();
        plan.Intensity = model.Intensity;
        plan.SessionsPerWeek = model.SessionsPerWeek;
        plan.DurationWeeks = model.DurationWeeks;
        // Administrators only create system templates.
        plan.IsSystemTemplate = IsAdmin;
        plan.IsTemplate = IsAdmin || model.IsTemplate;
        plan.Exercises = model.Exercises.Select((e, index) => new WorkoutExercise
        {
            Name = e.Name!.Trim(),
            DayOfWeek = e.DayOfWeek,
            Sets = e.Sets,
            Reps = e.Reps,
            DurationMinutes = e.DurationMinutes,
            Notes = e.Notes?.Trim(),
            SortOrder = index
        }).ToList();
    }

    private async Task FillClientsAsync(PlanAssignViewModel model)
    {
        var trainerId = User.GetUserId();
        var clients = await _db.ClientProfiles.AsNoTracking().Where(c => c.TrainerId == trainerId)
            .OrderBy(c => c.FullName).Select(c => new { c.UserId, c.FullName }).ToListAsync();
        var ids = clients.Select(c => c.UserId).ToList();
        var current = await _db.PlanAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.ClientId) && a.IsActive && a.WorkoutPlanId != null)
            .Select(a => new { a.ClientId, a.WorkoutPlan!.Title }).ToListAsync();

        model.Clients = clients.Select(c => new ClientChoice
        {
            ClientId = c.UserId,
            Name = c.FullName,
            CurrentPlan = current.FirstOrDefault(a => a.ClientId == c.UserId)?.Title
        }).ToList();
    }
}

/// <summary>Trainers create, edit, template and assign diet plans.</summary>
[Authorize(Roles = RoleNames.TrainerOrAdmin)]
public class DietPlansController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPlanService _plans;
    private readonly IAuditService _audit;

    public DietPlansController(ApplicationDbContext db, IPlanService plans, IAuditService audit)
    {
        _db = db;
        _plans = plans;
        _audit = audit;
    }

    private bool IsAdmin => User.IsInRole(RoleNames.Administrator);

    /// <summary>Lists the trainer's plans and template library (administrators: system templates).</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = User.GetUserId();
        var query = _db.DietPlans.AsNoTracking().Include(p => p.Meals).AsQueryable();
        query = IsAdmin
            ? query.Where(p => p.IsSystemTemplate)
            : query.Where(p => p.CreatedById == userId || p.IsSystemTemplate);
        var plans = await query.OrderByDescending(p => p.UpdatedAtUtc).ToListAsync();

        var today = DateTime.Today;
        var planIds = plans.Select(p => p.Id).ToList();
        var activeCounts = await _db.PlanAssignments.AsNoTracking()
            .Where(a => a.DietPlanId != null && planIds.Contains(a.DietPlanId.Value) && a.IsActive && (a.EndDate == null || a.EndDate >= today))
            .GroupBy(a => a.DietPlanId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        PlanListItem ToItem(DietPlan p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Description = p.Description,
            Summary = $"{p.Meals.Count} meals · {p.DailyCalorieTarget} kcal/day · P{p.ProteinTargetG}/C{p.CarbsTargetG}/F{p.FatTargetG} g",
            IsTemplate = p.IsTemplate,
            IsSystemTemplate = p.IsSystemTemplate,
            IsFlagged = p.IsFlagged,
            FlagReason = p.FlagReason,
            CanEdit = CanEdit(p, userId),
            ActiveAssignments = activeCounts.TryGetValue(p.Id, out var n) ? n : 0,
            UpdatedAtUtc = p.UpdatedAtUtc
        };

        return View("~/Views/WorkoutPlans/Index.cshtml", new PlanIndexViewModel
        {
            PlanType = "Diet",
            IsAdministrator = IsAdmin,
            Plans = plans.Where(p => !p.IsTemplate).Select(ToItem).ToList(),
            Templates = plans.Where(p => p.IsTemplate).Select(ToItem).ToList()
        });
    }

    /// <summary>Shows one plan.</summary>
    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var plan = await _db.DietPlans.AsNoTracking().Include(p => p.Meals).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanView(plan, User.GetUserId())) return NotFound();
        ViewData["CanEdit"] = CanEdit(plan, User.GetUserId());
        return View(plan);
    }

    /// <summary>Shows the editor / creates a plan with its rows.</summary>
    [HttpGet]
    public IActionResult Create() => View("Form", new DietPlanFormViewModel { IsTemplate = IsAdmin, IsSystemTemplate = IsAdmin });

    /// <summary>Shows the editor / creates a plan with its rows.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DietPlanFormViewModel model)
    {
        if (!ModelState.IsValid) return View("Form", model);

        var plan = new DietPlan { CreatedById = User.GetUserId() };
        Apply(plan, model);
        _db.DietPlans.Add(plan);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Plans", "DietPlan.Created", plan.Title);

        TempData["Success"] = plan.IsTemplate ? "Template saved to your library." : "Diet plan created. You can now assign it to clients.";
        return plan.IsTemplate ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Assign), new { id = plan.Id });
    }

    /// <summary>Shows the editor / replaces the plan and notifies clients who follow it.</summary>
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var plan = await _db.DietPlans.AsNoTracking().Include(p => p.Meals).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        return View("Form", new DietPlanFormViewModel
        {
            Id = plan.Id,
            Title = plan.Title,
            Description = plan.Description,
            DailyCalorieTarget = plan.DailyCalorieTarget,
            ProteinTargetG = plan.ProteinTargetG,
            CarbsTargetG = plan.CarbsTargetG,
            FatTargetG = plan.FatTargetG,
            Guidelines = plan.Guidelines,
            IsTemplate = plan.IsTemplate,
            IsSystemTemplate = plan.IsSystemTemplate,
            Meals = plan.Meals.OrderBy(m => m.SortOrder).Select(m => new MealRow
            {
                MealType = m.MealType,
                FoodItems = m.FoodItems,
                Portion = m.Portion,
                Calories = m.Calories,
                ProteinG = m.ProteinG,
                CarbsG = m.CarbsG,
                FatG = m.FatG
            }).ToList()
        });
    }

    /// <summary>Shows the editor / replaces the plan and notifies clients who follow it.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, DietPlanFormViewModel model)
    {
        var plan = await _db.DietPlans.Include(p => p.Meals).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        if (!ModelState.IsValid)
        {
            model.Id = id;
            return View("Form", model);
        }

        _db.DietMeals.RemoveRange(plan.Meals);
        plan.Meals.Clear();
        Apply(plan, model);
        plan.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _plans.NotifyPlanUpdatedAsync(null, plan.Id, User.GetUserId());
        await _audit.LogAsync("Plans", "DietPlan.Updated", plan.Title);
        TempData["Success"] = "Plan updated. Clients following it have been notified.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Deletes a plan the user owns.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var plan = await _db.DietPlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanEdit(plan, User.GetUserId())) return NotFound();

        _db.DietPlans.Remove(plan);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Plans", "DietPlan.Deleted", plan.Title);
        TempData["Success"] = $"\"{plan.Title}\" was deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Creates an editable copy of a template.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UseTemplate(int id)
    {
        var template = await _db.DietPlans.AsNoTracking().Include(p => p.Meals).FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);
        if (template == null || !CanView(template, User.GetUserId())) return NotFound();

        var copy = await _plans.CloneDietPlanAsync(template, User.GetUserId(), asTemplate: false);
        copy.Title = template.Title.Replace("(template)", string.Empty).Trim();
        await _db.SaveChangesAsync();
        TempData["Success"] = "A new plan was created from the template. Personalise it below.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    /// <summary>Saves a copy of a plan in the template library.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAsTemplate(int id)
    {
        var plan = await _db.DietPlans.AsNoTracking().Include(p => p.Meals).FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || !CanView(plan, User.GetUserId())) return NotFound();

        await _plans.CloneDietPlanAsync(plan, User.GetUserId(), asTemplate: true);
        TempData["Success"] = "Saved to your template library.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Shows the client picker / assigns the plan to the selected clients.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpGet]
    public async Task<IActionResult> Assign(int id)
    {
        var plan = await _db.DietPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null || plan.CreatedById != User.GetUserId() || plan.IsTemplate) return NotFound();

        var model = new PlanAssignViewModel { PlanId = plan.Id, PlanType = "Diet", PlanTitle = plan.Title, EndDate = DateTime.Today.AddDays(27) };
        await FillClientsAsync(model);
        return View("~/Views/Shared/AssignPlan.cshtml", model);
    }

    /// <summary>Shows the client picker / assigns the plan to the selected clients.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(PlanAssignViewModel model)
    {
        var trainerId = User.GetUserId();
        var plan = await _db.DietPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.PlanId);
        if (plan == null || plan.CreatedById != trainerId || plan.IsTemplate) return NotFound();

        var myClients = await _db.ClientProfiles.Where(c => c.TrainerId == trainerId).Select(c => c.UserId).ToListAsync();
        if (model.ClientIds.Any(id => !myClients.Contains(id)))
            ModelState.AddModelError(nameof(model.ClientIds), "You can only assign plans to your own clients.");

        if (!ModelState.IsValid)
        {
            model.PlanTitle = plan.Title;
            model.PlanType = "Diet";
            await FillClientsAsync(model);
            return View("~/Views/Shared/AssignPlan.cshtml", model);
        }

        var count = await _plans.AssignDietPlanAsync(plan.Id, model.ClientIds, model.StartDate, model.EndDate, trainerId);
        await _audit.LogAsync("Plans", "DietPlan.Assigned", $"{plan.Title} → {count} client(s)");
        TempData["Success"] = $"\"{plan.Title}\" assigned to {count} client(s). They have been notified.";
        return RedirectToAction(nameof(Details), new { id = plan.Id });
    }

    // ------------------------------------------------------------------ helpers

    private bool CanView(DietPlan plan, string userId) => IsAdmin || plan.CreatedById == userId || plan.IsSystemTemplate;

    private bool CanEdit(DietPlan plan, string userId) => IsAdmin ? plan.IsSystemTemplate : plan.CreatedById == userId && !plan.IsSystemTemplate;

    private void Apply(DietPlan plan, DietPlanFormViewModel model)
    {
        plan.Title = model.Title!.Trim();
        plan.Description = model.Description?.Trim();
        plan.DailyCalorieTarget = model.DailyCalorieTarget;
        plan.ProteinTargetG = model.ProteinTargetG;
        plan.CarbsTargetG = model.CarbsTargetG;
        plan.FatTargetG = model.FatTargetG;
        plan.Guidelines = model.Guidelines?.Trim();
        plan.IsSystemTemplate = IsAdmin;
        plan.IsTemplate = IsAdmin || model.IsTemplate;
        plan.Meals = model.Meals.Select((m, index) => new DietMeal
        {
            MealType = m.MealType,
            FoodItems = m.FoodItems!.Trim(),
            Portion = m.Portion?.Trim(),
            Calories = m.Calories,
            ProteinG = m.ProteinG,
            CarbsG = m.CarbsG,
            FatG = m.FatG,
            SortOrder = index
        }).ToList();
    }

    private async Task FillClientsAsync(PlanAssignViewModel model)
    {
        var trainerId = User.GetUserId();
        var clients = await _db.ClientProfiles.AsNoTracking().Where(c => c.TrainerId == trainerId)
            .OrderBy(c => c.FullName).Select(c => new { c.UserId, c.FullName }).ToListAsync();
        var ids = clients.Select(c => c.UserId).ToList();
        var current = await _db.PlanAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.ClientId) && a.IsActive && a.DietPlanId != null)
            .Select(a => new { a.ClientId, a.DietPlan!.Title }).ToListAsync();

        model.Clients = clients.Select(c => new ClientChoice
        {
            ClientId = c.UserId,
            Name = c.FullName,
            CurrentPlan = current.FirstOrDefault(a => a.ClientId == c.UserId)?.Title
        }).ToList();
    }
}
