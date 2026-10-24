using System.Globalization;
using System.Security.Claims;
using HealthTrack.Models;

namespace HealthTrack.Infrastructure;

// Role names used throughout the application (single source of truth).
public static class RoleNames
{
    public const string Client = "Client";
    public const string Trainer = "Trainer";
    public const string Administrator = "Administrator";
    public const string TrainerOrAdmin = Trainer + "," + Administrator;

    public static readonly string[] All = { Client, Trainer, Administrator };
}

// Convenience helpers for the signed-in user.
public static class ClaimsPrincipalExtensions
{
    // Returns the Identity user id of the signed-in user.
    public static string GetUserId(this ClaimsPrincipal principal)
    {
        var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("The current user is not signed in.");
        return id;
    }
}

//Body-mass-index helpers
public static class BmiCalculator
{
    // BMI = weight (kg) / height (m)^2, rounded to one decimal place
    public static decimal Calculate(decimal weightKg, decimal heightCm)
    {
        if (weightKg <= 0 || heightCm <= 0)
            return 0;

        var heightM = heightCm / 100m;
        return Math.Round(weightKg / (heightM * heightM), 1);
    }

    //WHO adult BMI category.
    public static string Category(decimal bmi) => bmi switch
    {
        <= 0m => "Unknown",
        < 18.5m => "Underweight",
        < 25m => "Healthy",
        < 30m => "Overweight",
        _ => "Obese"
    };
}

// Estimates calories burned from MET values (metabolic equivalents):
// kcal = MET × body weight (kg) × duration (hours).
public static class CalorieEstimator
{
    public static int Estimate(IntensityLevel intensity, int minutes, decimal weightKg)
    {
        var met = intensity switch
        {
            IntensityLevel.Low => 3.5m,
            IntensityLevel.High => 9.0m,
            _ => 6.0m
        };
        return (int)Math.Round(met * weightKg * minutes / 60m);
    }
}

// Date helpers shared by analytics and reminders
public static class DateHelpers
{
    // Monday of the week containing the given date.
    public static DateTime StartOfWeek(DateTime date)
    {
        var diff = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
        return date.Date.AddDays(-diff);
    }

    public static DateTime StartOfMonth(DateTime date) => new(date.Year, date.Month, 1);

    // Short label such as "19 Oct"
    public static string ShortLabel(DateTime date) => date.ToString("dd MMM", CultureInfo.InvariantCulture);

    // Human friendly "time ago" text for activity feeds
    public static string TimeAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }
}

// Adds standard browser security headers to every response
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    // Adds the security headers and calls the next middleware.
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
            "font-src 'self' data: https://cdn.jsdelivr.net https://fonts.gstatic.com; " +
            "img-src 'self' data: blob:; " +
            "connect-src 'self' ws: wss: http://localhost:* https://localhost:*; " +
            "frame-ancestors 'none'; form-action 'self'; base-uri 'self'";
        return _next(context);
    }
}

// Serialises chart data for the data-chart attribute (camelCase, HTML-safe)
public static class ChartJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // Serialises chart data as camelCase JSON for Chart.js
    public static string Serialize(object data) => System.Text.Json.JsonSerializer.Serialize(data, Options);
}

// Display helpers used by several views.
public static class DisplayHelpers
{
    // Two-letter initials used for avatars.
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant()
            : (parts[0][0].ToString() + parts[^1][0]).ToUpperInvariant();
    }

    // CSS badge class for an appointment status.
    public static string StatusClass(AppointmentStatus status) => status switch
    {
        AppointmentStatus.Pending => "warning",
        AppointmentStatus.Approved => "primary",
        AppointmentStatus.Completed => "success",
        AppointmentStatus.Declined => "danger",
        _ => ""
    };

    // Bootstrap icon name for a notification type.
    public static string NotificationIcon(NotificationType type) => type switch
    {
        NotificationType.WorkoutReminder => "bi-activity",
        NotificationType.MealReminder => "bi-egg-fried",
        NotificationType.AppointmentReminder => "bi-alarm",
        NotificationType.AppointmentUpdate => "bi-calendar-event",
        NotificationType.PlanUpdate => "bi-clipboard2-pulse",
        NotificationType.Feedback => "bi-chat-heart",
        NotificationType.ClientAssignment => "bi-person-plus",
        NotificationType.ClientLog => "bi-journal-check",
        NotificationType.AdherenceAlert => "bi-graph-down-arrow",
        NotificationType.SecurityAlert => "bi-shield-exclamation",
        NotificationType.ApprovalPending => "bi-hourglass-split",
        NotificationType.Broadcast => "bi-megaphone",
        _ => "bi-bell"
    };

    // Formats a nullable percentage ("–" when there is no value)
    public static string Percent(decimal? value) => value.HasValue ? $"{value.Value:0}%" : "–";

    // Formats a nullable change with an explicit + or − sign.
    public static string Signed(decimal? value, string unit = "") =>
        value.HasValue ? value.Value.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + unit : "–";
}
