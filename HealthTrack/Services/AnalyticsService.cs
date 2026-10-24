using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

public interface IAnalyticsService
{
    Task<Dictionary<string, AdherenceResult>> GetAdherenceAsync(IReadOnlyCollection<string> clientIds, DateTime from, DateTime to);
    Task<ClientProgressReport> GetClientProgressAsync(string clientId, DateTime from, DateTime to, ReportGrouping grouping);
    Task<List<ClientSummaryRow>> GetClientSummariesAsync(IReadOnlyCollection<string> clientIds);
    Task<List<RoutineStatsRow>> GetRoutineStatsAsync(string? trainerId);
    Task<List<TrendAlert>> GetTrendAlertsAsync(IReadOnlyCollection<string> clientIds);
    Task<List<FeedbackEffectivenessRow>> GetFeedbackEffectivenessAsync(string? trainerId);
    Task<EngagementReport> GetEngagementAsync(IReadOnlyCollection<string>? userIds, DateTime from, DateTime to);
    Task<List<TrainerPerformanceRow>> GetTrainerPerformanceAsync(DateTime from, DateTime to);
    Task<SystemReport> GetSystemReportAsync(DateTime from, DateTime to);
    Task<CustomReportResult> RunCustomReportAsync(CustomReportRequest request);
    Task<Dictionary<string, DateTime>> GetLastActiveAsync(IReadOnlyCollection<string> userIds);
}

// Progress tracking and reporting. Data is fetched with simple, index-friendly queries
// and aggregated in memory, which keeps the algorithms easy to read and test.
public sealed class AnalyticsService : IAnalyticsService
{
    // Daily calories within ±15% of the target count as "on target".
    private const decimal DietTolerance = 0.15m;

    private readonly ApplicationDbContext _db;
    private readonly IUserDirectory _directory;

    public AnalyticsService(ApplicationDbContext db, IUserDirectory directory)
    {
        _db = db;
        _directory = directory;
    }

    // =====================================================================
    // Adherence
    // =====================================================================

    // Calculates workout and diet adherence for many clients over a date range.
    public async Task<Dictionary<string, AdherenceResult>> GetAdherenceAsync(IReadOnlyCollection<string> clientIds, DateTime from, DateTime to)
    {
        var ids = clientIds.Distinct().ToList();
        from = from.Date;
        to = to.Date > DateTime.Today ? DateTime.Today : to.Date;

        var assignments = await LoadAssignmentsAsync(ids, from, to);
        var workoutDates = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => ids.Contains(w.ClientId) && w.LogDate >= from && w.LogDate <= to)
            .Select(w => new { w.ClientId, w.LogDate })
            .ToListAsync();
        var meals = await _db.MealLogs.AsNoTracking()
            .Where(m => ids.Contains(m.ClientId) && m.LogDate >= from && m.LogDate <= to)
            .Select(m => new { m.ClientId, m.LogDate, m.Calories })
            .ToListAsync();

