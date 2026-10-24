using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace HealthTrack.Controllers;

/// <summary>
/// Profile management for every role: personal details, picture, email, password,
/// two-factor authentication and GDPR tools (download my data, delete my account).
/// </summary>
[Authorize]
public class ProfileController : Controller
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _files;
    private readonly IUserDataService _userData;
    private readonly IAppEmailSender _emailSender;
    private readonly IAuditService _audit;
    private readonly IUserDirectory _directory;
    private readonly UrlEncoder _urlEncoder;
    private readonly IWebHostEnvironment _environment;

    public ProfileController(UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager,
        ApplicationDbContext db, IFileStorageService files, IUserDataService userData, IAppEmailSender emailSender,
        IAuditService audit, IUserDirectory directory, UrlEncoder urlEncoder, IWebHostEnvironment environment)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _files = files;
        _userData = userData;
        _emailSender = emailSender;
        _audit = audit;
        _directory = directory;
        _urlEncoder = urlEncoder;
        _environment = environment;
    }

    // =====================================================================
    // Profile details
    // =====================================================================

    /// <summary>Shows / saves the profile for the current role, including the profile picture.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        return View(await BuildProfileAsync(user));
    }

    /// <summary>Shows / saves the profile for the current role, including the profile picture.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ProfileViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var role = await GetRoleAsync(user);
        ValidateRoleFields(model, role);

        string? newPicture = null;
        if (model.ProfilePicture != null && model.ProfilePicture.Length > 0)
        {
            var (path, error) = await _files.SaveProfilePictureAsync(model.ProfilePicture);
            if (error != null) ModelState.AddModelError(nameof(model.ProfilePicture), error);
            newPicture = path;
        }

        if (!ModelState.IsValid)
        {
            _files.DeleteFile(newPicture);
            var fresh = await BuildProfileAsync(user);
            CopyEditableFields(model, fresh);
            return View(fresh);
        }

        user.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim();
        await _userManager.UpdateAsync(user);

        if (role == RoleNames.Client)
        {
            var profile = await _db.ClientProfiles.FindAsync(user.Id);
            if (profile != null)
            {
                profile.FullName = model.FullName!.Trim();
                profile.Age = model.Age!.Value;
                profile.Gender = model.Gender!.Trim();
                profile.HeightCm = model.HeightCm!.Value;
                profile.TargetWeightKg = model.TargetWeightKg;
                profile.DailyCalorieTarget = model.DailyCalorieTarget;
                profile.FitnessGoals = model.FitnessGoals?.Trim();
                profile.MedicalNotes = string.IsNullOrWhiteSpace(model.MedicalNotes) ? null : model.MedicalNotes.Trim();
                profile.ProfilePicturePath = UpdatePicture(profile.ProfilePicturePath, newPicture, model.RemovePicture);
            }
        }
        else if (role == RoleNames.Trainer)
        {
            var profile = await _db.TrainerProfiles.FindAsync(user.Id);
            if (profile != null)
            {
                profile.FullName = model.FullName!.Trim();
                profile.Qualifications = model.Qualifications!.Trim();
                profile.Specializations = model.Specializations!.Trim();
                profile.Certifications = model.Certifications?.Trim();
                profile.Bio = model.Bio?.Trim();
                profile.ProfilePicturePath = UpdatePicture(profile.ProfilePicturePath, newPicture, model.RemovePicture);
            }
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync("Profile", "Profile.Updated");
        TempData["Success"] = "Your profile has been updated.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Sends a confirmation link to a new email address.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeEmail(ChangeEmailViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please enter a valid email address.";
            return RedirectToAction(nameof(Index));
        }

        var newEmail = model.NewEmail!.Trim();
        if (string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Info"] = "That is already your email address.";
            return RedirectToAction(nameof(Index));
        }
        if (await _userManager.FindByEmailAsync(newEmail) != null)
        {
            TempData["Error"] = "That email address is already used by another account.";
            return RedirectToAction(nameof(Index));
        }

        // The change only happens once the new address is verified.
        var token = await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Action(nameof(ConfirmEmailChange), "Profile", new { email = newEmail, code }, Request.Scheme)!;
        await _emailSender.SendAsync(newEmail, "Confirm your new HealthTrack email",
            $"<p>Please confirm your new email address:</p><p><a href='{WebUtility.HtmlEncode(link)}'>Confirm email change</a></p>");

        TempData["Success"] = "We sent a confirmation link to " + newEmail + ". Your email changes once you open it.";
        if (_environment.IsDevelopment())
            TempData["DevLink"] = link;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Completes the email change after the new address is confirmed.</summary>
    [HttpGet]
    public async Task<IActionResult> ConfirmEmailChange(string? email, string? code)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code))
            return RedirectToAction(nameof(Index));

        try
        {
            var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            var result = await _userManager.ChangeEmailAsync(user, email, token);
            if (result.Succeeded)
            {
                await _signInManager.RefreshSignInAsync(user);
                await _audit.LogAsync("Profile", "Email.Changed", null, true);
                TempData["Success"] = "Your email address has been changed.";
                return RedirectToAction(nameof(Index));
            }
        }
        catch (FormatException)
        {
            // fall through to the error message
        }

        TempData["Error"] = "The email confirmation link is invalid or has expired.";
        return RedirectToAction(nameof(Index));
    }

    // =====================================================================
    // Password
    // =====================================================================

    /// <summary>Shows the form / changes the password after verifying the current one.</summary>
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    /// <summary>Shows the form / changes the password after verifying the current one.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword!, model.NewPassword!);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }

        // Changing the password rotates the security stamp; re-issue the cookie so this session stays valid.
        await _signInManager.RefreshSignInAsync(user);
        await _audit.LogAsync("Auth", "Password.Changed", null, true);
        TempData["Success"] = "Your password has been changed.";
        return RedirectToAction(nameof(Index));
    }

    // =====================================================================
    // Two-factor authentication (authenticator app)
    // =====================================================================

    /// <summary>Shows two-factor authentication status and recovery codes left.</summary>
    [HttpGet]
    public async Task<IActionResult> TwoFactor()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        return View(new TwoFactorViewModel
        {
            IsEnabled = await _userManager.GetTwoFactorEnabledAsync(user),
            RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user),
            IsAdministrator = User.IsInRole(RoleNames.Administrator)
        });
    }

    /// <summary>Shows the QR code / verifies a code and turns on two-factor authentication.</summary>
    [HttpGet]
    public async Task<IActionResult> EnableAuthenticator()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        return View(await BuildAuthenticatorModelAsync(user));
    }

    /// <summary>Shows the QR code / verifies a code and turns on two-factor authentication.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableAuthenticator(EnableAuthenticatorViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        if (!ModelState.IsValid)
            return View(await BuildAuthenticatorModelAsync(user));

        var code = model.Code!.Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await _userManager.VerifyTwoFactorTokenAsync(user, _userManager.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            ModelState.AddModelError(nameof(model.Code), "That code is not valid. Check the time on your phone and try again.");
            return View(await BuildAuthenticatorModelAsync(user));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        await _audit.LogAsync("Auth", "2FA.Enabled", null, true);

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join(",", recoveryCodes ?? Array.Empty<string>());
        return RedirectToAction(nameof(RecoveryCodes));
    }

    /// <summary>Displays newly generated recovery codes once.</summary>
    [HttpGet]
    public IActionResult RecoveryCodes()
    {
        var codes = TempData["RecoveryCodes"] as string;
        if (string.IsNullOrEmpty(codes))
            return RedirectToAction(nameof(TwoFactor));
        return View(codes.Split(','));
    }

    /// <summary>Replaces the recovery codes with a new set.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateRecoveryCodes()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!await _userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(TwoFactor));

        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["RecoveryCodes"] = string.Join(",", codes ?? Array.Empty<string>());
        await _audit.LogAsync("Auth", "2FA.RecoveryCodesRegenerated", null, true);
        return RedirectToAction(nameof(RecoveryCodes));
    }

    /// <summary>Turns off two-factor authentication and resets the authenticator key.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableTwoFactor()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);
        await _signInManager.RefreshSignInAsync(user);
        await _audit.LogAsync("Auth", "2FA.Disabled", null, true);
        TempData["Warning"] = "Two-factor authentication has been turned off.";
        return RedirectToAction(nameof(TwoFactor));
    }

    // =====================================================================
    // Privacy (GDPR): data export and account deletion
    // =====================================================================

    /// <summary>GDPR page: data download and account deletion.</summary>
    [HttpGet]
    public IActionResult Privacy() => View(new DeleteAccountViewModel());

    /// <summary>Downloads all of the user's data as a JSON file.</summary>
    [HttpGet]
    public async Task<IActionResult> DownloadData()
    {
        var userId = User.GetUserId();
        var data = await _userData.ExportUserDataAsync(userId);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
        await _audit.LogAsync("Privacy", "Data.Exported");
        return File(json, "application/json", $"healthtrack-my-data-{DateTime.Today:yyyyMMdd}.json");
    }

    /// <summary>Permanently deletes the account and all data after password confirmation.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccount(DeleteAccountViewModel model)
    {
        if (!ModelState.IsValid) return View("Privacy", model);

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        if (User.IsInRole(RoleNames.Administrator) &&
            (await _userManager.GetUsersInRoleAsync(RoleNames.Administrator)).Count <= 1)
        {
            ModelState.AddModelError(string.Empty, "You are the only administrator. Create another administrator before deleting this account.");
            return View("Privacy", model);
        }

        if (!await _userManager.CheckPasswordAsync(user, model.Password!))
        {
            ModelState.AddModelError(nameof(model.Password), "The password is incorrect.");
            return View("Privacy", model);
        }

        await _audit.LogAsync("Privacy", "Account.SelfDeleted", null, true);
        var result = await _userData.DeleteUserCompletelyAsync(user.Id);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View("Privacy", model);
        }

        await _signInManager.SignOutAsync();
        TempData["Success"] = "Your account and all associated data have been permanently deleted.";
        return RedirectToAction("Index", "Home");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<string> GetRoleAsync(IdentityUser user) =>
        (await _userManager.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;

    private async Task<ProfileViewModel> BuildProfileAsync(IdentityUser user)
    {
        var role = await GetRoleAsync(user);
        var model = new ProfileViewModel
        {
            Role = role,
            UserName = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber,
            FullName = user.UserName,
            TwoFactorEnabled = user.TwoFactorEnabled
        };

        if (role == RoleNames.Client)
        {
            var p = await _db.ClientProfiles.FindAsync(user.Id);
            if (p != null)
            {
                model.FullName = p.FullName;
                model.Age = p.Age;
                model.Gender = p.Gender;
                model.HeightCm = p.HeightCm;
                model.CurrentWeightKg = p.WeightKg;
                model.TargetWeightKg = p.TargetWeightKg;
                model.DailyCalorieTarget = p.DailyCalorieTarget;
                model.FitnessGoals = p.FitnessGoals;
                model.MedicalNotes = p.MedicalNotes;
                model.ProfilePicturePath = p.ProfilePicturePath;
                model.TrainerName = p.TrainerId != null ? await _directory.GetDisplayNameAsync(p.TrainerId) : null;
            }
        }
        else if (role == RoleNames.Trainer)
        {
            var p = await _db.TrainerProfiles.FindAsync(user.Id);
            if (p != null)
            {
                model.FullName = p.FullName;
                model.Qualifications = p.Qualifications;
                model.Specializations = p.Specializations;
                model.Certifications = p.Certifications;
                model.Bio = p.Bio;
                model.ProfilePicturePath = p.ProfilePicturePath;
            }
        }
        return model;
    }

    private void ValidateRoleFields(ProfileViewModel model, string role)
    {
        if (role == RoleNames.Client)
        {
            if (!model.Age.HasValue) ModelState.AddModelError(nameof(model.Age), "Enter your age.");
            if (string.IsNullOrWhiteSpace(model.Gender)) ModelState.AddModelError(nameof(model.Gender), "Select a gender option.");
            if (!model.HeightCm.HasValue) ModelState.AddModelError(nameof(model.HeightCm), "Enter your height.");
        }
        else if (role == RoleNames.Trainer)
        {
            if (string.IsNullOrWhiteSpace(model.Qualifications)) ModelState.AddModelError(nameof(model.Qualifications), "Enter your qualifications.");
            if (string.IsNullOrWhiteSpace(model.Specializations)) ModelState.AddModelError(nameof(model.Specializations), "Enter your specializations.");
        }
        else
        {
            // Administrators have no extended profile; their name is their username.
            ModelState.Remove(nameof(model.FullName));
        }
    }

    private static void CopyEditableFields(ProfileViewModel from, ProfileViewModel to)
    {
        to.FullName = from.FullName;
        to.PhoneNumber = from.PhoneNumber;
        to.Age = from.Age;
        to.Gender = from.Gender;
        to.HeightCm = from.HeightCm;
        to.TargetWeightKg = from.TargetWeightKg;
        to.DailyCalorieTarget = from.DailyCalorieTarget;
        to.FitnessGoals = from.FitnessGoals;
        to.MedicalNotes = from.MedicalNotes;
        to.Qualifications = from.Qualifications;
        to.Specializations = from.Specializations;
        to.Certifications = from.Certifications;
        to.Bio = from.Bio;
    }

    private string? UpdatePicture(string? current, string? uploaded, bool remove)
    {
        if (uploaded != null)
        {
            _files.DeleteFile(current);
            return uploaded;
        }
        if (remove)
        {
            _files.DeleteFile(current);
            return null;
        }
        return current;
    }

    private async Task<EnableAuthenticatorViewModel> BuildAuthenticatorModelAsync(IdentityUser user)
    {
        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        // Format the key in groups of four for easier manual entry.
        var formatted = new StringBuilder();
        for (var i = 0; i < key!.Length; i += 4)
            formatted.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');

        var uri = string.Format("otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            _urlEncoder.Encode("HealthTrack"), _urlEncoder.Encode(user.Email ?? user.UserName ?? "user"), key);

        return new EnableAuthenticatorViewModel { SharedKey = formatted.ToString().Trim().ToLowerInvariant(), AuthenticatorUri = uri };
    }
}
