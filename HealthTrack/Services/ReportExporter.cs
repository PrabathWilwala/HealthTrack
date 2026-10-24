using System.Globalization;
using HealthTrack.Infrastructure;

namespace HealthTrack.Services.Reporting;

// Turns analytics results into downloadable PDF and CSV files.
public static class ReportExporter
{
    private static string N(decimal? value, string format = "0.#") =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string P(decimal? value) => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "-";

    // ------------------------------------------------------------------ client progress

    // PDF version of a client progress report.
    public static byte[] ClientProgressPdf(ClientProgressReport r)
    {
        var pdf = new SimplePdfDocument("Progress report: " + r.ClientName, $"{r.From:dd MMM yyyy} - {r.To:dd MMM yyyy}");

        pdf.Heading("Summary").KeyValues(new[]
        {
            ("Current weight", $"{N(r.CurrentWeight)} kg"),
            ("BMI", $"{N(r.CurrentBmi)} ({r.BmiCategory})"),
            ("Weight change in period", r.WeightChange.HasValue ? $"{r.WeightChange:+0.0;-0.0;0.0} kg" : "-"),
            ("Goal progress", r.Goal.Percent.HasValue ? $"{r.Goal.Percent:0}% - {r.Goal.Description}" : r.Goal.Description),
            ("Workouts", $"{r.TotalWorkouts} sessions, {r.TotalWorkoutMinutes} min, {r.TotalCaloriesBurned} kcal burned"),
            ("Average daily intake", $"{N(r.AverageDailyIntake, "0")} kcal (target {r.CalorieTarget} kcal)"),
            ("Workout adherence", P(r.Adherence.WorkoutPercent)),
            ("Diet adherence", P(r.Adherence.DietPercent))
        });

        pdf.Heading("This month vs last month").Table(
            new[] { "Metric", "Last month", "This month", "Change" },
            r.MonthComparison.Select(c => (IReadOnlyList<string>)new[]
            {
                c.Metric,
                N(c.Previous) + " " + c.Unit,
                N(c.Current) + " " + c.Unit,
                c.Change.HasValue ? c.Change.Value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) : "-"
            }));

        pdf.Heading("Measurements").Table(
            new[] { "Date", "Weight (kg)", "BMI", "Body fat %", "Waist (cm)", "Blood pressure", "Heart rate" },
            r.Measurements.OrderBy(m => m.MeasuredOn).Select(m => (IReadOnlyList<string>)new[]
            {
                m.MeasuredOn.ToString("dd MMM yyyy"), N(m.WeightKg), N(m.Bmi), N(m.BodyFatPercent), N(m.WaistCm),
                m.SystolicBp.HasValue ? $"{m.SystolicBp}/{m.DiastolicBp}" : "-",
                m.RestingHeartRate?.ToString() ?? "-"
            }));

        pdf.Heading("Workouts").Table(
            new[] { "Date", "Activity", "Minutes", "Intensity", "Calories" },
            r.Workouts.OrderBy(w => w.LogDate).Select(w => (IReadOnlyList<string>)new[]
            {
                w.LogDate.ToString("dd MMM yyyy"), w.ExerciseType, w.DurationMinutes.ToString(), w.Intensity.ToString(), w.CaloriesBurned.ToString()
            }));

        var dailyMeals = r.Meals.GroupBy(m => m.LogDate.Date).OrderBy(g => g.Key);
        pdf.Heading("Nutrition (daily totals)").Table(
            new[] { "Date", "Meals", "Calories", "Protein (g)", "Carbs (g)", "Fat (g)" },
            dailyMeals.Select(g => (IReadOnlyList<string>)new[]
            {
                g.Key.ToString("dd MMM yyyy"), g.Count().ToString(), g.Sum(m => m.Calories).ToString(),
                N(g.Sum(m => m.ProteinG), "0"), N(g.Sum(m => m.CarbsG), "0"), N(g.Sum(m => m.FatG), "0")
            }));

