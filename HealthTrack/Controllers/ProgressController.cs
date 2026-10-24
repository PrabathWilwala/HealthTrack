using HealthTrack.Infrastructure;
using HealthTrack.Services;
using HealthTrack.Services.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Controllers;

// Client progress tracking: trends, goal progress, comparisons and exports.
[Authorize(Roles = RoleNames.Client)]
public class ProgressController : Controller
{
    private readonly IAnalyticsService _analytics;
    private readonly IAuditService _audit;

    public ProgressController(IAnalyticsService analytics, IAuditService audit)
    {
        _analytics = analytics;
        _audit = audit;
    }

    // days: Length of the period ending today (7, 30, 90, 180 or 365).
    // grouping: Day, week or month buckets for the charts.
    [HttpGet]
    public async Task<IActionResult> Index(int days = 30, ReportGrouping? grouping = null)
    {
        days = days is 7 or 30 or 90 or 180 or 365 ? days : 30;
        var effectiveGrouping = grouping ?? (days <= 30 ? ReportGrouping.Day : days <= 180 ? ReportGrouping.Week : ReportGrouping.Month);

        var report = await _analytics.GetClientProgressAsync(User.GetUserId(), DateTime.Today.AddDays(-(days - 1)), DateTime.Today, effectiveGrouping);
        ViewData["Days"] = days;
        return View(report);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string format = "pdf", int days = 30)
    {
        days = Math.Clamp(days, 7, 365);
        var report = await _analytics.GetClientProgressAsync(User.GetUserId(), DateTime.Today.AddDays(-(days - 1)), DateTime.Today, ReportGrouping.Day);
        await _audit.LogAsync("Reports", "Progress.Exported", format.ToUpperInvariant());

        var fileName = $"healthtrack-progress-{DateTime.Today:yyyyMMdd}";
        return format == "csv"
            ? File(ReportExporter.ClientProgressCsv(report), "text/csv", fileName + ".csv")
            : File(ReportExporter.ClientProgressPdf(report), "application/pdf", fileName + ".pdf");
    }
}