        var result = new Dictionary<string, AdherenceResult>();
        foreach (var id in ids)
        {
            result[id] = ComputeAdherence(
                assignments.Where(a => a.ClientId == id),
                workoutDates.Where(w => w.ClientId == id).Select(w => w.LogDate),
                meals.Where(m => m.ClientId == id).Select(m => (m.LogDate, m.Calories)),
                from, to);
        }
        return result;
    }

    // Core adherence algorithm.
    // Workout: sessions completed ÷ sessions expected (SessionsPerWeek ÷ 7 for each covered day), capped at 100%.
    // Diet: covered days whose logged calories fall within ±15% of the plan's daily target ÷ covered days.
    private static AdherenceResult ComputeAdherence(
        IEnumerable<PlanAssignment> assignments,
        IEnumerable<DateTime> workoutDates,
        IEnumerable<(DateTime Date, int Calories)> meals,
        DateTime from, DateTime to)
    {
        var assignmentList = assignments.ToList();
        var workoutDays = workoutDates.Select(d => d.Date).ToHashSet();
        var caloriesByDay = meals.GroupBy(m => m.Date.Date).ToDictionary(g => g.Key, g => g.Sum(m => m.Calories));

        var result = new AdherenceResult();
        decimal expected = 0;
        var completed = 0;
        int dietDays = 0, dietOnTarget = 0;
        var anyWorkoutPlan = false;

        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var workoutAssignment = assignmentList.FirstOrDefault(a => a.WorkoutPlan != null && Covers(a, day));
            if (workoutAssignment != null)
            {
                anyWorkoutPlan = true;
                expected += workoutAssignment.WorkoutPlan!.SessionsPerWeek / 7m;
                if (workoutDays.Contains(day)) completed++;
            }

            var dietAssignment = assignmentList.FirstOrDefault(a => a.DietPlan != null && Covers(a, day));
            if (dietAssignment != null)
            {
                dietDays++;
                var target = dietAssignment.DietPlan!.DailyCalorieTarget;
                var intake = caloriesByDay.TryGetValue(day, out var c) ? c : 0;
                if (intake >= target * (1 - DietTolerance) && intake <= target * (1 + DietTolerance))
                    dietOnTarget++;
            }
        }

        result.WorkoutsCompleted = completed;
        result.WorkoutsExpected = Math.Round(expected, 1);
        if (anyWorkoutPlan && expected > 0)
            result.WorkoutPercent = Math.Min(100m, Math.Round(completed / expected * 100m, 0));

        result.DietDaysPlanned = dietDays;
        result.DietDaysOnTarget = dietOnTarget;
        if (dietDays > 0)
            result.DietPercent = Math.Round(dietOnTarget * 100m / dietDays, 0);

        return result;
    }

    // An assignment covers a day if the day is within its date range. Assignments that were
    // switched off without an end date are treated as no longer covering anything.
    private static bool Covers(PlanAssignment a, DateTime day) =>
        a.StartDate.Date <= day.Date && (a.EndDate == null ? a.IsActive : a.EndDate.Value.Date >= day.Date);

    private Task<List<PlanAssignment>> LoadAssignmentsAsync(List<string> clientIds, DateTime from, DateTime to) =>
        _db.PlanAssignments.AsNoTracking()
            .Include(a => a.WorkoutPlan)
            .Include(a => a.DietPlan)
            .Where(a => clientIds.Contains(a.ClientId) && a.StartDate <= to && (a.EndDate == null || a.EndDate >= from))
            .ToListAsync();

    // =====================================================================
    // Client progress (client analytics page + individual reports)
    // =====================================================================

    // Builds the complete progress report for one client: charts, totals, goal progress, adherence and month-over-month comparison.
    public async Task<ClientProgressReport> GetClientProgressAsync(string clientId, DateTime from, DateTime to, ReportGrouping grouping)
    {
        from = from.Date;
        to = to.Date;
        var profile = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == clientId)
                      ?? new ClientProfile { UserId = clientId, FullName = "Client", HeightCm = 170 };

        // Load from the start of last month so the month-over-month comparison is always available.
        var lastMonthStart = DateHelpers.StartOfMonth(DateTime.Today).AddMonths(-1);
        var loadFrom = from < lastMonthStart ? from : lastMonthStart;
        var loadTo = to > DateTime.Today ? to : DateTime.Today;

        var allMeasurements = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => m.ClientId == clientId).OrderBy(m => m.MeasuredOn).ThenBy(m => m.Id).ToListAsync();
        var workouts = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => w.ClientId == clientId && w.LogDate >= loadFrom && w.LogDate <= loadTo)
            .OrderBy(w => w.LogDate).ToListAsync();
        var meals = await _db.MealLogs.AsNoTracking()
            .Where(m => m.ClientId == clientId && m.LogDate >= loadFrom && m.LogDate <= loadTo)
            .OrderBy(m => m.LogDate).ToListAsync();

        var measurementsInRange = allMeasurements.Where(m => m.MeasuredOn >= from && m.MeasuredOn <= to).ToList();
        var workoutsInRange = workouts.Where(w => w.LogDate >= from && w.LogDate <= to).ToList();
        var mealsInRange = meals.Where(m => m.LogDate >= from && m.LogDate <= to).ToList();

        // Nutrition targets come from the active diet plan, otherwise the personal target.
        var activeDiet = await _db.PlanAssignments.AsNoTracking().Include(a => a.DietPlan)
            .Where(a => a.ClientId == clientId && a.DietPlanId != null && a.IsActive)
            .OrderByDescending(a => a.StartDate).FirstOrDefaultAsync();

        var currentWeight = allMeasurements.Count > 0 ? allMeasurements[^1].WeightKg : profile.WeightKg;
        var report = new ClientProgressReport
        {
            ClientName = profile.FullName,
            From = from,
            To = to,
            Grouping = grouping,
            HeightCm = profile.HeightCm,
            CurrentWeight = currentWeight,
            CurrentBmi = BmiCalculator.Calculate(currentWeight, profile.HeightCm),
            CalorieTarget = activeDiet?.DietPlan?.DailyCalorieTarget ?? profile.DailyCalorieTarget ?? 2000,
            ProteinTarget = activeDiet?.DietPlan?.ProteinTargetG ?? 0,
            CarbsTarget = activeDiet?.DietPlan?.CarbsTargetG ?? 0,
            FatTarget = activeDiet?.DietPlan?.FatTargetG ?? 0,
            Measurements = measurementsInRange,
            Workouts = workoutsInRange,
            Meals = mealsInRange
        };
        report.BmiCategory = BmiCalculator.Category(report.CurrentBmi);

        var periods = Periods(from, to, grouping);
        var labels = periods.Select(p => PeriodLabel(p, grouping)).ToList();

        // Weight & BMI trend (average per period, gaps where nothing was measured).
        report.WeightChart.Labels = labels;
        report.WeightChart.Datasets.Add(new ChartDataset
        {
            Label = "Weight (kg)",
            Data = periods.Select(p => AverageOrNull(measurementsInRange.Where(m => PeriodStart(m.MeasuredOn, grouping) == p).Select(m => m.WeightKg))).ToList()
        });
        if (profile.TargetWeightKg.HasValue)
            report.WeightChart.Datasets.Add(new ChartDataset { Label = "Target", Data = periods.Select(_ => (decimal?)profile.TargetWeightKg).ToList() });

        report.BmiChart.Labels = labels;
        report.BmiChart.Datasets.Add(new ChartDataset
        {
            Label = "BMI",
            Data = periods.Select(p => AverageOrNull(measurementsInRange.Where(m => PeriodStart(m.MeasuredOn, grouping) == p).Select(m => m.Bmi))).ToList()
        });

        // Calories burned (sum per period).
        report.CaloriesBurnedChart.Labels = labels;
        report.CaloriesBurnedChart.Datasets.Add(new ChartDataset
        {
            Label = "Calories burned",
            Data = periods.Select(p => (decimal?)workoutsInRange.Where(w => PeriodStart(w.LogDate, grouping) == p).Sum(w => w.CaloriesBurned)).ToList()
        });

        // Calorie intake: average per logged day in each period vs the daily target.
        report.NutritionChart.Labels = labels;
        report.NutritionChart.Datasets.Add(new ChartDataset
        {
            Label = "Average daily intake (kcal)",
            Type = "bar",
            Data = periods.Select(p => AverageDailyCalories(mealsInRange.Where(m => PeriodStart(m.LogDate, grouping) == p))).ToList()
        });
        report.NutritionChart.Datasets.Add(new ChartDataset
        {
            Label = "Daily target (kcal)",
            Type = "line",
            Data = periods.Select(_ => (decimal?)report.CalorieTarget).ToList()
        });

        // Macronutrients: average grams per logged day vs targets.
        var loggedDays = Math.Max(1, mealsInRange.Select(m => m.LogDate.Date).Distinct().Count());
        report.MacroChart.Labels = new List<string> { "Protein", "Carbohydrates", "Fat" };
        report.MacroChart.Datasets.Add(new ChartDataset
        {
            Label = "Average per day (g)",
            Data = new List<decimal?>
            {
                Math.Round(mealsInRange.Sum(m => m.ProteinG) / loggedDays, 0),
                Math.Round(mealsInRange.Sum(m => m.CarbsG) / loggedDays, 0),
                Math.Round(mealsInRange.Sum(m => m.FatG) / loggedDays, 0)
            }
        });
        if (report.ProteinTarget + report.CarbsTarget + report.FatTarget > 0)
            report.MacroChart.Datasets.Add(new ChartDataset
            {
                Label = "Target (g)",
                Data = new List<decimal?> { report.ProteinTarget, report.CarbsTarget, report.FatTarget }
            });

        // Workout frequency is always shown per week (sessions and active minutes).
        var weeks = Periods(from, to, ReportGrouping.Week);
        report.WorkoutFrequencyChart.Labels = weeks.Select(w => PeriodLabel(w, ReportGrouping.Week)).ToList();
        report.WorkoutFrequencyChart.Datasets.Add(new ChartDataset
        {
            Label = "Sessions",
            Data = weeks.Select(w => (decimal?)workoutsInRange.Count(x => DateHelpers.StartOfWeek(x.LogDate) == w)).ToList()
        });

        // Totals.
        report.TotalWorkouts = workoutsInRange.Count;
        report.TotalWorkoutMinutes = workoutsInRange.Sum(w => w.DurationMinutes);
        report.TotalCaloriesBurned = workoutsInRange.Sum(w => w.CaloriesBurned);
        report.AverageDailyIntake = AverageDailyCalories(mealsInRange) ?? 0;
        if (measurementsInRange.Count >= 2)
            report.WeightChange = measurementsInRange[^1].WeightKg - measurementsInRange[0].WeightKg;

        report.Goal = BuildGoalProgress(profile, allMeasurements);
        report.Adherence = (await GetAdherenceAsync(new[] { clientId }, from, to))[clientId];
        report.MonthComparison = await BuildMonthComparisonAsync(clientId, allMeasurements, workouts, meals);

        return report;
    }

    private static GoalProgress BuildGoalProgress(ClientProfile profile, List<HealthMeasurement> allMeasurements)
    {
        var start = allMeasurements.Count > 0 ? allMeasurements[0].WeightKg : profile.WeightKg;
        var current = allMeasurements.Count > 0 ? allMeasurements[^1].WeightKg : profile.WeightKg;
        var goal = new GoalProgress { StartWeight = start, CurrentWeight = current, TargetWeight = profile.TargetWeightKg };

        if (profile.TargetWeightKg is not decimal target)
        {
            goal.Description = "Set a target weight in your profile to track goal progress.";
            return goal;
        }

        var totalChangeNeeded = start - target;
        if (totalChangeNeeded == 0)
        {
            goal.Percent = current == target ? 100m : 0m;
        }
        else
        {
            var achieved = start - current;
            goal.Percent = Math.Clamp(Math.Round(achieved / totalChangeNeeded * 100m, 0), 0m, 100m);
        }

        var remaining = Math.Abs(current - target);
        goal.Description = remaining < 0.1m
            ? "Goal reached — great work!"
            : $"{remaining:0.0} kg to go ({(target < current ? "lose" : "gain")}) to reach {target:0.0} kg.";
        return goal;
    }

    private async Task<List<PeriodComparison>> BuildMonthComparisonAsync(string clientId,
        List<HealthMeasurement> measurements, List<WorkoutLog> workouts, List<MealLog> meals)
    {
        var thisStart = DateHelpers.StartOfMonth(DateTime.Today);
        var lastStart = thisStart.AddMonths(-1);
        var lastEnd = thisStart.AddDays(-1);
        var today = DateTime.Today;

        bool InThis(DateTime d) => d >= thisStart && d <= today;
        bool InLast(DateTime d) => d >= lastStart && d <= lastEnd;

        var adherenceThis = (await GetAdherenceAsync(new[] { clientId }, thisStart, today))[clientId].OverallPercent;
        var adherenceLast = (await GetAdherenceAsync(new[] { clientId }, lastStart, lastEnd))[clientId].OverallPercent;

        return new List<PeriodComparison>
        {
            new("Average weight",
                AverageOrNull(measurements.Where(m => InLast(m.MeasuredOn)).Select(m => m.WeightKg)),
                AverageOrNull(measurements.Where(m => InThis(m.MeasuredOn)).Select(m => m.WeightKg)), "kg", false),
            new("Workouts completed",
                workouts.Count(w => InLast(w.LogDate)), workouts.Count(w => InThis(w.LogDate)), "", true),
            new("Active minutes",
                workouts.Where(w => InLast(w.LogDate)).Sum(w => w.DurationMinutes),
                workouts.Where(w => InThis(w.LogDate)).Sum(w => w.DurationMinutes), "min", true),
            new("Calories burned",
                workouts.Where(w => InLast(w.LogDate)).Sum(w => w.CaloriesBurned),
                workouts.Where(w => InThis(w.LogDate)).Sum(w => w.CaloriesBurned), "kcal", true),
            new("Average daily intake",
                AverageDailyCalories(meals.Where(m => InLast(m.LogDate))),
                AverageDailyCalories(meals.Where(m => InThis(m.LogDate))), "kcal", false),
            new("Plan adherence", adherenceLast, adherenceThis, "%", true)
        };
    }

    // =====================================================================
    // Trainer views: client summaries, routines, trends, feedback effectiveness
    // =====================================================================

    // One summary row per client for trainer dashboards and comparative reports.
    public async Task<List<ClientSummaryRow>> GetClientSummariesAsync(IReadOnlyCollection<string> clientIds)
    {
        var ids = clientIds.Distinct().ToList();
        if (ids.Count == 0) return new List<ClientSummaryRow>();

        var today = DateTime.Today;
        var from30 = today.AddDays(-29);

        var profiles = await _db.ClientProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId);
        var measurements = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => ids.Contains(m.ClientId))
            .Select(m => new { m.ClientId, m.MeasuredOn, m.WeightKg, m.Bmi, m.Id })
            .ToListAsync();
        var workouts30 = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => ids.Contains(w.ClientId) && w.LogDate >= from30)
            .Select(w => w.ClientId).ToListAsync();
        var meals30 = await _db.MealLogs.AsNoTracking()
            .Where(m => ids.Contains(m.ClientId) && m.LogDate >= from30)
            .Select(m => m.ClientId).ToListAsync();
        var nextAppointments = await _db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.ClientId) && a.StartTime >= DateTime.Now &&
                        (a.Status == AppointmentStatus.Approved || a.Status == AppointmentStatus.Pending))
            .Select(a => new { a.ClientId, a.StartTime })
            .ToListAsync();

        var adherence = await GetAdherenceAsync(ids, from30, today);
        var lastActive = await GetLastActiveAsync(ids);
        var alerts = await GetTrendAlertsAsync(ids);

        var rows = new List<ClientSummaryRow>();
        foreach (var id in ids)
        {
            profiles.TryGetValue(id, out var profile);
            var history = measurements.Where(m => m.ClientId == id).OrderBy(m => m.MeasuredOn).ThenBy(m => m.Id).ToList();
            var latest = history.LastOrDefault();
            var baseline = history.LastOrDefault(m => m.MeasuredOn <= from30) ?? history.FirstOrDefault(m => m.MeasuredOn >= from30);

            decimal? goalPercent = null;
            if (profile?.TargetWeightKg is decimal target && history.Count > 0)
            {
                var start = history[0].WeightKg;
                var needed = start - target;
                goalPercent = needed == 0 ? 100m : Math.Clamp(Math.Round((start - latest!.WeightKg) / needed * 100m, 0), 0m, 100m);
            }

            var workoutCount = workouts30.Count(c => c == id);
            rows.Add(new ClientSummaryRow
            {
                ClientId = id,
                Name = profile?.FullName ?? "Client",
                LastActiveUtc = lastActive.TryGetValue(id, out var seen) ? seen : (DateTime?)null,
                CurrentWeight = latest?.WeightKg ?? profile?.WeightKg,
                Bmi = latest?.Bmi ?? (profile != null ? BmiCalculator.Calculate(profile.WeightKg, profile.HeightCm) : (decimal?)null),
                WeightChange30Days = latest != null && baseline != null && latest.Id != baseline.Id ? latest.WeightKg - baseline.WeightKg : (decimal?)null,
                AdherencePercent = adherence[id].OverallPercent,
                WorkoutAdherence = adherence[id].WorkoutPercent,
                DietAdherence = adherence[id].DietPercent,
                Workouts30Days = workoutCount,
                Logs30Days = workoutCount + meals30.Count(c => c == id) + history.Count(m => m.MeasuredOn >= from30),
                GoalPercent = goalPercent,
                NextAppointment = nextAppointments.Where(a => a.ClientId == id).Select(a => (DateTime?)a.StartTime).OrderBy(t => t).FirstOrDefault(),
                Alerts = alerts.Where(a => a.ClientId == id).Select(a => a.Message).ToList()
            });
        }
        return rows.OrderBy(r => r.Name).ToList();
    }

    // Popularity (times assigned, active clients) and effectiveness (adherence, weight change) of each plan.
    public async Task<List<RoutineStatsRow>> GetRoutineStatsAsync(string? trainerId)
    {
        var workoutPlans = await _db.WorkoutPlans.AsNoTracking()
            .Where(p => trainerId == null || p.CreatedById == trainerId)
            .Select(p => new { p.Id, p.Title, p.CreatedById, p.SessionsPerWeek }).ToListAsync();
        var dietPlans = await _db.DietPlans.AsNoTracking()
            .Where(p => trainerId == null || p.CreatedById == trainerId)
            .Select(p => new { p.Id, p.Title, p.CreatedById }).ToListAsync();

        var workoutIds = workoutPlans.Select(p => p.Id).ToList();
        var dietIds = dietPlans.Select(p => p.Id).ToList();

        var assignments = await _db.PlanAssignments.AsNoTracking()
            .Include(a => a.WorkoutPlan).Include(a => a.DietPlan)
            .Where(a => (a.WorkoutPlanId != null && workoutIds.Contains(a.WorkoutPlanId.Value)) ||
                        (a.DietPlanId != null && dietIds.Contains(a.DietPlanId.Value)))
            .ToListAsync();

        var clientIds = assignments.Select(a => a.ClientId).Distinct().ToList();
        var earliest = assignments.Count == 0 ? DateTime.Today : assignments.Min(a => a.StartDate);
        var workoutDates = await _db.WorkoutLogs.AsNoTracking()
            .Where(w => clientIds.Contains(w.ClientId) && w.LogDate >= earliest)
            .Select(w => new { w.ClientId, w.LogDate }).ToListAsync();
        var meals = await _db.MealLogs.AsNoTracking()
            .Where(m => clientIds.Contains(m.ClientId) && m.LogDate >= earliest)
            .Select(m => new { m.ClientId, m.LogDate, m.Calories }).ToListAsync();
        var weights = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => clientIds.Contains(m.ClientId))
            .Select(m => new { m.ClientId, m.MeasuredOn, m.WeightKg }).ToListAsync();

        var creatorNames = await _directory.GetDisplayNamesAsync(
            workoutPlans.Select(p => p.CreatedById).Concat(dietPlans.Select(p => p.CreatedById)).Where(id => id != null)!);

        // Evaluates one assignment on its own: adherence and weight change during the assignment.
        (decimal? Adherence, decimal? WeightChange) Evaluate(PlanAssignment a)
        {
            var start = a.StartDate.Date;
            var end = a.EndDate?.Date ?? DateTime.Today;
            if (end > DateTime.Today) end = DateTime.Today;
            if (end < start) return (null, null);

            var adherence = ComputeAdherence(new[] { a },
                workoutDates.Where(w => w.ClientId == a.ClientId).Select(w => w.LogDate),
                meals.Where(m => m.ClientId == a.ClientId).Select(m => (m.LogDate, m.Calories)),
                start, end);
            var percent = a.WorkoutPlanId != null ? adherence.WorkoutPercent : adherence.DietPercent;

            var during = weights.Where(w => w.ClientId == a.ClientId && w.MeasuredOn >= start.AddDays(-7) && w.MeasuredOn <= end)
                .OrderBy(w => w.MeasuredOn).ToList();
            decimal? change = during.Count >= 2 ? during[^1].WeightKg - during[0].WeightKg : (decimal?)null;
            return (percent, change);
        }

        RoutineStatsRow BuildRow(int id, string type, string title, string? createdBy, List<PlanAssignment> planAssignments)
        {
            var evaluations = planAssignments.Select(Evaluate).ToList();
            return new RoutineStatsRow
            {
                PlanId = id,
                PlanType = type,
                Title = title,
                CreatedBy = createdBy != null && creatorNames.TryGetValue(createdBy, out var n) ? n : "System",
                TimesAssigned = planAssignments.Count,
                ActiveClients = planAssignments.Where(a => Covers(a, DateTime.Today)).Select(a => a.ClientId).Distinct().Count(),
                AverageAdherence = AverageOrNull(evaluations.Where(e => e.Adherence.HasValue).Select(e => e.Adherence!.Value)),
                AverageWeightChange = AverageOrNull(evaluations.Where(e => e.WeightChange.HasValue).Select(e => e.WeightChange!.Value))
            };
        }

        var rows = new List<RoutineStatsRow>();
        rows.AddRange(workoutPlans.Select(p => BuildRow(p.Id, "Workout", p.Title, p.CreatedById,
            assignments.Where(a => a.WorkoutPlanId == p.Id).ToList())));
        rows.AddRange(dietPlans.Select(p => BuildRow(p.Id, "Diet", p.Title, p.CreatedById,
            assignments.Where(a => a.DietPlanId == p.Id).ToList())));

        return rows.OrderByDescending(r => r.TimesAssigned).ThenByDescending(r => r.AverageAdherence ?? 0).ToList();
    }

    // Detects patterns that need proactive intervention: inactivity, falling adherence,
    // weight-loss plateaus and weight moving away from the goal.
    public async Task<List<TrendAlert>> GetTrendAlertsAsync(IReadOnlyCollection<string> clientIds)
    {
        var ids = clientIds.Distinct().ToList();
        var alerts = new List<TrendAlert>();
        if (ids.Count == 0) return alerts;

        var today = DateTime.Today;
        var names = await _directory.GetDisplayNamesAsync(ids);
        var profiles = await _db.ClientProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId);
        var recentWorkouts = await _db.WorkoutLogs.AsNoTracking().Where(w => ids.Contains(w.ClientId) && w.LogDate >= today.AddDays(-7)).Select(w => w.ClientId).ToListAsync();
        var recentMeals = await _db.MealLogs.AsNoTracking().Where(m => ids.Contains(m.ClientId) && m.LogDate >= today.AddDays(-7)).Select(m => m.ClientId).ToListAsync();
        var weights = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => ids.Contains(m.ClientId) && m.MeasuredOn >= today.AddDays(-30))
            .Select(m => new { m.ClientId, m.MeasuredOn, m.WeightKg }).ToListAsync();

        var lastWeek = await GetAdherenceAsync(ids, today.AddDays(-6), today);
        var previousWeek = await GetAdherenceAsync(ids, today.AddDays(-13), today.AddDays(-7));

        foreach (var id in ids)
        {
            var name = names[id];
            var clientWeights = weights.Where(w => w.ClientId == id).OrderBy(w => w.MeasuredOn).ToList();

            if (!recentWorkouts.Contains(id) && !recentMeals.Contains(id) && !clientWeights.Any(w => w.MeasuredOn >= today.AddDays(-7)))
                alerts.Add(new TrendAlert(id, name, "warning", "No activity logged in the last 7 days."));

            var now = lastWeek[id].WorkoutPercent;
            var before = previousWeek[id].WorkoutPercent;
            if (now.HasValue && before.HasValue && before - now >= 25)
                alerts.Add(new TrendAlert(id, name, "warning", $"Workout adherence dropped from {before:0}% to {now:0}% this week."));

            if (profiles.TryGetValue(id, out var profile) && profile.TargetWeightKg is decimal target && clientWeights.Count >= 2)
            {
                var first = clientWeights[0];
                var last = clientWeights[^1];
                var wantsToLose = target < last.WeightKg;
                var span = (last.MeasuredOn - first.MeasuredOn).TotalDays;

                var recent = clientWeights.Where(w => w.MeasuredOn >= today.AddDays(-21)).ToList();
                if (recent.Count >= 2 && (recent[^1].MeasuredOn - recent[0].MeasuredOn).TotalDays >= 14 &&
                    Math.Abs(recent[^1].WeightKg - recent[0].WeightKg) < 0.3m && Math.Abs(last.WeightKg - target) > 1)
                    alerts.Add(new TrendAlert(id, name, "info", "Weight has plateaued for 2+ weeks. Consider adjusting the plan."));

                if (span >= 7 && wantsToLose && last.WeightKg - first.WeightKg > 1)
                    alerts.Add(new TrendAlert(id, name, "warning", $"Weight increased by {last.WeightKg - first.WeightKg:0.0} kg in 30 days against a weight-loss goal."));
            }
        }
        return alerts;
    }

    // Compares each client's logging activity in the week before and after feedback.
    public async Task<List<FeedbackEffectivenessRow>> GetFeedbackEffectivenessAsync(string? trainerId)
    {
        var since = DateTime.UtcNow.AddDays(-90);
        var feedback = await _db.TrainerFeedback.AsNoTracking()
            .Where(f => (trainerId == null || f.TrainerId == trainerId) && f.CreatedAtUtc >= since)
            .OrderByDescending(f => f.CreatedAtUtc).Take(100).ToListAsync();
        if (feedback.Count == 0) return new List<FeedbackEffectivenessRow>();

        var clientIds = feedback.Select(f => f.ClientId).Distinct().ToList();
        var from = since.ToLocalTime().Date.AddDays(-7);
        var dates = new List<(string ClientId, DateTime Date)>();
        dates.AddRange((await _db.WorkoutLogs.AsNoTracking().Where(w => clientIds.Contains(w.ClientId) && w.LogDate >= from)
            .Select(w => new { w.ClientId, w.LogDate }).ToListAsync()).Select(x => (x.ClientId, x.LogDate)));
        dates.AddRange((await _db.MealLogs.AsNoTracking().Where(m => clientIds.Contains(m.ClientId) && m.LogDate >= from)
            .Select(m => new { m.ClientId, m.LogDate }).ToListAsync()).Select(x => (x.ClientId, x.LogDate)));
        dates.AddRange((await _db.HealthMeasurements.AsNoTracking().Where(m => clientIds.Contains(m.ClientId) && m.MeasuredOn >= from)
            .Select(m => new { m.ClientId, m.MeasuredOn }).ToListAsync()).Select(x => (x.ClientId, x.MeasuredOn)));

        var names = await _directory.GetDisplayNamesAsync(clientIds);
        return feedback.Select(f =>
        {
            var given = f.CreatedAtUtc.ToLocalTime().Date;
            return new FeedbackEffectivenessRow
            {
                ClientName = names[f.ClientId],
                GivenAtUtc = f.CreatedAtUtc,
                Message = f.Message,
                LogsWeekBefore = dates.Count(d => d.ClientId == f.ClientId && d.Date >= given.AddDays(-7) && d.Date < given),
                LogsWeekAfter = dates.Count(d => d.ClientId == f.ClientId && d.Date >= given && d.Date < given.AddDays(7))
            };
        }).ToList();
    }

    // =====================================================================
    // Engagement, trainer performance, system report, custom reports
    // =====================================================================

    // Most recent audit-log activity time for each user.
    public async Task<Dictionary<string, DateTime>> GetLastActiveAsync(IReadOnlyCollection<string> userIds)
    {
        var ids = userIds.Distinct().ToList();
        var rows = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId != null && ids.Contains(a.UserId))
            .GroupBy(a => a.UserId!)
            .Select(g => new { UserId = g.Key, Last = g.Max(a => a.CreatedAtUtc) })
            .ToListAsync();
        return rows.ToDictionary(r => r.UserId, r => r.Last);
    }

    // Login and logging activity, active users, retention and per-user engagement.
    public async Task<EngagementReport> GetEngagementAsync(IReadOnlyCollection<string>? userIds, DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.AddDays(1).ToUniversalTime();

        // Population: the given users, or every client and trainer.
        List<string> ids;
        if (userIds != null)
            ids = userIds.Distinct().ToList();
        else
            ids = await _db.ClientProfiles.Select(p => p.UserId).Concat(_db.TrainerProfiles.Select(p => p.UserId)).ToListAsync();

        // Load enough audit history for both the requested range and the 7/30-day activity figures.
        var thirtyDaysAgoUtc = DateTime.UtcNow.AddDays(-30);
        var activitySince = fromUtc < thirtyDaysAgoUtc ? fromUtc : thirtyDaysAgoUtc;
        var activity = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId != null && ids.Contains(a.UserId) && a.CreatedAtUtc >= activitySince)
            .Select(a => new { UserId = a.UserId!, a.Action, a.CreatedAtUtc })
            .ToListAsync();

        var logDates = new List<(string UserId, DateTime Date)>();
        logDates.AddRange((await _db.WorkoutLogs.AsNoTracking().Where(w => ids.Contains(w.ClientId) && w.LogDate >= from && w.LogDate <= to)
            .Select(w => new { w.ClientId, w.LogDate }).ToListAsync()).Select(x => (x.ClientId, x.LogDate)));
        logDates.AddRange((await _db.MealLogs.AsNoTracking().Where(m => ids.Contains(m.ClientId) && m.LogDate >= from && m.LogDate <= to)
            .Select(m => new { m.ClientId, m.LogDate }).ToListAsync()).Select(x => (x.ClientId, x.LogDate)));
        logDates.AddRange((await _db.HealthMeasurements.AsNoTracking().Where(m => ids.Contains(m.ClientId) && m.MeasuredOn >= from && m.MeasuredOn <= to)
            .Select(m => new { m.ClientId, m.MeasuredOn }).ToListAsync()).Select(x => (x.ClientId, x.MeasuredOn)));

        var logins = activity.Where(a => a.Action == "Login.Success" && a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc).ToList();

        var report = new EngagementReport
        {
            TotalUsers = ids.Count,
            ActiveLast7Days = activity.Where(a => a.CreatedAtUtc >= DateTime.UtcNow.AddDays(-7)).Select(a => a.UserId).Distinct().Count(),
            ActiveLast30Days = activity.Where(a => a.CreatedAtUtc >= DateTime.UtcNow.AddDays(-30)).Select(a => a.UserId).Distinct().Count()
        };

        // Retention: of the users who joined more than 30 days ago, how many were active in the last 30 days.
        var joined = await _db.ClientProfiles.Where(p => ids.Contains(p.UserId)).Select(p => new { p.UserId, p.CreatedAtUtc }).ToListAsync();
        joined.AddRange(await _db.TrainerProfiles.Where(p => ids.Contains(p.UserId)).Select(p => new { p.UserId, p.CreatedAtUtc }).ToListAsync());
        var established = joined.Where(j => j.CreatedAtUtc < DateTime.UtcNow.AddDays(-30)).Select(j => j.UserId).ToHashSet();
        var activeRecently = activity.Where(a => a.CreatedAtUtc >= DateTime.UtcNow.AddDays(-30)).Select(a => a.UserId).ToHashSet();
        report.RetentionPercent = established.Count == 0 ? 100m : Math.Round(established.Count(activeRecently.Contains) * 100m / established.Count, 0);

        var days = Periods(from, to, ReportGrouping.Day);
        report.DailyActivityChart.Labels = days.Select(d => PeriodLabel(d, ReportGrouping.Day)).ToList();
        report.DailyActivityChart.Datasets.Add(new ChartDataset
        {
            Label = "Logins",
            Data = days.Select(d => (decimal?)logins.Count(l => l.CreatedAtUtc.ToLocalTime().Date == d)).ToList()
        });
        report.DailyActivityChart.Datasets.Add(new ChartDataset
        {
            Label = "Log entries",
            Data = days.Select(d => (decimal?)logDates.Count(l => l.Date.Date == d)).ToList()
        });

        var names = await _directory.GetDisplayNamesAsync(ids);
        var clientIds = await _db.ClientProfiles.Where(p => ids.Contains(p.UserId)).Select(p => p.UserId).ToListAsync();
        var adherence = await GetAdherenceAsync(clientIds, from, to);
        var lastActive = activity.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Max(a => a.CreatedAtUtc));

        report.Rows = ids.Select(id => new EngagementRow
        {
            UserId = id,
            Name = names[id],
            Logins = logins.Count(l => l.UserId == id),
            LogEntries = logDates.Count(l => l.UserId == id),
            AdherencePercent = adherence.TryGetValue(id, out var clientAdherence) ? clientAdherence.OverallPercent : null,
            LastActiveUtc = lastActive.TryGetValue(id, out var seen) ? seen : (DateTime?)null
        }).OrderByDescending(r => r.Logins + r.LogEntries).ToList();

        return report;
    }

    // Compares trainers by client adherence, weight change, completed consultations, cancellations and feedback.
    public async Task<List<TrainerPerformanceRow>> GetTrainerPerformanceAsync(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.AddDays(1).ToUniversalTime();

        var trainers = await _db.TrainerProfiles.AsNoTracking().Where(t => t.IsApproved)
            .Select(t => new { t.UserId, t.FullName }).ToListAsync();
        var clients = await _db.ClientProfiles.AsNoTracking().Where(c => c.TrainerId != null)
            .Select(c => new { c.UserId, TrainerId = c.TrainerId! }).ToListAsync();
        var clientIds = clients.Select(c => c.UserId).ToList();

        var adherence = await GetAdherenceAsync(clientIds, from, to);
        var weights = await _db.HealthMeasurements.AsNoTracking()
            .Where(m => clientIds.Contains(m.ClientId) && m.MeasuredOn >= from && m.MeasuredOn <= to)
            .Select(m => new { m.ClientId, m.MeasuredOn, m.WeightKg }).ToListAsync();
        var appointments = await _db.Appointments.AsNoTracking()
            .Where(a => a.StartTime >= from && a.StartTime < to.AddDays(1))
            .Select(a => new { a.TrainerId, a.Status }).ToListAsync();
        var feedback = await _db.TrainerFeedback.AsNoTracking()
            .Where(f => f.CreatedAtUtc >= fromUtc && f.CreatedAtUtc < toUtc)
            .Select(f => f.TrainerId).ToListAsync();
        var workoutPlanCreators = await _db.WorkoutPlans.AsNoTracking().Select(p => p.CreatedById).ToListAsync();
        var dietPlanCreators = await _db.DietPlans.AsNoTracking().Select(p => p.CreatedById).ToListAsync();

        decimal? WeightChange(string clientId)
        {
            var list = weights.Where(w => w.ClientId == clientId).OrderBy(w => w.MeasuredOn).ToList();
            return list.Count >= 2 ? list[^1].WeightKg - list[0].WeightKg : (decimal?)null;
        }

        return trainers.Select(t =>
        {
            var myClients = clients.Where(c => c.TrainerId == t.UserId).Select(c => c.UserId).ToList();
            var myAppointments = appointments.Where(a => a.TrainerId == t.UserId).ToList();
            var decided = myAppointments.Count(a => a.Status != AppointmentStatus.Pending);
            return new TrainerPerformanceRow
            {
                TrainerId = t.UserId,
                Name = t.FullName,
                Clients = myClients.Count,
                AverageAdherence = AverageOrNull(myClients.Select(c => adherence[c].OverallPercent).Where(v => v.HasValue).Select(v => v!.Value)),
                AverageWeightChange = AverageOrNull(myClients.Select(WeightChange).Where(v => v.HasValue).Select(v => v!.Value)),
                CompletedAppointments = myAppointments.Count(a => a.Status == AppointmentStatus.Completed),
                CancellationRate = decided == 0 ? (decimal?)null : Math.Round(myAppointments.Count(a => a.Status == AppointmentStatus.Cancelled) * 100m / decided, 0),
                FeedbackGiven = feedback.Count(f => f == t.UserId),
                PlansCreated = workoutPlanCreators.Count(c => c == t.UserId) + dietPlanCreators.Count(c => c == t.UserId)
            };
        }).OrderByDescending(r => r.AverageAdherence ?? -1).ToList();
    }

    // System-wide administrator report: outcomes, goals achieved, trainer performance, feature usage, peak hours and registrations.
    public async Task<SystemReport> GetSystemReportAsync(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.AddDays(1).ToUniversalTime();

        var report = new SystemReport { From = from, To = to };

        var profiles = await _db.ClientProfiles.AsNoTracking().ToListAsync();
        var clientIds = profiles.Select(p => p.UserId).ToList();
        report.ClientCount = clientIds.Count;

        var allWeights = await _db.HealthMeasurements.AsNoTracking()
            .Select(m => new { m.ClientId, m.MeasuredOn, m.WeightKg, m.Id }).ToListAsync();

        var changes = new List<decimal>();
        foreach (var id in clientIds)
        {
            var inRange = allWeights.Where(w => w.ClientId == id && w.MeasuredOn >= from && w.MeasuredOn <= to)
                .OrderBy(w => w.MeasuredOn).ThenBy(w => w.Id).ToList();
            if (inRange.Count >= 2) changes.Add(inRange[^1].WeightKg - inRange[0].WeightKg);
        }
        report.AverageWeightChange = AverageOrNull(changes);

        var adherence = await GetAdherenceAsync(clientIds, from, to);
        report.AverageAdherence = AverageOrNull(adherence.Values.Where(a => a.OverallPercent.HasValue).Select(a => a.OverallPercent!.Value));

        // Goal achievement: clients with a target whose latest weight has reached it.
        foreach (var profile in profiles.Where(p => p.TargetWeightKg.HasValue))
        {
            report.ClientsWithGoals++;
            var history = allWeights.Where(w => w.ClientId == profile.UserId).OrderBy(w => w.MeasuredOn).ThenBy(w => w.Id).ToList();
            var start = history.Count > 0 ? history[0].WeightKg : profile.WeightKg;
            var current = history.Count > 0 ? history[^1].WeightKg : profile.WeightKg;
            var target = profile.TargetWeightKg!.Value;
            var reached = start >= target ? current <= target : current >= target;
            if (reached) report.GoalsAchieved++;
        }

        report.Trainers = await GetTrainerPerformanceAsync(from, to);
        report.PopularRoutines = (await GetRoutineStatsAsync(null)).Take(10).ToList();
        report.Engagement = await GetEngagementAsync(null, from, to);

        var audit = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc)
            .Select(a => new { a.Category, a.CreatedAtUtc }).ToListAsync();

        var byCategory = audit.GroupBy(a => a.Category).OrderByDescending(g => g.Count()).ToList();
        report.FeatureUsageChart.Labels = byCategory.Select(g => g.Key).ToList();
        report.FeatureUsageChart.Datasets.Add(new ChartDataset { Label = "Actions", Data = byCategory.Select(g => (decimal?)g.Count()).ToList() });

        report.PeakHoursChart.Labels = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
        report.PeakHoursChart.Datasets.Add(new ChartDataset
        {
            Label = "Activity",
            Data = Enumerable.Range(0, 24).Select(h => (decimal?)audit.Count(a => a.CreatedAtUtc.ToLocalTime().Hour == h)).ToList()
        });

        var trainerJoins = await _db.TrainerProfiles.AsNoTracking().Select(t => t.CreatedAtUtc).ToListAsync();
        var weeks = Periods(from, to, ReportGrouping.Week);
        report.RegistrationsChart.Labels = weeks.Select(w => PeriodLabel(w, ReportGrouping.Week)).ToList();
        report.RegistrationsChart.Datasets.Add(new ChartDataset
        {
            Label = "Clients",
            Data = weeks.Select(w => (decimal?)profiles.Count(p => DateHelpers.StartOfWeek(p.CreatedAtUtc.ToLocalTime()) == w)).ToList()
        });
        report.RegistrationsChart.Datasets.Add(new ChartDataset
        {
            Label = "Trainers",
            Data = weeks.Select(w => (decimal?)trainerJoins.Count(t => DateHelpers.StartOfWeek(t.ToLocalTime()) == w)).ToList()
        });

        return report;
    }

    // Custom report builder: any combination of metrics, a date range, a grouping and an
    // optional user segment (clients of one trainer and/or one gender).
    public async Task<CustomReportResult> RunCustomReportAsync(CustomReportRequest request)
    {
        var from = request.From.Date;
        var to = request.To.Date < from ? from : request.To.Date;
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.AddDays(1).ToUniversalTime();
        var grouping = request.Grouping;

        // Resolve the user segment. Null means "everyone".
        List<string>? segment = null;
        var segmentParts = new List<string>();
        if (!string.IsNullOrEmpty(request.TrainerId) || !string.IsNullOrEmpty(request.Gender))
        {
            var query = _db.ClientProfiles.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(request.TrainerId))
            {
                query = query.Where(c => c.TrainerId == request.TrainerId);
                segmentParts.Add("clients of " + await _directory.GetDisplayNameAsync(request.TrainerId));
            }
            if (!string.IsNullOrEmpty(request.Gender))
            {
                query = query.Where(c => c.Gender == request.Gender);
                segmentParts.Add("gender: " + request.Gender);
            }
            segment = await query.Select(c => c.UserId).ToListAsync();
        }

        var periods = Periods(from, to, grouping);
        var result = new CustomReportResult
        {
            Periods = periods.Select(p => PeriodLabel(p, grouping)).ToList(),
            SegmentDescription = segmentParts.Count == 0 ? "All users" : string.Join(", ", segmentParts)
        };
        result.Chart.Labels = result.Periods;

        foreach (var metric in request.Metrics.Distinct())
        {
            // Each metric produces (date, value) points that are then bucketed by period.
            List<(DateTime Date, decimal Value)> points;
            var average = false;
            switch (metric)
            {
                case ReportMetric.WorkoutsLogged:
                case ReportMetric.CaloriesBurned:
                {
                    var q = _db.WorkoutLogs.AsNoTracking().Where(w => w.LogDate >= from && w.LogDate <= to);
                    if (segment != null) q = q.Where(w => segment.Contains(w.ClientId));
                    var rows = await q.Select(w => new { w.LogDate, w.CaloriesBurned }).ToListAsync();
                    points = rows.Select(r => (r.LogDate, metric == ReportMetric.WorkoutsLogged ? 1m : r.CaloriesBurned)).ToList();
                    break;
                }
                case ReportMetric.MealsLogged:
                case ReportMetric.CaloriesConsumed:
                {
                    var q = _db.MealLogs.AsNoTracking().Where(m => m.LogDate >= from && m.LogDate <= to);
                    if (segment != null) q = q.Where(m => segment.Contains(m.ClientId));
                    var rows = await q.Select(m => new { m.LogDate, m.Calories }).ToListAsync();
                    points = rows.Select(r => (r.LogDate, metric == ReportMetric.MealsLogged ? 1m : r.Calories)).ToList();
                    break;
                }
                case ReportMetric.Measurements:
                case ReportMetric.AverageWeight:
                {
                    var q = _db.HealthMeasurements.AsNoTracking().Where(m => m.MeasuredOn >= from && m.MeasuredOn <= to);
                    if (segment != null) q = q.Where(m => segment.Contains(m.ClientId));
                    var rows = await q.Select(m => new { m.MeasuredOn, m.WeightKg }).ToListAsync();
                    average = metric == ReportMetric.AverageWeight;
                    points = rows.Select(r => (r.MeasuredOn, average ? r.WeightKg : 1m)).ToList();
                    break;
                }
                case ReportMetric.Appointments:
                case ReportMetric.Cancellations:
                {
                    var dayAfter = to.AddDays(1);
                    var q = _db.Appointments.AsNoTracking().Where(a => a.StartTime >= from && a.StartTime < dayAfter);
                    if (segment != null) q = q.Where(a => segment.Contains(a.ClientId));
                    if (metric == ReportMetric.Cancellations) q = q.Where(a => a.Status == AppointmentStatus.Cancelled);
                    var rows = await q.Select(a => a.StartTime).ToListAsync();
                    points = rows.Select(d => (d, 1m)).ToList();
                    break;
                }
                case ReportMetric.Logins:
                {
                    var q = _db.AuditLogs.AsNoTracking()
                        .Where(a => a.Action == "Login.Success" && a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc);
                    if (segment != null) q = q.Where(a => a.UserId != null && segment.Contains(a.UserId));
                    var rows = await q.Select(a => a.CreatedAtUtc).ToListAsync();
                    points = rows.Select(d => (d.ToLocalTime(), 1m)).ToList();
                    break;
                }
                default: // Registrations
                {
                    var q = _db.ClientProfiles.AsNoTracking().Where(c => c.CreatedAtUtc >= fromUtc && c.CreatedAtUtc < toUtc);
                    if (segment != null) q = q.Where(c => segment.Contains(c.UserId));
                    var rows = await q.Select(c => c.CreatedAtUtc).ToListAsync();
                    if (segment == null)
                        rows.AddRange(await _db.TrainerProfiles.AsNoTracking()
                            .Where(t => t.CreatedAtUtc >= fromUtc && t.CreatedAtUtc < toUtc)
                            .Select(t => t.CreatedAtUtc).ToListAsync());
                    points = rows.Select(d => (d.ToLocalTime(), 1m)).ToList();
                    break;
                }
            }

            var values = periods.Select(p =>
            {
                var inPeriod = points.Where(pt => PeriodStart(pt.Date, grouping) == p).Select(pt => pt.Value).ToList();
                if (average) return inPeriod.Count == 0 ? (decimal?)null : Math.Round(inPeriod.Average(), 1);
                return (decimal?)inPeriod.Sum();
            }).ToList();

            result.Series.Add((metric, values));
            result.Chart.Datasets.Add(new ChartDataset { Label = MetricName(metric), Data = values });
        }
        return result;
    }

    // =====================================================================
    // Shared helpers
    // =====================================================================

    // Human-readable name of a custom-report metric.
    public static string MetricName(ReportMetric metric) => metric switch
    {
        ReportMetric.WorkoutsLogged => "Workouts logged",
        ReportMetric.CaloriesBurned => "Calories burned",
        ReportMetric.MealsLogged => "Meals logged",
        ReportMetric.CaloriesConsumed => "Calories consumed",
        ReportMetric.Measurements => "Measurements recorded",
        ReportMetric.AverageWeight => "Average weight (kg)",
        ReportMetric.Appointments => "Appointments booked",
        ReportMetric.Cancellations => "Appointments cancelled",
        ReportMetric.Logins => "Successful logins",
        _ => "New registrations"
    };

    // First day of the day/week/month bucket containing a date.
    public static DateTime PeriodStart(DateTime date, ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Week => DateHelpers.StartOfWeek(date),
        ReportGrouping.Month => DateHelpers.StartOfMonth(date),
        _ => date.Date
    };

    // All bucket start dates between two dates.
    public static List<DateTime> Periods(DateTime from, DateTime to, ReportGrouping grouping)
    {
        var list = new List<DateTime>();
        var period = PeriodStart(from, grouping);
        while (period <= to.Date && list.Count < 400)
        {
            list.Add(period);
            period = grouping switch
            {
                ReportGrouping.Week => period.AddDays(7),
                ReportGrouping.Month => period.AddMonths(1),
                _ => period.AddDays(1)
            };
        }
        return list;
    }

    // Chart label for a bucket (e.g. "Wk 06 Oct" or "Oct 2026").
    public static string PeriodLabel(DateTime period, ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Month => period.ToString("MMM yyyy"),
        ReportGrouping.Week => "Wk " + period.ToString("dd MMM"),
        _ => period.ToString("dd MMM")
    };

    private static decimal? AverageOrNull(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }

    // Average total calories per day, counting only days on which meals were logged.
    private static decimal? AverageDailyCalories(IEnumerable<MealLog> meals)
    {
        var perDay = meals.GroupBy(m => m.LogDate.Date).Select(g => g.Sum(m => m.Calories)).ToList();
        return perDay.Count == 0 ? null : Math.Round((decimal)perDay.Average(), 0);
    }
}
