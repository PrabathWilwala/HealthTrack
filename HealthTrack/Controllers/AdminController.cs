using System.Diagnostics;
using System.Text.RegularExpressions;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

/// <summary>
/// Administrator area: system dashboard, user management (create, edit, activate/deactivate,
/// delete, trainer approval and assignment), plan and appointment oversight, policies,
/// broadcasts, notification templates and the audit log. Every change is audited.
/// </summary>
[Authorize(Roles = RoleNames.Administrator)]
public class AdminController : Controller
{
    private static readonly DateTime StartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IAnalyticsService _analytics;
    private readonly IUserDirectory _directory;
    private readonly INotificationService _notifications;
    private readonly IAppointmentService _appointments;
    private readonly ISettingsService _settings;
    private readonly IUserDataService _userData;
    private readonly IAuditService _audit;
    private readonly IAppEmailSender _emailSender;
    private readonly ReminderBackgroundService _reminders;

    public AdminController(ApplicationDbContext db, UserManager<IdentityUser> userManager, IAnalyticsService analytics,
        IUserDirectory directory, INotificationService notifications, IAppointmentService appointments,
        ISettingsService settings, IUserDataService userData, IAuditService audit, IAppEmailSender emailSender,
        ReminderBackgroundService reminders)
    {
        _db = db;
        _userManager = userManager;
        _analytics = analytics;
        _directory = directory;
        _notifications = notifications;
        _appointments = appointments;
        _settings = settings;
        _userData = userData;
        _audit = audit;
        _emailSender = emailSender;
        _reminders = reminders;
    }

    // =====================================================================
    // Dashboard
    // =====================================================================

    /// <summary>Administrator dashboard: user activity, usage, health, workload, approvals, alerts and activity feed.</summary>
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        var now = DateTime.UtcNow;
        var model = new AdminDashboardViewModel();

        // System health: database round-trip time.
        var stopwatch = Stopwatch.StartNew();
        model.DatabaseHealthy = await _db.Database.CanConnectAsync();
        model.DatabaseLatencyMs = stopwatch.ElapsedMilliseconds;
        model.Uptime = now - StartedAtUtc;

        var roles = await LoadUserRolesAsync();
        model.TotalUsers = await _db.Users.CountAsync();
        model.Clients = roles.Count(r => r.Value == RoleNames.Client);
        model.Trainers = roles.Count(r => r.Value == RoleNames.Trainer);
        model.Administrators = roles.Count(r => r.Value == RoleNames.Administrator);

        var since30 = now.AddDays(-30);
        model.NewRegistrations30Days =
            await _db.ClientProfiles.CountAsync(c => c.CreatedAtUtc >= since30) +
            await _db.TrainerProfiles.CountAsync(t => t.CreatedAtUtc >= since30);

        var todayUtc = DateTime.Today.ToUniversalTime();
        model.LoginsToday = await _db.AuditLogs.CountAsync(a => a.Action == "Login.Success" && a.CreatedAtUtc >= todayUtc);
        model.FailedLogins24Hours = await _db.AuditLogs.CountAsync(a => a.Action.StartsWith("Login.Failed") && a.CreatedAtUtc >= now.AddHours(-24));
        model.ActiveUsers7Days = await _db.AuditLogs.Where(a => a.UserId != null && a.CreatedAtUtc >= now.AddDays(-7))
            .Select(a => a.UserId).Distinct().CountAsync();
        model.UnassignedClients = await _db.ClientProfiles.CountAsync(c => c.TrainerId == null);
        model.FlaggedPlans = await _db.WorkoutPlans.CountAsync(p => p.IsFlagged) + await _db.DietPlans.CountAsync(p => p.IsFlagged);
        model.AppointmentConflicts = (await _appointments.FindConflictsAsync()).Count;
        model.NotificationsSent24Hours = await _db.Notifications.CountAsync(n => n.CreatedAtUtc >= now.AddHours(-24));

        var currentAdmin = await _userManager.GetUserAsync(User);
        model.CurrentAdminHasTwoFactor = currentAdmin?.TwoFactorEnabled ?? false;

        model.PendingTrainers = await _db.TrainerProfiles.AsNoTracking()
            .Where(t => !t.IsApproved && !t.IsRejected).OrderBy(t => t.CreatedAtUtc).ToListAsync();

        // Trainer workload.
        var trainers = await _directory.GetApprovedTrainersAsync();
        var upcoming = await _db.Appointments.AsNoTracking()
            .Where(a => a.StartTime >= DateTime.Now && (a.Status == AppointmentStatus.Approved || a.Status == AppointmentStatus.Pending))
            .Select(a => a.TrainerId).ToListAsync();
        var performance = await _analytics.GetTrainerPerformanceAsync(DateTime.Today.AddDays(-29), DateTime.Today);
        model.TrainerWorkload = trainers.Select(t => new TrainerWorkloadRow
        {
            TrainerId = t.UserId,
            Name = t.FullName,
            Specializations = t.Specializations,
            Clients = t.ClientCount,
            UpcomingAppointments = upcoming.Count(id => id == t.UserId),
            AverageAdherence = performance.FirstOrDefault(p => p.TrainerId == t.UserId)?.AverageAdherence
        }).OrderByDescending(t => t.Clients).ToList();

