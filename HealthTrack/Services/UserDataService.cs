using HealthTrack.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

public interface IUserDataService
{
    Task<ServiceResult> DeleteUserCompletelyAsync(string userId);
    Task<object> ExportUserDataAsync(string userId);
}

/// <summary>
/// GDPR helpers: right to erasure (delete every record that belongs to a user)
/// and right of access / data portability (export everything as JSON).
/// </summary>
public sealed class UserDataService : IUserDataService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IFileStorageService _files;
    private readonly ILogger<UserDataService> _logger;

    public UserDataService(ApplicationDbContext db, UserManager<IdentityUser> userManager,
        IFileStorageService files, ILogger<UserDataService> logger)
    {
        _db = db;
        _userManager = userManager;
        _files = files;
        _logger = logger;
    }

    /// <summary>Right to erasure: removes or detaches every record that references the user inside one transaction, then deletes the account.</summary>
    public async Task<ServiceResult> DeleteUserCompletelyAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return ServiceResult.Fail("User not found.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Rows that reference the user through NO ACTION foreign keys must be handled first.
            await _db.PlanAssignments.Where(a => a.ClientId == userId).ExecuteDeleteAsync();
            await _db.PlanAssignments.Where(a => a.AssignedById == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AssignedById, (string?)null));
            await _db.WorkoutPlans.Where(p => p.CreatedById == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedById, (string?)null));
            await _db.DietPlans.Where(p => p.CreatedById == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedById, (string?)null));
            await _db.TrainerFeedback.Where(f => f.TrainerId == userId || f.ClientId == userId).ExecuteDeleteAsync();
            await _db.Appointments.Where(a => a.TrainerId == userId || a.ClientId == userId).ExecuteDeleteAsync();
            await _db.ClientProfiles.Where(c => c.TrainerId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.TrainerId, (string?)null));

            // Remove uploaded profile pictures.
            var pictures = await _db.ClientProfiles.Where(c => c.UserId == userId).Select(c => c.ProfilePicturePath)
                .Concat(_db.TrainerProfiles.Where(t => t.UserId == userId).Select(t => t.ProfilePicturePath))
                .ToListAsync();

            // Everything else (profiles, logs, measurements, notifications, preferences)
            // is removed by ON DELETE CASCADE when the Identity user is deleted.
            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                await transaction.RollbackAsync();
                return ServiceResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
            }

            // Audit entries are kept for accountability but no longer link to the person.
            await _db.AuditLogs.Where(a => a.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.UserName, "[deleted user]"));

            await transaction.CommitAsync();

            foreach (var path in pictures)
                _files.DeleteFile(path);

            return ServiceResult.Ok();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to delete user {UserId}", userId);
            return ServiceResult.Fail("The account could not be deleted because of an unexpected error.");
        }
    }

    /// <summary>Right of access: collects all of the user's data into one object that is downloaded as JSON.</summary>
    public async Task<object> ExportUserDataAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        var roles = user == null ? new List<string>() : (await _userManager.GetRolesAsync(user)).ToList();

        return new
        {
            ExportedAtUtc = DateTime.UtcNow,
            Account = new { user?.UserName, user?.Email, user?.EmailConfirmed, user?.PhoneNumber, user?.TwoFactorEnabled, Roles = roles },
            ClientProfile = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId),
            TrainerProfile = await _db.TrainerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId),
            Measurements = await _db.HealthMeasurements.AsNoTracking().Where(m => m.ClientId == userId).OrderBy(m => m.MeasuredOn).ToListAsync(),
            Workouts = await _db.WorkoutLogs.AsNoTracking().Where(w => w.ClientId == userId).OrderBy(w => w.LogDate).ToListAsync(),
            Meals = await _db.MealLogs.AsNoTracking().Where(m => m.ClientId == userId).OrderBy(m => m.LogDate).ToListAsync(),
            Appointments = await _db.Appointments.AsNoTracking().Where(a => a.ClientId == userId || a.TrainerId == userId).OrderBy(a => a.StartTime).ToListAsync(),
            Feedback = await _db.TrainerFeedback.AsNoTracking().Where(f => f.ClientId == userId || f.TrainerId == userId).ToListAsync(),
            Notifications = await _db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAtUtc).ToListAsync()
        };
    }
}

// ============================================================================
// Profile picture storage
// ============================================================================

public interface IFileStorageService
{
    Task<(string? Path, string? Error)> SaveProfilePictureAsync(IFormFile file);
    void DeleteFile(string? relativePath);
}

/// <summary>
/// Stores profile pictures under wwwroot/uploads/profiles with random file names.
/// Validates size, extension and the file signature ("magic bytes") to block disguised files.
/// </summary>
public sealed class FileStorageService : IFileStorageService
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const string UploadFolder = "uploads/profiles";
    private readonly IWebHostEnvironment _environment;

    public FileStorageService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<(string? Path, string? Error)> SaveProfilePictureAsync(IFormFile file)
    {
        if (file.Length == 0)
            return (null, "The selected file is empty.");
        if (file.Length > MaxBytes)
            return (null, "Profile pictures must be 2 MB or smaller.");

        var extension = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png"))
            return (null, "Only JPG and PNG images are allowed.");

        var header = new byte[8];
        await using (var stream = file.OpenReadStream())
        {
            var read = await stream.ReadAsync(header.AsMemory(0, header.Length));
            if (read < 4)
                return (null, "The file is not a valid image.");
        }

        var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        var isPng = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
        if (!isJpeg && !isPng)
            return (null, "The file content is not a JPG or PNG image.");

        var webRoot = _environment.WebRootPath ?? System.IO.Path.Combine(_environment.ContentRootPath, "wwwroot");
        var folder = System.IO.Path.Combine(webRoot, "uploads", "profiles");
        Directory.CreateDirectory(folder);

        var fileName = Guid.NewGuid().ToString("N") + (isPng ? ".png" : ".jpg");
        await using (var output = File.Create(System.IO.Path.Combine(folder, fileName)))
        {
            await file.CopyToAsync(output);
        }

        return ("/" + UploadFolder + "/" + fileName, null);
    }

    /// <summary>Deletes a previously uploaded file (only inside the uploads folder).</summary>
    public void DeleteFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.StartsWith("/" + UploadFolder + "/", StringComparison.Ordinal))
            return;

        var webRoot = _environment.WebRootPath ?? System.IO.Path.Combine(_environment.ContentRootPath, "wwwroot");
        var fullPath = System.IO.Path.Combine(webRoot, relativePath.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (IOException)
        {
            // Ignore: a leftover file is harmless.
        }
    }
}
