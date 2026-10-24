using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Services;

/// <summary>How time-series data is bucketed in charts and reports.</summary>
public enum ReportGrouping
{
    Day = 0,
    Week = 1,
    Month = 2
}

/// <summary>Chart.js compatible structure (serialised with camelCase names).</summary>
public sealed class ChartData
{
    public List<string> Labels { get; set; } = new();
    public List<ChartDataset> Datasets { get; set; } = new();
}

public sealed class ChartDataset
{
    public string Label { get; set; } = string.Empty;
    public List<decimal?> Data { get; set; } = new();
    /// <summary>Optional per-dataset chart type for mixed charts ("line" or "bar").</summary>
    public string? Type { get; set; }
}

/// <summary>Plan adherence for one client over a period. Null percentages mean "no plan assigned".</summary>
public sealed class AdherenceResult
{
    public decimal? WorkoutPercent { get; set; }
    public decimal? DietPercent { get; set; }
    public int WorkoutsCompleted { get; set; }
    public decimal WorkoutsExpected { get; set; }
    public int DietDaysOnTarget { get; set; }
    public int DietDaysPlanned { get; set; }

    public decimal? OverallPercent
    {
        get
        {
            var parts = new[] { WorkoutPercent, DietPercent }.Where(p => p.HasValue).Select(p => p!.Value).ToList();
            return parts.Count == 0 ? null : Math.Round(parts.Average(), 0);
        }
    }
}

