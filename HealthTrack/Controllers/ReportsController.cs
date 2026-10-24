using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Services;
using HealthTrack.Services.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

// Reports for trainers (scoped to their own clients) and administrators (system-wide),
// each viewable on screen and exportable as PDF or CSV.
[Authorize(Roles = RoleNames.TrainerOrAdmin)]
public class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAnalyticsService _analytics;
    private readonly IUserDirectory _directory;
    private readonly IAuditService _audit;

    public ReportsController(ApplicationDbContext db, IAnalyticsService analytics, IUserDirectory directory, IAuditService audit)
    {
        _db = db;
        _analytics = analytics;
        _directory = directory;
        _audit = audit;
    }

    private bool IsAdmin => User.IsInRole(RoleNames.Administrator);

    // Client ids the current user may report on.
    private async Task<List<string>> ScopeClientIdsAsync() =>
        IsAdmin
            ? await _db.ClientProfiles.Select(c => c.UserId).ToListAsync()
            : await _directory.GetClientIdsForTrainerAsync(User.GetUserId());

    // Reports hub.
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ids = await ScopeClientIdsAsync();
        var names = await _directory.GetDisplayNamesAsync(ids);
        ViewData["Clients"] = names.OrderBy(n => n.Value).ToList();
        return View();
    }

    // ------------------------------------------------------------------ individual client report

    // Individual client progress report (HTML, PDF or CSV).
    [HttpGet]
    public async Task<IActionResult> Client(string id, int days = 30, string? format = null)
    {
        var ids = await ScopeClientIdsAsync();
        if (!ids.Contains(id)) return NotFound();

        days = Math.Clamp(days, 7, 365);
        var grouping = days <= 31 ? ReportGrouping.Day : days <= 180 ? ReportGrouping.Week : ReportGrouping.Month;
        var report = await _analytics.GetClientProgressAsync(id, DateTime.Today.AddDays(-(days - 1)), DateTime.Today, grouping);

        if (format != null)
        {
            await _audit.LogAsync("Reports", "ClientReport.Exported", $"{report.ClientName} ({format})");
            var name = $"client-report-{Slug(report.ClientName)}-{DateTime.Today:yyyyMMdd}";
            return format == "csv"
                ? File(ReportExporter.ClientProgressCsv(report), "text/csv", name + ".csv")
                : File(ReportExporter.ClientProgressPdf(report), "application/pdf", name + ".pdf");
        }

        ViewData["Days"] = days;
        ViewData["ClientId"] = id;
        return View(report);
    }

    // ------------------------------------------------------------------ comparative analysis

    // Comparative analysis of selected clients (HTML, PDF or CSV).
    [HttpGet]
    public async Task<IActionResult> Comparative(List<string>? clientIds, string? format = null)
    {
        var scope = await ScopeClientIdsAsync();
        var selected = clientIds != null && clientIds.Count > 0 ? clientIds.Where(scope.Contains).ToList() : scope;
        var rows = await _analytics.GetClientSummariesAsync(selected);

        if (format != null)
        {
            await _audit.LogAsync("Reports", "Comparative.Exported", format);
            return format == "csv"
                ? File(ReportExporter.ClientSummariesCsv(rows), "text/csv", $"comparative-report-{DateTime.Today:yyyyMMdd}.csv")
                : File(ReportExporter.ClientSummariesPdf("Comparative client analysis", rows), "application/pdf", $"comparative-report-{DateTime.Today:yyyyMMdd}.pdf");
        }

        ViewData["AllClients"] = (await _directory.GetDisplayNamesAsync(scope)).OrderBy(n => n.Value).ToList();
        ViewData["Selected"] = selected;
        return View(rows);
    }

    // ------------------------------------------------------------------ engagement

    // Engagement statistics (HTML, PDF or CSV).
    [HttpGet]
    public async Task<IActionResult> Engagement(int days = 30, string? format = null)
    {
        days = Math.Clamp(days, 7, 365);
        IReadOnlyCollection<string>? ids = IsAdmin ? null : await ScopeClientIdsAsync();
        var report = await _analytics.GetEngagementAsync(ids, DateTime.Today.AddDays(-(days - 1)), DateTime.Today);

        if (format != null)
        {
            await _audit.LogAsync("Reports", "Engagement.Exported", format);
            return format == "csv"
                ? File(ReportExporter.EngagementCsv(report), "text/csv", $"engagement-{DateTime.Today:yyyyMMdd}.csv")
                : File(ReportExporter.EngagementPdf($"Engagement statistics (last {days} days)", report), "application/pdf", $"engagement-{DateTime.Today:yyyyMMdd}.pdf");
        }

        ViewData["Days"] = days;
        return View(report);
    }

    // ------------------------------------------------------------------ popular routines

    // Popular and effective routines (HTML, PDF or CSV).
    [HttpGet]
    public async Task<IActionResult> Routines(string? format = null)
    {
        var rows = await _analytics.GetRoutineStatsAsync(IsAdmin ? null : User.GetUserId());

        if (format != null)
        {
            await _audit.LogAsync("Reports", "Routines.Exported", format);
            return format == "csv"
                ? File(ReportExporter.RoutinesCsv(rows), "text/csv", $"routines-{DateTime.Today:yyyyMMdd}.csv")
                : File(ReportExporter.RoutinesPdf("Popular and effective routines", rows), "application/pdf", $"routines-{DateTime.Today:yyyyMMdd}.pdf");
        }
        return View(rows);
    }

    // ------------------------------------------------------------------ trends & feedback effectiveness

    // Trend alerts needing intervention.
    [HttpGet]
    public async Task<IActionResult> Trends()
    {
        var alerts = await _analytics.GetTrendAlertsAsync(await ScopeClientIdsAsync());
        return View(alerts);
    }

    // Client activity before and after feedback.
    [HttpGet]
    public async Task<IActionResult> FeedbackEffectiveness()
    {
        var rows = await _analytics.GetFeedbackEffectivenessAsync(IsAdmin ? null : User.GetUserId());
        return View(rows);
    }

    // ------------------------------------------------------------------ administrator-only reports

    // Administrator system performance report (HTML, PDF or CSV).
    [Authorize(Roles = RoleNames.Administrator)]
    [HttpGet]
    public async Task<IActionResult> Overview(int days = 30, string? format = null)
    {
        days = Math.Clamp(days, 7, 365);
        var report = await _analytics.GetSystemReportAsync(DateTime.Today.AddDays(-(days - 1)), DateTime.Today);

        if (format != null)
        {
            await _audit.LogAsync("Reports", "SystemReport.Exported", format);
            return format == "csv"
                ? File(ReportExporter.SystemCsv(report), "text/csv", $"system-report-{DateTime.Today:yyyyMMdd}.csv")
                : File(ReportExporter.SystemPdf(report), "application/pdf", $"system-report-{DateTime.Today:yyyyMMdd}.pdf");
        }

        ViewData["Days"] = days;
        return View(report);
    }

    // Administrator custom report builder (HTML, PDF or CSV).
    [Authorize(Roles = RoleNames.Administrator)]
    [HttpGet]
    public async Task<IActionResult> Custom(CustomReportRequest? request, bool run = false, string? format = null)
    {
        request ??= new CustomReportRequest();
        if (request.Metrics.Count == 0) request.Metrics.Add(ReportMetric.WorkoutsLogged);
        if ((request.To - request.From).TotalDays > 730) request.From = request.To.AddDays(-730);

        ViewData["Trainers"] = await _directory.GetApprovedTrainersAsync();
        ViewData["Request"] = request;

        if (!run && format == null)
            return View((CustomReportResult?)null);

        var result = await _analytics.RunCustomReportAsync(request);
        if (format != null)
        {
            await _audit.LogAsync("Reports", "CustomReport.Exported", format);
            return format == "csv"
                ? File(ReportExporter.CustomCsv(result), "text/csv", $"custom-report-{DateTime.Today:yyyyMMdd}.csv")
                : File(ReportExporter.CustomPdf(result, request), "application/pdf", $"custom-report-{DateTime.Today:yyyyMMdd}.pdf");
        }

        await _audit.LogAsync("Reports", "CustomReport.Run", string.Join(",", request.Metrics));
        return View(result);
    }

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
