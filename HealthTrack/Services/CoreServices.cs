using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using HealthTrack.Data;
using HealthTrack.Models;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

// Outcome of a business operation: success, or a user-friendly error message.
public sealed record ServiceResult(bool Succeeded, string? Error)
{
    // Creates a successful result.
    public static ServiceResult Ok() => new(true, null);
    // Creates a failed result carrying a user-friendly error message.
    public static ServiceResult Fail(string error) => new(false, error);
}

// ============================================================================
// Audit logging
// ============================================================================

// Writes security-relevant and activity events to the AuditLogs table.
public interface IAuditService
{
    Task LogAsync(string category, string action, string? details = null,
        bool isSecurityEvent = false, string? userId = null, string? userName = null);
}

public sealed class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(ApplicationDbContext db, IHttpContextAccessor httpContextAccessor, ILogger<AuditService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    // Writes an audit entry (user, IP address, category, action, details). Never throws.
    public async Task LogAsync(string category, string action, string? details = null,
        bool isSecurityEvent = false, string? userId = null, string? userName = null)
    {
        var http = _httpContextAccessor.HttpContext;
        userId ??= http?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        userName ??= http?.User.Identity?.Name;

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = Truncate(userName, 256),
            Category = Truncate(category, 50) ?? "General",
            Action = Truncate(action, 100) ?? "Unknown",
            Details = Truncate(details, 1000),
            IpAddress = Truncate(http?.Connection.RemoteIpAddress?.ToString(), 64),
            IsSecurityEvent = isSecurityEvent,
            CreatedAtUtc = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Auditing must never break the user's request.
            _logger.LogError(ex, "Failed to write audit entry {Category}/{Action}", category, action);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value == null ? null : (value.Length <= max ? value : value.Substring(0, max));
}

// ============================================================================
// Email
// ============================================================================

// SMTP configuration read from the "Email" section of appsettings.json.
public sealed class EmailSettings
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@healthtrack.local";
    public string FromName { get; set; } = "HealthTrack";
}

public interface IAppEmailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody);
}

// Sends email through SMTP when configured. Otherwise every message is saved as an .html file
// under App_Data/Emails so the flows (verification, password reset) can be tested locally.
public sealed class EmailSender : IAppEmailSender
{
    private readonly EmailSettings _settings;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IConfiguration configuration, IWebHostEnvironment environment, ILogger<EmailSender> logger)
    {
        _settings = configuration.GetSection("Email").Get<EmailSettings>() ?? new EmailSettings();
        _environment = environment;
        _logger = logger;
    }

    // Sends an HTML email via SMTP, or saves it to App_Data/Emails when SMTP is not configured.
    public async Task SendAsync(string toAddress, string subject, string htmlBody)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.SmtpHost))
            {
                await SaveToPickupFolderAsync(toAddress, subject, htmlBody);
                return;
            }

            using var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress, _settings.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(toAddress);

            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.UseSsl
            };
            if (!string.IsNullOrWhiteSpace(_settings.UserName))
                client.Credentials = new NetworkCredential(_settings.UserName, _settings.Password);

            await client.SendMailAsync(message);
        }
        catch (Exception ex)
        {
            // Email failure should not crash the request; it is logged for the administrator.
            _logger.LogError(ex, "Failed to send email '{Subject}' to {To}", subject, toAddress);
        }
    }

    private async Task SaveToPickupFolderAsync(string toAddress, string subject, string htmlBody)
    {
        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "Emails");
        Directory.CreateDirectory(folder);

        var safeTo = new string(toAddress.Where(char.IsLetterOrDigit).ToArray());
        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeTo}.html";
        var content = $"<p><b>To:</b> {WebUtility.HtmlEncode(toAddress)}<br/><b>Subject:</b> {WebUtility.HtmlEncode(subject)}</p><hr/>{htmlBody}";

        await File.WriteAllTextAsync(Path.Combine(folder, fileName), content);
        _logger.LogInformation("SMTP not configured. Email to {To} saved to {File}", toAddress, fileName);
    }
}

// ============================================================================
// Administrator-managed settings (appointment policy, alert thresholds)
// ============================================================================