        pdf.Paragraph("This report contains personal health information. Share it only with people you trust, such as your healthcare provider.");
        return pdf.Build();
    }

    // CSV export of a client's measurements, workouts and meals.
    public static byte[] ClientProgressCsv(ClientProgressReport r)
    {
        // One flat table with a "Type" column so it opens nicely in Excel.
        var rows = new List<string?[]>();
        rows.AddRange(r.Measurements.OrderBy(m => m.MeasuredOn).Select(m => new string?[]
        {
            "Measurement", m.MeasuredOn.ToString("yyyy-MM-dd"), "Weight/BMI", N(m.WeightKg), N(m.Bmi),
            m.SystolicBp.HasValue ? $"{m.SystolicBp}/{m.DiastolicBp}" : "", m.RestingHeartRate?.ToString(), ""
        }));
        rows.AddRange(r.Workouts.OrderBy(w => w.LogDate).Select(w => new string?[]
        {
            "Workout", w.LogDate.ToString("yyyy-MM-dd"), w.ExerciseType, w.DurationMinutes.ToString(), w.CaloriesBurned.ToString(),
            w.Intensity.ToString(), "", w.Notes
        }));
        rows.AddRange(r.Meals.OrderBy(m => m.LogDate).Select(m => new string?[]
        {
            "Meal", m.LogDate.ToString("yyyy-MM-dd"), $"{m.MealType}: {m.FoodItems}", m.Calories.ToString(),
            N(m.ProteinG), N(m.CarbsG), N(m.FatG), m.Notes
        }));

        return CsvBuilder.Build(
            new[] { "Type", "Date", "Description", "Value 1 (weight kg / minutes / kcal)", "Value 2 (BMI / kcal / protein g)", "Value 3 (BP / intensity / carbs g)", "Value 4 (HR / fat g)", "Notes" },
            rows);
    }

    // ------------------------------------------------------------------ trainer / admin tables

    // PDF table of client summaries (comparative analysis).
    public static byte[] ClientSummariesPdf(string title, IEnumerable<ClientSummaryRow> rows)
    {
        var pdf = new SimplePdfDocument(title);
        pdf.Table(new[] { "Client", "Weight", "30d change", "BMI", "Adherence", "Workouts 30d", "Goal", "Last active" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name, N(r.CurrentWeight), r.WeightChange30Days.HasValue ? r.WeightChange30Days.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "-",
                N(r.Bmi), P(r.AdherencePercent), r.Workouts30Days.ToString(), P(r.GoalPercent),
                r.LastActiveUtc.HasValue ? DateHelpers.TimeAgo(r.LastActiveUtc.Value) : "never"
            }));
        return pdf.Build();
    }

    // CSV table of client summaries.
    public static byte[] ClientSummariesCsv(IEnumerable<ClientSummaryRow> rows) =>
        CsvBuilder.Build(
            new[] { "Client", "Current weight (kg)", "Weight change 30 days (kg)", "BMI", "Overall adherence %", "Workout adherence %", "Diet adherence %", "Workouts 30 days", "Log entries 30 days", "Goal progress %", "Last active (UTC)", "Alerts" },
            rows.Select(r => (IReadOnlyList<string?>)new[]
            {
                r.Name, N(r.CurrentWeight), N(r.WeightChange30Days), N(r.Bmi), N(r.AdherencePercent), N(r.WorkoutAdherence), N(r.DietAdherence),
                r.Workouts30Days.ToString(), r.Logs30Days.ToString(), N(r.GoalPercent), r.LastActiveUtc?.ToString("u"), string.Join("; ", r.Alerts)
            }));

    // PDF of plan popularity and effectiveness.
    public static byte[] RoutinesPdf(string title, IEnumerable<RoutineStatsRow> rows)
    {
        var pdf = new SimplePdfDocument(title);
        pdf.Table(new[] { "Plan", "Type", "Created by", "Assigned", "Active", "Avg adherence", "Avg weight change" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Title, r.PlanType, r.CreatedBy, r.TimesAssigned.ToString(), r.ActiveClients.ToString(), P(r.AverageAdherence),
                r.AverageWeightChange.HasValue ? r.AverageWeightChange.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " kg" : "-"
            }));
        return pdf.Build();
    }

    // CSV of plan popularity and effectiveness.
    public static byte[] RoutinesCsv(IEnumerable<RoutineStatsRow> rows) =>
        CsvBuilder.Build(new[] { "Plan", "Type", "Created by", "Times assigned", "Active clients", "Average adherence %", "Average weight change (kg)" },
            rows.Select(r => (IReadOnlyList<string?>)new[]
            {
                r.Title, r.PlanType, r.CreatedBy, r.TimesAssigned.ToString(), r.ActiveClients.ToString(), N(r.AverageAdherence), N(r.AverageWeightChange)
            }));

    // PDF engagement statistics.
    public static byte[] EngagementPdf(string title, EngagementReport report)
    {
        var pdf = new SimplePdfDocument(title);
        pdf.KeyValues(new[]
        {
            ("Users", report.TotalUsers.ToString()),
            ("Active in last 7 days", report.ActiveLast7Days.ToString()),
            ("Active in last 30 days", report.ActiveLast30Days.ToString()),
            ("30-day retention", report.RetentionPercent.ToString("0") + "%")
        });
        pdf.Table(new[] { "User", "Logins", "Log entries", "Adherence", "Last active" },
            report.Rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name, r.Logins.ToString(), r.LogEntries.ToString(), P(r.AdherencePercent),
                r.LastActiveUtc.HasValue ? DateHelpers.TimeAgo(r.LastActiveUtc.Value) : "never"
            }));
        return pdf.Build();
    }

    // CSV engagement statistics.
    public static byte[] EngagementCsv(EngagementReport report) =>
        CsvBuilder.Build(new[] { "User", "Logins", "Log entries", "Adherence %", "Last active (UTC)" },
            report.Rows.Select(r => (IReadOnlyList<string?>)new[]
            {
                r.Name, r.Logins.ToString(), r.LogEntries.ToString(), N(r.AdherencePercent), r.LastActiveUtc?.ToString("u")
            }));

    // PDF system performance report for administrators.
    public static byte[] SystemPdf(SystemReport r)
    {
        var pdf = new SimplePdfDocument("HealthTrack system report", $"{r.From:dd MMM yyyy} - {r.To:dd MMM yyyy}");
        pdf.Heading("Outcomes").KeyValues(new[]
        {
            ("Clients", r.ClientCount.ToString()),
            ("Average weight change", r.AverageWeightChange.HasValue ? $"{r.AverageWeightChange:+0.0;-0.0;0.0} kg" : "-"),
            ("Average plan adherence", P(r.AverageAdherence)),
            ("Goals achieved", $"{r.GoalsAchieved} of {r.ClientsWithGoals} clients with a target weight"),
            ("Active users (7 / 30 days)", $"{r.Engagement.ActiveLast7Days} / {r.Engagement.ActiveLast30Days}"),
            ("30-day retention", r.Engagement.RetentionPercent.ToString("0") + "%")
        });
        pdf.Heading("Trainer performance").Table(
            new[] { "Trainer", "Clients", "Avg adherence", "Avg weight change", "Completed appts", "Cancellation rate", "Feedback", "Plans" },
            r.Trainers.Select(t => (IReadOnlyList<string>)new[]
            {
                t.Name, t.Clients.ToString(), P(t.AverageAdherence),
                t.AverageWeightChange.HasValue ? t.AverageWeightChange.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " kg" : "-",
                t.CompletedAppointments.ToString(), P(t.CancellationRate), t.FeedbackGiven.ToString(), t.PlansCreated.ToString()
            }));
        pdf.Heading("Most popular routines").Table(
            new[] { "Plan", "Type", "Assigned", "Active", "Avg adherence" },
            r.PopularRoutines.Select(p => (IReadOnlyList<string>)new[]
            {
                p.Title, p.PlanType, p.TimesAssigned.ToString(), p.ActiveClients.ToString(), P(p.AverageAdherence)
            }));
        pdf.Heading("Feature usage").Table(
            new[] { "Feature area", "Actions" },
            r.FeatureUsageChart.Labels.Select((label, i) => (IReadOnlyList<string>)new[]
            {
                label, N(r.FeatureUsageChart.Datasets.FirstOrDefault()?.Data.ElementAtOrDefault(i), "0")
            }));
        return pdf.Build();
    }

    // CSV trainer performance table from the system report.
    public static byte[] SystemCsv(SystemReport r) =>
        CsvBuilder.Build(
            new[] { "Trainer", "Clients", "Average adherence %", "Average weight change (kg)", "Completed appointments", "Cancellation rate %", "Feedback given", "Plans created" },
            r.Trainers.Select(t => (IReadOnlyList<string?>)new[]
            {
                t.Name, t.Clients.ToString(), N(t.AverageAdherence), N(t.AverageWeightChange), t.CompletedAppointments.ToString(),
                N(t.CancellationRate), t.FeedbackGiven.ToString(), t.PlansCreated.ToString()
            }));

    // CSV output of the custom report builder.
    public static byte[] CustomCsv(CustomReportResult result) =>
        CsvBuilder.Build(
            new[] { "Period" }.Concat(result.Series.Select(s => AnalyticsService.MetricName(s.Metric))).ToArray(),
            result.Periods.Select((period, i) => (IReadOnlyList<string?>)new[] { period }
                .Concat(result.Series.Select(s => N(s.Values[i]))).ToArray()));

    // PDF output of the custom report builder.
    public static byte[] CustomPdf(CustomReportResult result, CustomReportRequest request)
    {
        var pdf = new SimplePdfDocument("Custom report", $"{request.From:dd MMM yyyy} - {request.To:dd MMM yyyy} | {result.SegmentDescription} | by {request.Grouping}");
        pdf.Table(
            new[] { "Period" }.Concat(result.Series.Select(s => AnalyticsService.MetricName(s.Metric))).ToArray(),
            result.Periods.Select((period, i) => (IReadOnlyList<string>)new[] { period }
                .Concat(result.Series.Select(s => N(s.Values[i]))).ToArray()));
        return pdf.Build();
    }
}
