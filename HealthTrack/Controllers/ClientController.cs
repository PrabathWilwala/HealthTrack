using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

// Client dashboard, assigned plans and trainer feedback.
[Authorize(Roles = RoleNames.Client)]
public class ClientController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAnalyticsService _analytics;
    private readonly IUserDirectory _directory;

    public ClientController(ApplicationDbContext db, IAnalyticsService analytics, IUserDirectory directory)
    {
        _db = db;
        _analytics = analytics;
        _directory = directory;
    }

    // Client dashboard: health stats, today's workout and meals, weight trend, appointments, notifications and feedback.
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        var clientId = User.GetUserId();
        var today = DateTime.Today;
        var weekStart = DateHelpers.StartOfWeek(today);

        var profile = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == clientId);
        if (profile == null)
            return RedirectToAction("Index", "Profile");

        var (workoutAssignment, dietAssignment) = await GetActiveAssignmentsAsync(clientId);

        var weekWorkouts = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => w.ClientId == clientId && w.LogDate >= weekStart && w.LogDate <= today)
            .ToListAsync();
        var todaysMeals = await _db.MealLogs.AsNoTracking()
            .Where(m => m.ClientId == clientId && m.LogDate == today)
            .OrderBy(m => m.MealType).ToListAsync();

        // 30-day progress data is reused for both the 7- and 30-day weight charts.
        var progress = await _analytics.GetClientProgressAsync(clientId, today.AddDays(-29), today, ReportGrouping.Day);

        var model = new ClientDashboardViewModel
        {
            FullName = profile.FullName,
            ProfilePicturePath = profile.ProfilePicturePath,
            TrainerName = profile.TrainerId != null ? await _directory.GetDisplayNameAsync(profile.TrainerId) : null,
            CurrentWeight = progress.CurrentWeight,
            Bmi = progress.CurrentBmi,
            BmiCategory = progress.BmiCategory,
            Goal = progress.Goal,
            CaloriesBurnedToday = weekWorkouts.Where(w => w.LogDate == today).Sum(w => w.CaloriesBurned),
            CaloriesBurnedWeek = weekWorkouts.Sum(w => w.CaloriesBurned),
            CaloriesConsumedToday = todaysMeals.Sum(m => m.Calories),
            ProteinToday = todaysMeals.Sum(m => m.ProteinG),
            CarbsToday = todaysMeals.Sum(m => m.CarbsG),
            FatToday = todaysMeals.Sum(m => m.FatG),
            CalorieTarget = progress.CalorieTarget,
            ProteinTarget = progress.ProteinTarget,
            CarbsTarget = progress.CarbsTarget,
            FatTarget = progress.FatTarget,
            WorkoutsThisWeek = weekWorkouts.Select(w => w.LogDate).Distinct().Count(),
            SessionsPerWeekTarget = workoutAssignment?.WorkoutPlan?.SessionsPerWeek ?? 0,
            WorkoutPlan = workoutAssignment?.WorkoutPlan,
            DietPlan = dietAssignment?.DietPlan,
            TodaysWorkouts = weekWorkouts.Where(w => w.LogDate == today).ToList(),
            WorkoutDoneToday = weekWorkouts.Any(w => w.LogDate == today),
            TodaysMeals = todaysMeals,
            Adherence = progress.Adherence,
            WeightChart30 = progress.WeightChart
        };

        // Today's exercises: those scheduled for this weekday plus "any day" exercises.
        if (model.WorkoutPlan != null)
        {
            model.TodaysExercises = model.WorkoutPlan.Exercises
                .Where(e => e.DayOfWeek == null || e.DayOfWeek == today.DayOfWeek)
                .OrderBy(e => e.SortOrder).ToList();
        }

        // Last 7 days of the weight chart.
        var skip = Math.Max(0, progress.WeightChart.Labels.Count - 7);
        model.WeightChart7 = new ChartData
        {
            Labels = progress.WeightChart.Labels.Skip(skip).ToList(),
            Datasets = progress.WeightChart.Datasets
                .Select(d => new ChartDataset { Label = d.Label, Data = d.Data.Skip(skip).ToList() }).ToList()
        };

        var upcoming = await _db.Appointments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.StartTime >= DateTime.Now &&
                        (a.Status == AppointmentStatus.Approved || a.Status == AppointmentStatus.Pending))
            .OrderBy(a => a.StartTime).Take(3).ToListAsync();
        var names = await _directory.GetDisplayNamesAsync(upcoming.Select(a => a.TrainerId));
        model.UpcomingAppointments = upcoming.Select(a => new AppointmentListItem(a, names[a.TrainerId])).ToList();

        model.RecentNotifications = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == clientId).OrderByDescending(n => n.CreatedAtUtc).Take(5).ToListAsync();

        var feedback = await _db.TrainerFeedback.AsNoTracking()
            .Where(f => f.ClientId == clientId).OrderByDescending(f => f.CreatedAtUtc).Take(2).ToListAsync();
        var trainerNames = await _directory.GetDisplayNamesAsync(feedback.Select(f => f.TrainerId));
        model.RecentFeedback = feedback.Select(f => new FeedbackItem(f, trainerNames[f.TrainerId], null)).ToList();

        return View(model);
    }

    // Assigned workout and diet plans with adherence and plan history.
    [HttpGet]
    public async Task<IActionResult> Plans()
    {
        var clientId = User.GetUserId();
        var today = DateTime.Today;
        var (workoutAssignment, dietAssignment) = await GetActiveAssignmentsAsync(clientId);

        var adherence30 = await _analytics.GetAdherenceAsync(new[] { clientId }, today.AddDays(-29), today);
        var adherence7 = await _analytics.GetAdherenceAsync(new[] { clientId }, today.AddDays(-6), today);

        var weekStart = DateHelpers.StartOfWeek(today);
        var workoutDays = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => w.ClientId == clientId && w.LogDate >= weekStart && w.LogDate <= weekStart.AddDays(6))
            .Select(w => w.LogDate).ToListAsync();

        var trainerId = await _db.ClientProfiles.Where(c => c.UserId == clientId).Select(c => c.TrainerId).FirstOrDefaultAsync();

        var model = new ClientPlansViewModel
        {
            WorkoutAssignment = workoutAssignment,
            DietAssignment = dietAssignment,
            Adherence30 = adherence30[clientId],
            Adherence7 = adherence7[clientId],
            TrainerName = trainerId != null ? await _directory.GetDisplayNameAsync(trainerId) : null,
            History = await _db.PlanAssignments.AsNoTracking()
                .Include(a => a.WorkoutPlan).Include(a => a.DietPlan)
                .Where(a => a.ClientId == clientId)
                .OrderByDescending(a => a.StartDate).ToListAsync()
        };

        for (var i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            model.WorkoutDaysThisWeek[day.DayOfWeek] = workoutDays.Contains(day);
        }

        return View(model);
    }

    // All feedback from trainers plus post-consultation notes.
    [HttpGet]
    public async Task<IActionResult> Feedback()
    {
        var clientId = User.GetUserId();
        var feedback = await _db.TrainerFeedback.AsNoTracking()
            .Where(f => f.ClientId == clientId).OrderByDescending(f => f.CreatedAtUtc).ToListAsync();

        // Describe what each piece of feedback refers to.
        var workoutIds = feedback.Where(f => f.Target == FeedbackTarget.Workout && f.TargetId != null).Select(f => f.TargetId!.Value).ToList();
        var mealIds = feedback.Where(f => f.Target == FeedbackTarget.Meal && f.TargetId != null).Select(f => f.TargetId!.Value).ToList();
        var measurementIds = feedback.Where(f => f.Target == FeedbackTarget.Measurement && f.TargetId != null).Select(f => f.TargetId!.Value).ToList();

        var workouts = await _db.WorkoutLogs.AsNoTracking().Where(w => workoutIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id);
        var meals = await _db.MealLogs.AsNoTracking().Where(m => mealIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        var measurements = await _db.HealthMeasurements.AsNoTracking().Where(m => measurementIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        string? Describe(TrainerFeedback f)
        {
            if (f.TargetId == null) return null;
            var id = f.TargetId.Value;
            return f.Target switch
            {
                FeedbackTarget.Workout when workouts.TryGetValue(id, out var w) => $"Workout: {w.ExerciseType} on {w.LogDate:dd MMM}",
                FeedbackTarget.Meal when meals.TryGetValue(id, out var m) => $"{m.MealType}: {m.FoodItems} on {m.LogDate:dd MMM}",
                FeedbackTarget.Measurement when measurements.TryGetValue(id, out var x) => $"Measurement: {x.WeightKg:0.0} kg on {x.MeasuredOn:dd MMM}",
                _ => null
            };
        }

        var names = await _directory.GetDisplayNamesAsync(feedback.Select(f => f.TrainerId));
        ViewData["Notes"] = await _db.Appointments.AsNoTracking()
            .Where(a => a.ClientId == clientId && a.Status == AppointmentStatus.Completed)
            .OrderByDescending(a => a.StartTime).ToListAsync();

        return View(feedback.Select(f => new FeedbackItem(f, names[f.TrainerId], Describe(f))).ToList());
    }

    private async Task<(PlanAssignment? Workout, PlanAssignment? Diet)> GetActiveAssignmentsAsync(string clientId)
    {
        var today = DateTime.Today;
        var active = await _db.PlanAssignments.AsNoTracking()
            .Include(a => a.WorkoutPlan!).ThenInclude(p => p.Exercises)
            .Include(a => a.DietPlan!).ThenInclude(p => p.Meals)
            .Where(a => a.ClientId == clientId && a.IsActive && a.StartDate <= today && (a.EndDate == null || a.EndDate >= today))
            .OrderByDescending(a => a.StartDate)
            .ToListAsync();

        return (active.FirstOrDefault(a => a.WorkoutPlanId != null), active.FirstOrDefault(a => a.DietPlanId != null));
    }
}