public static class SettingKeys
{
    public const string MinBookingNoticeHours = "Appointments.MinBookingNoticeHours";
    public const string MinCancellationNoticeHours = "Appointments.MinCancellationNoticeHours";
    public const string MaxCancellationsPerMonth = "Appointments.MaxCancellationsPerMonth";
    public const string AdherenceAlertThreshold = "Notifications.AdherenceAlertThreshold";

    // Defaults used when a setting has never been saved.
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [MinBookingNoticeHours] = "2",
        [MinCancellationNoticeHours] = "4",
        [MaxCancellationsPerMonth] = "3",
        [AdherenceAlertThreshold] = "50"
    };
}

public interface ISettingsService
{
    Task<int> GetIntAsync(string key);
    Task SetAsync(string key, string value);
}

public sealed class SettingsService : ISettingsService
{
    private readonly ApplicationDbContext _db;

    public SettingsService(ApplicationDbContext db)
    {
        _db = db;
    }

    // Reads an integer policy setting, falling back to its default value.
    public async Task<int> GetIntAsync(string key)
    {
        var setting = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        var raw = setting?.Value ?? (SettingKeys.Defaults.TryGetValue(key, out var d) ? d : "0");
        return int.TryParse(raw, out var value) ? value : 0;
    }

    // Creates or updates a policy setting.
    public async Task SetAsync(string key, string value)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
            _db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        else
            setting.Value = value;
        await _db.SaveChangesAsync();
    }
}

// ============================================================================
// User directory (display names, trainer lookups)
// ============================================================================

public sealed record TrainerOption(string UserId, string FullName, string Specializations, int ClientCount);

public interface IUserDirectory
{
    Task<Dictionary<string, string>> GetDisplayNamesAsync(IEnumerable<string> userIds);
    Task<string> GetDisplayNameAsync(string userId);
    Task<List<TrainerOption>> GetApprovedTrainersAsync();
    Task<List<string>> GetClientIdsForTrainerAsync(string trainerId);
}

public sealed class UserDirectory : IUserDirectory
{
    private readonly ApplicationDbContext _db;

    public UserDirectory(ApplicationDbContext db)
    {
        _db = db;
    }

    // Maps user ids to the best available display name (profile full name, else username).
    public async Task<Dictionary<string, string>> GetDisplayNamesAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var result = new Dictionary<string, string>();
        if (ids.Count == 0)
            return result;

        var clients = await _db.ClientProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.UserId)).Select(p => new { p.UserId, p.FullName }).ToListAsync();
        foreach (var c in clients) result[c.UserId] = c.FullName;

        var trainers = await _db.TrainerProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.UserId)).Select(p => new { p.UserId, p.FullName }).ToListAsync();
        foreach (var t in trainers) result[t.UserId] = t.FullName;

        var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var users = await _db.Users.AsNoTracking()
                .Where(u => missing.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToListAsync();
            foreach (var u in users) result[u.Id] = u.UserName ?? "User";
        }

        foreach (var id in ids.Where(id => !result.ContainsKey(id)))
            result[id] = "Deleted user";

        return result;
    }

    // Returns the display name of one user.
    public async Task<string> GetDisplayNameAsync(string userId)
    {
        var names = await GetDisplayNamesAsync(new[] { userId });
        return names.TryGetValue(userId, out var name) ? name : "User";
    }

    // Lists approved trainers with their specialisations and current number of clients.
    public async Task<List<TrainerOption>> GetApprovedTrainersAsync()
    {
        var trainers = await _db.TrainerProfiles.AsNoTracking()
            .Where(t => t.IsApproved)
            .OrderBy(t => t.FullName)
            .Select(t => new { t.UserId, t.FullName, t.Specializations })
            .ToListAsync();

        var counts = await _db.ClientProfiles.AsNoTracking()
            .Where(c => c.TrainerId != null)
            .GroupBy(c => c.TrainerId!)
            .Select(g => new { TrainerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TrainerId, x => x.Count);

        return trainers
            .Select(t => new TrainerOption(t.UserId, t.FullName, t.Specializations,
                counts.TryGetValue(t.UserId, out var n) ? n : 0))
            .ToList();
    }

    // Returns the ids of every client assigned to the given trainer.
    public Task<List<string>> GetClientIdsForTrainerAsync(string trainerId) =>
        _db.ClientProfiles.AsNoTracking()
            .Where(c => c.TrainerId == trainerId)
            .Select(c => c.UserId)
            .ToListAsync();
}