/// <summary>Start → current → target weight progress.</summary>
public sealed class GoalProgress
{
    public decimal StartWeight { get; set; }
    public decimal CurrentWeight { get; set; }
    public decimal? TargetWeight { get; set; }
    public decimal? Percent { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>One metric compared between this month and last month.</summary>
public sealed record PeriodComparison(string Metric, decimal? Previous, decimal? Current, string Unit, bool HigherIsBetter)
{
    public decimal? Change => Previous.HasValue && Current.HasValue ? Current - Previous : (decimal?)null;

    public bool? IsImprovement => Change == null || Change == 0 ? (bool?)null : (Change > 0) == HigherIsBetter;
}

/// <summary>Everything the client progress page and personal reports need.</summary>
public sealed class ClientProgressReport
{
    public string ClientName { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public ReportGrouping Grouping { get; set; }

    public decimal HeightCm { get; set; }
    public decimal CurrentWeight { get; set; }
    public decimal CurrentBmi { get; set; }
    public string BmiCategory { get; set; } = string.Empty;
    public int CalorieTarget { get; set; }
    public int ProteinTarget { get; set; }
    public int CarbsTarget { get; set; }
    public int FatTarget { get; set; }

    public ChartData WeightChart { get; set; } = new();
    public ChartData BmiChart { get; set; } = new();
    public ChartData CaloriesBurnedChart { get; set; } = new();
    public ChartData NutritionChart { get; set; } = new();
    public ChartData MacroChart { get; set; } = new();
    public ChartData WorkoutFrequencyChart { get; set; } = new();

    public int TotalWorkouts { get; set; }
    public int TotalWorkoutMinutes { get; set; }
    public int TotalCaloriesBurned { get; set; }
    public decimal AverageDailyIntake { get; set; }
    public decimal? WeightChange { get; set; }

    public GoalProgress Goal { get; set; } = new();
    public AdherenceResult Adherence { get; set; } = new();
    public List<PeriodComparison> MonthComparison { get; set; } = new();

    public List<Models.HealthMeasurement> Measurements { get; set; } = new();
    public List<Models.WorkoutLog> Workouts { get; set; } = new();
    public List<Models.MealLog> Meals { get; set; } = new();
}

/// <summary>Row in the trainer's client list / comparative report.</summary>
public sealed class ClientSummaryRow
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? LastActiveUtc { get; set; }
    public decimal? CurrentWeight { get; set; }
    public decimal? WeightChange30Days { get; set; }
    public decimal? Bmi { get; set; }
    public decimal? AdherencePercent { get; set; }
    public decimal? WorkoutAdherence { get; set; }
    public decimal? DietAdherence { get; set; }
    public int Workouts30Days { get; set; }
    public int Logs30Days { get; set; }
    public decimal? GoalPercent { get; set; }
    public DateTime? NextAppointment { get; set; }
    public List<string> Alerts { get; set; } = new();
}

/// <summary>Popularity and effectiveness of one workout or diet plan.</summary>
public sealed class RoutineStatsRow
{
    public int PlanId { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public int TimesAssigned { get; set; }
    public int ActiveClients { get; set; }
    public decimal? AverageAdherence { get; set; }
    public decimal? AverageWeightChange { get; set; }
}

/// <summary>A pattern that may need the trainer's attention.</summary>
public sealed record TrendAlert(string ClientId, string ClientName, string Severity, string Message);

public sealed class FeedbackEffectivenessRow
{
    public string ClientName { get; set; } = string.Empty;
    public DateTime GivenAtUtc { get; set; }
    public string Message { get; set; } = string.Empty;
    public int LogsWeekBefore { get; set; }
    public int LogsWeekAfter { get; set; }
    public int Change => LogsWeekAfter - LogsWeekBefore;
}

public sealed class EngagementReport
{
    public int TotalUsers { get; set; }
    public int ActiveLast7Days { get; set; }
    public int ActiveLast30Days { get; set; }
    public decimal RetentionPercent { get; set; }
    public ChartData DailyActivityChart { get; set; } = new();
    public List<EngagementRow> Rows { get; set; } = new();
}

public sealed class EngagementRow
{
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Logins { get; set; }
    public int LogEntries { get; set; }
    public decimal? AdherencePercent { get; set; }
    public DateTime? LastActiveUtc { get; set; }
}

public sealed class TrainerPerformanceRow
{
    public string TrainerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Clients { get; set; }
    public decimal? AverageAdherence { get; set; }
    public decimal? AverageWeightChange { get; set; }
    public int CompletedAppointments { get; set; }
    public decimal? CancellationRate { get; set; }
    public int FeedbackGiven { get; set; }
    public int PlansCreated { get; set; }
}

public sealed class SystemReport
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int ClientCount { get; set; }
    public decimal? AverageWeightChange { get; set; }
    public decimal? AverageAdherence { get; set; }
    public int GoalsAchieved { get; set; }
    public int ClientsWithGoals { get; set; }
    public List<TrainerPerformanceRow> Trainers { get; set; } = new();
    public List<RoutineStatsRow> PopularRoutines { get; set; } = new();
    public ChartData FeatureUsageChart { get; set; } = new();
    public ChartData PeakHoursChart { get; set; } = new();
    public ChartData RegistrationsChart { get; set; } = new();
    public EngagementReport Engagement { get; set; } = new();
}

/// <summary>Metrics available in the administrator's custom report builder.</summary>
public enum ReportMetric
{
    [Display(Name = "Workouts logged")] WorkoutsLogged = 0,
    [Display(Name = "Calories burned")] CaloriesBurned = 1,
    [Display(Name = "Meals logged")] MealsLogged = 2,
    [Display(Name = "Calories consumed")] CaloriesConsumed = 3,
    [Display(Name = "Measurements recorded")] Measurements = 4,
    [Display(Name = "Average weight (kg)")] AverageWeight = 5,
    [Display(Name = "Appointments booked")] Appointments = 6,
    [Display(Name = "Appointments cancelled")] Cancellations = 7,
    [Display(Name = "Successful logins")] Logins = 8,
    [Display(Name = "New registrations")] Registrations = 9
}

public sealed class CustomReportRequest
{
    public List<ReportMetric> Metrics { get; set; } = new() { ReportMetric.WorkoutsLogged };

    [DataType(DataType.Date)]
    public DateTime From { get; set; } = DateTime.Today.AddDays(-29);

    [DataType(DataType.Date)]
    public DateTime To { get; set; } = DateTime.Today;

    public ReportGrouping Grouping { get; set; } = ReportGrouping.Week;

    /// <summary>Restrict to the clients of one trainer (optional).</summary>
    public string? TrainerId { get; set; }

    /// <summary>Restrict to one gender (optional segment).</summary>
    public string? Gender { get; set; }
}

public sealed class CustomReportResult
{
    public List<string> Periods { get; set; } = new();
    public List<(ReportMetric Metric, List<decimal?> Values)> Series { get; set; } = new();
    public ChartData Chart { get; set; } = new();
    public string SegmentDescription { get; set; } = string.Empty;
}