        model.SecurityAlerts = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.IsSecurityEvent && a.Action != "Login.Success" && a.CreatedAtUtc >= now.AddDays(-7))
            .OrderByDescending(a => a.CreatedAtUtc).Take(8).ToListAsync();
        model.RecentActivity = await _db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.CreatedAtUtc).Take(12).ToListAsync();

        // Charts: logins over 14 days, feature usage and peak hours over 30 days.
        var authEvents = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Category == "Auth" && a.CreatedAtUtc >= now.AddDays(-14))
            .Select(a => new { a.Action, a.CreatedAtUtc }).ToListAsync();
        var days = Enumerable.Range(0, 14).Select(i => DateTime.Today.AddDays(-13 + i)).ToList();
        model.LoginChart.Labels = days.Select(DateHelpers.ShortLabel).ToList();
        model.LoginChart.Datasets.Add(new ChartDataset
        {
            Label = "Successful logins",
            Data = days.Select(d => (decimal?)authEvents.Count(e => e.Action == "Login.Success" && e.CreatedAtUtc.ToLocalTime().Date == d)).ToList()
        });
        model.LoginChart.Datasets.Add(new ChartDataset
        {
            Label = "Failed logins",
            Data = days.Select(d => (decimal?)authEvents.Count(e => e.Action.StartsWith("Login.Failed") && e.CreatedAtUtc.ToLocalTime().Date == d)).ToList()
        });

        var usage = await _db.AuditLogs.AsNoTracking().Where(a => a.CreatedAtUtc >= since30)
            .Select(a => new { a.Category, a.CreatedAtUtc }).ToListAsync();
        var byCategory = usage.GroupBy(u => u.Category).OrderByDescending(g => g.Count()).Take(10).ToList();
        model.FeatureUsageChart.Labels = byCategory.Select(g => g.Key).ToList();
        model.FeatureUsageChart.Datasets.Add(new ChartDataset { Label = "Actions (30 days)", Data = byCategory.Select(g => (decimal?)g.Count()).ToList() });
        model.PeakHoursChart.Labels = Enumerable.Range(0, 24).Select(h => $"{h:00}h").ToList();
        model.PeakHoursChart.Datasets.Add(new ChartDataset
        {
            Label = "Activity by hour",
            Data = Enumerable.Range(0, 24).Select(h => (decimal?)usage.Count(u => u.CreatedAtUtc.ToLocalTime().Hour == h)).ToList()
        });

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunReminders()
    {
        await _reminders.RunOnceAsync();
        await _audit.LogAsync("Admin", "Reminders.RunManually");
        TempData["Success"] = "The reminder job ran successfully. Any due reminders have been sent.";
        return RedirectToAction(nameof(Dashboard));
    }

    // =====================================================================
    // User management
    // =====================================================================

    /// <summary>Searchable, filterable, paged list of all users.</summary>
    [HttpGet]
    public async Task<IActionResult> Users(string? search, string? role, string? status, int page = 1)
    {
        var model = new UserListViewModel { Search = search, Role = role, Status = status, Page = Math.Max(1, page) };
        var rows = await BuildUserRowsAsync();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r => r.UserName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                   r.Email.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                   r.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(role))
            rows = rows.Where(r => r.Role == role).ToList();

        rows = status switch
        {
            "active" => rows.Where(r => !r.IsDeactivated).ToList(),
            "deactivated" => rows.Where(r => r.IsDeactivated).ToList(),
            "pending" => rows.Where(r => r.TrainerApproved == false && !r.TrainerRejected).ToList(),
            "unconfirmed" => rows.Where(r => !r.EmailConfirmed).ToList(),
            "unassigned" => rows.Where(r => r.Role == RoleNames.Client && r.AssignedTrainer == null).ToList(),
            _ => rows
        };

        model.TotalCount = rows.Count;
        model.Users = rows.OrderBy(r => r.Role).ThenBy(r => r.FullName)
            .Skip((model.Page - 1) * model.PageSize).Take(model.PageSize).ToList();
        return View(model);
    }

    /// <summary>Shows the form / creates a client, trainer or administrator account.</summary>
    [HttpGet]
    public IActionResult CreateUser() => View(new CreateUserViewModel());

    /// <summary>Shows the form / creates a client, trainer or administrator account.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreateUserViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = new IdentityUser { UserName = model.UserName!.Trim(), Email = model.Email!.Trim(), EmailConfirmed = true };
        var result = await _userManager.CreateAsync(user, model.Password!);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }
        await _userManager.AddToRoleAsync(user, model.Role!);

        if (model.Role == RoleNames.Client)
        {
            _db.ClientProfiles.Add(new ClientProfile
            {
                UserId = user.Id, FullName = model.FullName!.Trim(), Age = model.Age!.Value, Gender = model.Gender!,
                HeightCm = model.HeightCm!.Value, WeightKg = model.WeightKg!.Value, FitnessGoals = model.FitnessGoals?.Trim()
            });
            _db.HealthMeasurements.Add(new HealthMeasurement
            {
                ClientId = user.Id, MeasuredOn = DateTime.Today, WeightKg = model.WeightKg!.Value,
                Bmi = BmiCalculator.Calculate(model.WeightKg!.Value, model.HeightCm!.Value)
            });
        }
        else if (model.Role == RoleNames.Trainer)
        {
            _db.TrainerProfiles.Add(new TrainerProfile
            {
                UserId = user.Id, FullName = model.FullName!.Trim(), Qualifications = model.Qualifications!.Trim(),
                Specializations = model.Specializations!.Trim(), IsApproved = true, ApprovedAtUtc = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync("Admin", "User.Created", $"{model.Role} '{user.UserName}' created by administrator");
        TempData["Success"] = $"{model.Role} account '{user.UserName}' created.";
        return RedirectToAction(nameof(Users));
    }

    /// <summary>Shows / saves account details and role.</summary>
    [HttpGet]
    public async Task<IActionResult> EditUser(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();

        var role = (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;
        return View(new EditUserViewModel
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            FullName = await _directory.GetDisplayNameAsync(user.Id),
            Role = role,
            CurrentRole = role,
            EmailConfirmed = user.EmailConfirmed,
            IsDeactivated = IsDeactivated(user),
            TwoFactorEnabled = user.TwoFactorEnabled
        });
    }

    /// <summary>Shows / saves account details and role.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(EditUserViewModel model)
    {
        var user = await _userManager.FindByIdAsync(model.Id);
        if (user == null) return NotFound();

        var currentRole = (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;
        if (!RoleNames.All.Contains(model.Role))
            ModelState.AddModelError(nameof(model.Role), "Choose a valid role.");
        if (user.Id == User.GetUserId() && model.Role != currentRole)
            ModelState.AddModelError(nameof(model.Role), "You cannot change your own role.");

        if (!ModelState.IsValid)
        {
            model.CurrentRole = currentRole;
            model.IsDeactivated = IsDeactivated(user);
            return View(model);
        }

        user.UserName = model.UserName!.Trim();
        user.Email = model.Email!.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim();
        user.EmailConfirmed = model.EmailConfirmed;
        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            foreach (var error in update.Errors) ModelState.AddModelError(string.Empty, error.Description);
            model.CurrentRole = currentRole;
            return View(model);
        }

        // Keep the profile name in sync.
        if (!string.IsNullOrWhiteSpace(model.FullName))
        {
            var client = await _db.ClientProfiles.FindAsync(user.Id);
            if (client != null) client.FullName = model.FullName.Trim();
            var trainer = await _db.TrainerProfiles.FindAsync(user.Id);
            if (trainer != null) trainer.FullName = model.FullName.Trim();
        }

        if (model.Role != currentRole)
            await ChangeRoleAsync(user, currentRole, model.Role!, model.FullName);

        await _db.SaveChangesAsync();
        await _audit.LogAsync("Admin", "User.Edited", $"User '{user.UserName}' updated" + (model.Role != currentRole ? $"; role {currentRole} → {model.Role}" : string.Empty));
        TempData["Success"] = "User updated.";
        return RedirectToAction(nameof(EditUser), new { id = user.Id });
    }

    /// <summary>Sets a new password for a user.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPassword(AdminSetPasswordViewModel model)
    {
        var user = await _userManager.FindByIdAsync(model.Id);
        if (user == null) return NotFound();
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Passwords must be at least 8 characters.";
            return RedirectToAction(nameof(EditUser), new { id = model.Id });
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword!);
        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(EditUser), new { id = model.Id });
        }

        await _audit.LogAsync("Admin", "User.PasswordReset", $"Password reset for '{user.UserName}'", true);
        TempData["Success"] = "The password has been reset. Share it with the user securely.";
        return RedirectToAction(nameof(EditUser), new { id = model.Id });
    }

    /// <summary>Emails a password reset link to a user.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendPasswordReset(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null || string.IsNullOrEmpty(user.Email)) return NotFound();

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        var link = Url.Action("ResetPassword", "Account", new { userId = user.Id, code }, Request.Scheme)!;
        await _emailSender.SendAsync(user.Email, "Reset your HealthTrack password",
            $"<p>An administrator started a password reset for your account.</p><p><a href='{System.Net.WebUtility.HtmlEncode(link)}'>Choose a new password</a></p>");

        await _audit.LogAsync("Admin", "User.PasswordResetLinkSent", $"Reset link sent to '{user.UserName}'", true);
        TempData["Success"] = "A password reset link has been emailed to the user.";
        return RedirectToAction(nameof(EditUser), new { id });
    }

    /// <summary>Deactivates (and signs out) or reactivates an account.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(string id, string? returnUrl)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (user.Id == User.GetUserId())
        {
            TempData["Error"] = "You cannot deactivate your own account.";
            return RedirectBack(returnUrl);
        }

        if (IsDeactivated(user))
        {
            await _userManager.SetLockoutEndDateAsync(user, null);
            await _userManager.ResetAccessFailedCountAsync(user);
            await _audit.LogAsync("Admin", "User.Reactivated", $"'{user.UserName}' reactivated", true);
            TempData["Success"] = $"'{user.UserName}' has been reactivated.";
        }
        else
        {
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            // Changing the security stamp signs the user out everywhere within a minute.
            await _userManager.UpdateSecurityStampAsync(user);
            await _audit.LogAsync("Admin", "User.Deactivated", $"'{user.UserName}' deactivated", true);
            TempData["Success"] = $"'{user.UserName}' has been deactivated and signed out.";
        }
        return RedirectBack(returnUrl);
    }

    /// <summary>Deletes an account and all associated data.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(string id)
    {
        if (id == User.GetUserId())
        {
            TempData["Error"] = "You cannot delete your own account from here.";
            return RedirectToAction(nameof(Users));
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        var userName = user.UserName;

        var result = await _userData.DeleteUserCompletelyAsync(id);
        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Users));
        }

        await _audit.LogAsync("Admin", "User.Deleted", $"'{userName}' and all associated data deleted", true);
        TempData["Success"] = $"'{userName}' and all associated data have been deleted.";
        return RedirectToAction(nameof(Users));
    }

    /// <summary>Approves a trainer registration and notifies the trainer.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveTrainer(string id, string? returnUrl)
    {
        var profile = await _db.TrainerProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        profile.IsApproved = true;
        profile.IsRejected = false;
        profile.RejectionReason = null;
        profile.ApprovedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _notifications.NotifyFromTemplateAsync(id, NotificationType.SystemAlert, NotificationTemplateKeys.TrainerApproved,
            new Dictionary<string, string>(), "/Trainer/Dashboard");
        var user = await _userManager.FindByIdAsync(id);
        if (!string.IsNullOrEmpty(user?.Email))
            await _emailSender.SendAsync(user.Email, "Your HealthTrack trainer account is approved",
                "<p>Good news! An administrator approved your trainer account. You can now sign in.</p>");

        await _audit.LogAsync("Admin", "Trainer.Approved", $"Trainer '{profile.FullName}' approved");
        TempData["Success"] = $"{profile.FullName} has been approved.";
        return RedirectBack(returnUrl);
    }

    /// <summary>Rejects a trainer registration with an optional reason.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectTrainer(string id, string? reason, string? returnUrl)
    {
        var profile = await _db.TrainerProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        profile.IsApproved = false;
        profile.IsRejected = true;
        profile.RejectionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await _db.SaveChangesAsync();

        var user = await _userManager.FindByIdAsync(id);
        if (!string.IsNullOrEmpty(user?.Email))
            await _emailSender.SendAsync(user.Email, "Your HealthTrack trainer application",
                "<p>Unfortunately your trainer application was not approved.</p>" +
                (profile.RejectionReason != null ? $"<p>Reason: {System.Net.WebUtility.HtmlEncode(profile.RejectionReason)}</p>" : string.Empty));

        await _audit.LogAsync("Admin", "Trainer.Rejected", $"Trainer '{profile.FullName}' rejected");
        TempData["Success"] = $"{profile.FullName}'s application has been rejected.";
        return RedirectBack(returnUrl);
    }

    /// <summary>Shows trainer suggestions / assigns a trainer to a client and notifies both.</summary>
    [HttpGet]
    public async Task<IActionResult> AssignTrainer(string id)
    {
        var client = await _db.ClientProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == id);
        if (client == null) return NotFound();

        var trainers = await _directory.GetApprovedTrainersAsync();
        return View(new AssignTrainerViewModel
        {
            ClientId = id,
            ClientName = client.FullName,
            FitnessGoals = client.FitnessGoals,
            CurrentTrainerId = client.TrainerId,
            TrainerId = client.TrainerId,
            Trainers = trainers,
            SuggestedTrainerIds = SuggestTrainers(client.FitnessGoals, trainers)
        });
    }

    /// <summary>Shows trainer suggestions / assigns a trainer to a client and notifies both.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignTrainer(AssignTrainerViewModel model)
    {
        var client = await _db.ClientProfiles.FirstOrDefaultAsync(c => c.UserId == model.ClientId);
        if (client == null) return NotFound();

        if (string.IsNullOrEmpty(model.TrainerId))
        {
            client.TrainerId = null;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("Admin", "Trainer.Unassigned", $"Trainer removed from '{client.FullName}'");
            TempData["Success"] = $"{client.FullName} no longer has an assigned trainer.";
            return RedirectToAction(nameof(Users), new { role = RoleNames.Client });
        }

        var trainer = await _db.TrainerProfiles.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == model.TrainerId && t.IsApproved);
        if (trainer == null)
        {
            TempData["Error"] = "Please choose an approved trainer.";
            return RedirectToAction(nameof(AssignTrainer), new { id = model.ClientId });
        }

        client.TrainerId = trainer.UserId;
        await _db.SaveChangesAsync();

        await _notifications.NotifyFromTemplateAsync(trainer.UserId, NotificationType.ClientAssignment, NotificationTemplateKeys.ClientAssigned,
            new Dictionary<string, string> { ["ClientName"] = client.FullName }, "/Trainer/Client/" + client.UserId);
        await _notifications.NotifyFromTemplateAsync(client.UserId, NotificationType.ClientAssignment, NotificationTemplateKeys.TrainerAssigned,
            new Dictionary<string, string> { ["TrainerName"] = trainer.FullName }, "/Appointments/Create");

        await _audit.LogAsync("Admin", "Trainer.Assigned", $"'{trainer.FullName}' assigned to '{client.FullName}'");
        TempData["Success"] = $"{trainer.FullName} is now {client.FullName}'s trainer.";
        return RedirectToAction(nameof(Users), new { role = RoleNames.Client });
    }

    /// <summary>Audit trail and activity counts for one user.</summary>
    [HttpGet]
    public async Task<IActionResult> UserActivity(string id)
    {
        var rows = await BuildUserRowsAsync();
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row == null) return NotFound();

        return View(new UserActivityViewModel
        {
            User = row,
            Entries = await _db.AuditLogs.AsNoTracking().Where(a => a.UserId == id)
                .OrderByDescending(a => a.CreatedAtUtc).Take(200).ToListAsync(),
            Workouts = await _db.WorkoutLogs.CountAsync(w => w.ClientId == id),
            Meals = await _db.MealLogs.CountAsync(m => m.ClientId == id),
            Measurements = await _db.HealthMeasurements.CountAsync(m => m.ClientId == id),
            Appointments = await _db.Appointments.CountAsync(a => a.ClientId == id || a.TrainerId == id)
        });
    }

    // =====================================================================
    // Plan oversight
    // =====================================================================

    /// <summary>Oversight of every workout and diet plan, with popularity.</summary>
    [HttpGet]
    public async Task<IActionResult> Plans(string? filter)
    {
        var workouts = await _db.WorkoutPlans.AsNoTracking()
            .Select(p => new { p.Id, p.Title, p.CreatedById, p.IsTemplate, p.IsSystemTemplate, p.IsFlagged, p.FlagReason, Items = p.Exercises.Count, Assigned = p.Assignments.Count, p.UpdatedAtUtc })
            .ToListAsync();
        var diets = await _db.DietPlans.AsNoTracking()
            .Select(p => new { p.Id, p.Title, p.CreatedById, p.IsTemplate, p.IsSystemTemplate, p.IsFlagged, p.FlagReason, Items = p.Meals.Count, Assigned = p.Assignments.Count, p.UpdatedAtUtc })
            .ToListAsync();

        var names = await _directory.GetDisplayNamesAsync(workouts.Select(w => w.CreatedById).Concat(diets.Select(d => d.CreatedById)).Where(id => id != null)!);
        string Creator(string? id) => id != null && names.TryGetValue(id, out var n) ? n : "System";

        var rows = workouts.Select(p => new AdminPlanRow
        {
            Id = p.Id, Type = "Workout", Title = p.Title, CreatedBy = Creator(p.CreatedById), IsTemplate = p.IsTemplate,
            IsSystemTemplate = p.IsSystemTemplate, IsFlagged = p.IsFlagged, FlagReason = p.FlagReason, Items = p.Items,
            TimesAssigned = p.Assigned, UpdatedAtUtc = p.UpdatedAtUtc
        }).Concat(diets.Select(p => new AdminPlanRow
        {
            Id = p.Id, Type = "Diet", Title = p.Title, CreatedBy = Creator(p.CreatedById), IsTemplate = p.IsTemplate,
            IsSystemTemplate = p.IsSystemTemplate, IsFlagged = p.IsFlagged, FlagReason = p.FlagReason, Items = p.Items,
            TimesAssigned = p.Assigned, UpdatedAtUtc = p.UpdatedAtUtc
        }));

        rows = filter switch
        {
            "flagged" => rows.Where(r => r.IsFlagged),
            "templates" => rows.Where(r => r.IsTemplate),
            "workout" => rows.Where(r => r.Type == "Workout"),
            "diet" => rows.Where(r => r.Type == "Diet"),
            _ => rows
        };

        return View(new AdminPlansViewModel
        {
            Filter = filter,
            Plans = rows.OrderByDescending(r => r.IsFlagged).ThenByDescending(r => r.UpdatedAtUtc).ToList(),
            Popular = (await _analytics.GetRoutineStatsAsync(null)).Take(5).ToList()
        });
    }

    /// <summary>Flags a plan for review (notifying its author) or clears the flag.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FlagPlan(string type, int id, bool flag, string? reason)
    {
        string? title = null;
        string? ownerId = null;
        if (type == "Workout")
        {
            var plan = await _db.WorkoutPlans.FindAsync(id);
            if (plan == null) return NotFound();
            plan.IsFlagged = flag;
            plan.FlagReason = flag ? (string.IsNullOrWhiteSpace(reason) ? "Flagged for review" : reason.Trim()) : null;
            title = plan.Title;
            ownerId = plan.CreatedById;
        }
        else
        {
            var plan = await _db.DietPlans.FindAsync(id);
            if (plan == null) return NotFound();
            plan.IsFlagged = flag;
            plan.FlagReason = flag ? (string.IsNullOrWhiteSpace(reason) ? "Flagged for review" : reason.Trim()) : null;
            title = plan.Title;
            ownerId = plan.CreatedById;
        }
        await _db.SaveChangesAsync();

        if (flag && ownerId != null)
            await _notifications.NotifyAsync(ownerId, NotificationType.SystemAlert, "A plan was flagged for review",
                $"An administrator flagged \"{title}\": {reason}", type == "Workout" ? "/WorkoutPlans/Details/" + id : "/DietPlans/Details/" + id);

        await _audit.LogAsync("Admin", flag ? "Plan.Flagged" : "Plan.Unflagged", $"{type} plan \"{title}\"");
        TempData["Success"] = flag ? $"\"{title}\" has been flagged." : $"\"{title}\" is no longer flagged.";
        return RedirectToAction(nameof(Plans));
    }

    /// <summary>Removes a plan that violates policy.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlan(string type, int id)
    {
        string title;
        if (type == "Workout")
        {
            var plan = await _db.WorkoutPlans.FindAsync(id);
            if (plan == null) return NotFound();
            title = plan.Title;
            _db.WorkoutPlans.Remove(plan);
        }
        else
        {
            var plan = await _db.DietPlans.FindAsync(id);
            if (plan == null) return NotFound();
            title = plan.Title;
            _db.DietPlans.Remove(plan);
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Admin", "Plan.Removed", $"{type} plan \"{title}\" removed by administrator", true);
        TempData["Success"] = $"\"{title}\" has been removed.";
        return RedirectToAction(nameof(Plans));
    }

    // =====================================================================
    // Appointment oversight and policies
    // =====================================================================

    /// <summary>All appointments with filters, conflict detection, analytics and policy settings.</summary>
    [HttpGet]
    public async Task<IActionResult> Appointments(AppointmentStatus? status, string? trainerId, DateTime? from, DateTime? to)
    {
        var start = (from ?? DateTime.Today.AddDays(-30)).Date;
        var end = (to ?? DateTime.Today.AddDays(30)).Date;

        var query = _db.Appointments.AsNoTracking().Where(a => a.StartTime >= start && a.StartTime < end.AddDays(1));
        if (status.HasValue) query = query.Where(a => a.Status == status.Value);
        if (!string.IsNullOrEmpty(trainerId)) query = query.Where(a => a.TrainerId == trainerId);
        var list = await query.OrderByDescending(a => a.StartTime).Take(500).ToListAsync();

        var conflicts = await _appointments.FindConflictsAsync();
        var names = await _directory.GetDisplayNamesAsync(
            list.SelectMany(a => new[] { a.ClientId, a.TrainerId })
                .Concat(conflicts.Select(c => c.First.TrainerId)));

        var model = new AdminAppointmentsViewModel
        {
            Status = status,
            TrainerId = trainerId,
            From = start,
            To = end,
            Items = list.Select(a => (a, names[a.ClientId], names[a.TrainerId])).ToList(),
            Conflicts = conflicts.Select(c => (c, names[c.First.TrainerId])).ToList(),
            Trainers = await _directory.GetApprovedTrainersAsync(),
            Total = list.Count,
            Policy = await LoadPolicyAsync()
        };

        var decided = list.Count(a => a.Status != AppointmentStatus.Pending);
        model.CancellationRate = decided == 0 ? (decimal?)null : Math.Round(list.Count(a => a.Status == AppointmentStatus.Cancelled) * 100m / decided, 0);

        var weeks = AnalyticsService.Periods(start, end, ReportGrouping.Week);
        model.VolumeChart.Labels = weeks.Select(w => AnalyticsService.PeriodLabel(w, ReportGrouping.Week)).ToList();
        model.VolumeChart.Datasets.Add(new ChartDataset { Label = "Appointments", Data = weeks.Select(w => (decimal?)list.Count(a => DateHelpers.StartOfWeek(a.StartTime) == w)).ToList() });

        var statuses = Enum.GetValues<AppointmentStatus>();
        model.StatusChart.Labels = statuses.Select(s => s.ToString()).ToList();
        model.StatusChart.Datasets.Add(new ChartDataset { Label = "Appointments", Data = statuses.Select(s => (decimal?)list.Count(a => a.Status == s)).ToList() });

        var hours = Enumerable.Range(7, 14).ToList(); // 07:00–20:00
        model.HoursChart.Labels = hours.Select(h => $"{h:00}:00").ToList();
        model.HoursChart.Datasets.Add(new ChartDataset { Label = "Bookings by start hour", Data = hours.Select(h => (decimal?)list.Count(a => a.StartTime.Hour == h)).ToList() });

        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        model.WeekdayChart.Labels = weekdays.Select(d => d.ToString().Substring(0, 3)).ToList();
        model.WeekdayChart.Datasets.Add(new ChartDataset { Label = "Bookings by weekday", Data = weekdays.Select(d => (decimal?)list.Count(a => a.StartTime.DayOfWeek == d)).ToList() });

        return View(model);
    }

    /// <summary>Moves an appointment to resolve a conflict.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RescheduleAppointment(int id, DateTime? newStart)
    {
        if (newStart == null)
        {
            TempData["Error"] = "Choose a new date and time.";
            return RedirectToAction(nameof(Appointments));
        }
        var result = await _appointments.AdminRescheduleAsync(id, newStart.Value);
        if (result.Succeeded) TempData["Success"] = "Appointment moved. Both parties were notified.";
        else TempData["Error"] = result.Error;
        return RedirectToAction(nameof(Appointments));
    }

    /// <summary>Saves the appointment and alert policies.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Policies(PolicyViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please enter valid policy values.";
            return RedirectToAction(nameof(Appointments));
        }

        await _settings.SetAsync(SettingKeys.MinBookingNoticeHours, model.MinBookingNoticeHours.ToString());
        await _settings.SetAsync(SettingKeys.MinCancellationNoticeHours, model.MinCancellationNoticeHours.ToString());
        await _settings.SetAsync(SettingKeys.MaxCancellationsPerMonth, model.MaxCancellationsPerMonth.ToString());
        await _settings.SetAsync(SettingKeys.AdherenceAlertThreshold, model.AdherenceAlertThreshold.ToString());
        await _audit.LogAsync("Admin", "Policies.Updated",
            $"Booking notice {model.MinBookingNoticeHours}h, cancellation notice {model.MinCancellationNoticeHours}h, max cancellations {model.MaxCancellationsPerMonth}, adherence threshold {model.AdherenceAlertThreshold}%");
        TempData["Success"] = "Policies saved.";
        return RedirectToAction(nameof(Appointments));
    }

    // =====================================================================
    // Notifications: broadcasts and templates
    // =====================================================================

    /// <summary>Shows history / sends an announcement to all users or one role.</summary>
    [HttpGet]
    public async Task<IActionResult> Broadcast()
    {
        return View(new BroadcastViewModel { History = await LoadBroadcastHistoryAsync() });
    }

    /// <summary>Shows history / sends an announcement to all users or one role.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Broadcast(BroadcastViewModel model)
    {
        if (!string.IsNullOrEmpty(model.Role) && !RoleNames.All.Contains(model.Role))
            ModelState.AddModelError(nameof(model.Role), "Choose a valid audience.");
        if (!ModelState.IsValid)
        {
            model.History = await LoadBroadcastHistoryAsync();
            return View(model);
        }

        var count = await _notifications.BroadcastAsync(string.IsNullOrEmpty(model.Role) ? null : model.Role,
            model.Title!.Trim(), model.Message!.Trim(), string.IsNullOrWhiteSpace(model.LinkUrl) ? null : model.LinkUrl.Trim());
        await _audit.LogAsync("Admin", "Broadcast.Sent", $"\"{model.Title}\" to {(string.IsNullOrEmpty(model.Role) ? "all users" : model.Role + "s")} ({count})");
        TempData["Success"] = $"Announcement sent to {count} user(s).";
        return RedirectToAction(nameof(Broadcast));
    }

    /// <summary>Lists notification templates.</summary>
    [HttpGet]
    public async Task<IActionResult> Templates()
    {
        return View(await _db.NotificationTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync());
    }

    /// <summary>Shows / saves a notification template.</summary>
    [HttpGet]
    public async Task<IActionResult> EditTemplate(int id)
    {
        var template = await _db.NotificationTemplates.FindAsync(id);
        if (template == null) return NotFound();
        return View(new TemplateEditViewModel
        {
            Id = template.Id,
            Key = template.Key,
            Name = template.Name,
            TitleTemplate = template.TitleTemplate,
            BodyTemplate = template.BodyTemplate,
            AvailableTokens = TokensFor(template.Key)
        });
    }

    /// <summary>Shows / saves a notification template.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTemplate(TemplateEditViewModel model)
    {
        var template = await _db.NotificationTemplates.FindAsync(model.Id);
        if (template == null) return NotFound();
        if (!ModelState.IsValid)
        {
            model.Key = template.Key;
            model.AvailableTokens = TokensFor(template.Key);
            return View(model);
        }

        template.Name = model.Name!.Trim();
        template.TitleTemplate = model.TitleTemplate!.Trim();
        template.BodyTemplate = model.BodyTemplate!.Trim();
        template.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("Admin", "Template.Updated", template.Key);
        TempData["Success"] = "Template saved.";
        return RedirectToAction(nameof(Templates));
    }

    /// <summary>Restores a template's default wording.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTemplate(int id)
    {
        var template = await _db.NotificationTemplates.FindAsync(id);
        if (template == null) return NotFound();
        var defaults = NotificationTemplateKeys.Defaults.FirstOrDefault(d => d.Key == template.Key);
        if (defaults.Key != null)
        {
            template.Name = defaults.Name;
            template.TitleTemplate = defaults.Title;
            template.BodyTemplate = defaults.Body;
            template.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("Admin", "Template.Reset", template.Key);
            TempData["Success"] = "Template restored to its default text.";
        }
        return RedirectToAction(nameof(Templates));
    }

    // =====================================================================
    // Audit log
    // =====================================================================

    /// <summary>Searchable audit log.</summary>
    [HttpGet]
    public async Task<IActionResult> AuditLog(string? search, string? category, bool securityOnly = false, int page = 1)
    {
        var model = new AuditLogViewModel { Search = search, Category = category, SecurityOnly = securityOnly, Page = Math.Max(1, page) };
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(a => (a.UserName != null && a.UserName.Contains(term)) || a.Action.Contains(term) ||
                                     (a.Details != null && a.Details.Contains(term)) || (a.IpAddress != null && a.IpAddress.Contains(term)));
        }
        if (!string.IsNullOrEmpty(category)) query = query.Where(a => a.Category == category);
        if (securityOnly) query = query.Where(a => a.IsSecurityEvent);

        model.TotalCount = await query.CountAsync();
        model.Entries = await query.OrderByDescending(a => a.CreatedAtUtc)
            .Skip((model.Page - 1) * model.PageSize).Take(model.PageSize).ToListAsync();
        model.Categories = await _db.AuditLogs.Select(a => a.Category).Distinct().OrderBy(c => c).ToListAsync();
        return View(model);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static bool IsDeactivated(IdentityUser user) =>
        user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow.AddYears(10);

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Users));

    /// <summary>Maps user id → role name.</summary>
    private async Task<Dictionary<string, string>> LoadUserRolesAsync()
    {
        var pairs = await _db.UserRoles.Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync();
        var map = new Dictionary<string, string>();
        foreach (var pair in pairs)
            map[pair.UserId] = pair.Name ?? string.Empty;
        return map;
    }

    private async Task<List<UserRow>> BuildUserRowsAsync()
    {
        var users = await _db.Users.AsNoTracking().ToListAsync();
        var roles = await LoadUserRolesAsync();
        var clients = await _db.ClientProfiles.AsNoTracking().ToDictionaryAsync(c => c.UserId);
        var trainers = await _db.TrainerProfiles.AsNoTracking().ToDictionaryAsync(t => t.UserId);
        var lastActive = await _analytics.GetLastActiveAsync(users.Select(u => u.Id).ToList());

        return users.Select(u =>
        {
            clients.TryGetValue(u.Id, out var client);
            trainers.TryGetValue(u.Id, out var trainer);
            return new UserRow
            {
                Id = u.Id,
                UserName = u.UserName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                FullName = client?.FullName ?? trainer?.FullName ?? u.UserName ?? string.Empty,
                Role = roles.TryGetValue(u.Id, out var role) ? role : string.Empty,
                EmailConfirmed = u.EmailConfirmed,
                IsDeactivated = IsDeactivated(u),
                IsLockedOut = u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow && !IsDeactivated(u),
                TrainerApproved = trainer?.IsApproved,
                TrainerRejected = trainer?.IsRejected ?? false,
                AssignedTrainer = client?.TrainerId != null && trainers.TryGetValue(client.TrainerId, out var t) ? t.FullName : null,
                JoinedUtc = client?.CreatedAtUtc ?? trainer?.CreatedAtUtc,
                LastActiveUtc = lastActive.TryGetValue(u.Id, out var seen) ? seen : (DateTime?)null
            };
        }).ToList();
    }

    /// <summary>Moves a user to a different role, creating a minimal profile when needed.</summary>
    private async Task ChangeRoleAsync(IdentityUser user, string currentRole, string newRole, string? fullName)
    {
        if (!string.IsNullOrEmpty(currentRole))
            await _userManager.RemoveFromRoleAsync(user, currentRole);
        await _userManager.AddToRoleAsync(user, newRole);

        var name = string.IsNullOrWhiteSpace(fullName) ? user.UserName ?? "User" : fullName.Trim();
        if (newRole == RoleNames.Client && await _db.ClientProfiles.FindAsync(user.Id) == null)
        {
            _db.ClientProfiles.Add(new ClientProfile
            {
                UserId = user.Id, FullName = name, Age = 30, Gender = "Prefer not to say", HeightCm = 170, WeightKg = 70,
                FitnessGoals = "Please update your profile."
            });
            TempData["Warning"] = "A client profile with placeholder values was created. Ask the user to update their profile.";
        }
        else if (newRole == RoleNames.Trainer && await _db.TrainerProfiles.FindAsync(user.Id) == null)
        {
            _db.TrainerProfiles.Add(new TrainerProfile
            {
                UserId = user.Id, FullName = name, Qualifications = "To be updated", Specializations = "To be updated",
                IsApproved = true, ApprovedAtUtc = DateTime.UtcNow
            });
            TempData["Warning"] = "A trainer profile was created. Ask the user to complete their professional details.";
        }

        // Role changes take effect at the user's next request.
        await _userManager.UpdateSecurityStampAsync(user);
    }

    private async Task<PolicyViewModel> LoadPolicyAsync() => new()
    {
        MinBookingNoticeHours = await _settings.GetIntAsync(SettingKeys.MinBookingNoticeHours),
        MinCancellationNoticeHours = await _settings.GetIntAsync(SettingKeys.MinCancellationNoticeHours),
        MaxCancellationsPerMonth = await _settings.GetIntAsync(SettingKeys.MaxCancellationsPerMonth),
        AdherenceAlertThreshold = await _settings.GetIntAsync(SettingKeys.AdherenceAlertThreshold)
    };

    private async Task<List<(string Title, string Message, DateTime SentAtUtc, int Recipients)>> LoadBroadcastHistoryAsync()
    {
        var recent = await _db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.Broadcast)
            .OrderByDescending(n => n.CreatedAtUtc).Take(500)
            .Select(n => new { n.Title, n.Message, n.CreatedAtUtc }).ToListAsync();

        // Notifications of one broadcast share title, message and (to the minute) timestamp.
        return recent
            .GroupBy(n => new { n.Title, n.Message, Minute = n.CreatedAtUtc.ToString("yyyyMMddHHmm") })
            .Select(g => (g.Key.Title, g.Key.Message, g.Max(x => x.CreatedAtUtc), g.Count()))
            .OrderByDescending(x => x.Item3).Take(10).ToList();
    }

    /// <summary>Suggests trainers whose specialisations share keywords with the client's goals.</summary>
    private static List<string> SuggestTrainers(string? goals, List<TrainerOption> trainers)
    {
        if (string.IsNullOrWhiteSpace(goals)) return new List<string>();
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "and", "the", "for", "with", "to", "a", "my", "of", "in", "on" };
        var words = Regex.Split(goals.ToLowerInvariant(), @"[^a-z]+").Where(w => w.Length > 3 && !stopWords.Contains(w)).ToHashSet();
        return trainers
            .Where(t => Regex.Split(t.Specializations.ToLowerInvariant(), @"[^a-z]+")
                .Any(s => s.Length > 3 && words.Any(w => w.StartsWith(s[..4]) || s.StartsWith(w[..4]))))
            .Select(t => t.UserId).ToList();
    }

    private static List<string> TokensFor(string key)
    {
        var defaults = NotificationTemplateKeys.Defaults.FirstOrDefault(d => d.Key == key);
        var text = (defaults.Title ?? string.Empty) + " " + (defaults.Body ?? string.Empty);
        return Regex.Matches(text, @"\{(\w+)\}").Select(m => "{" + m.Groups[1].Value + "}").Distinct().ToList();
    }
}
