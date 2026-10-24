using System.Net;
using System.Text;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers
{
    /// <summary>
    /// Registration, email verification, sign-in (with optional two-factor authentication),
    /// password reset and sign-out for all three roles.
    /// </summary>
    public class AccountController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly ApplicationDbContext _dbContext;
        private readonly IWebHostEnvironment _environment;
        private readonly IAppEmailSender _emailSender;
        private readonly IAuditService _audit;
        private readonly INotificationService _notifications;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
            ApplicationDbContext dbContext,
            IWebHostEnvironment environment,
            IAppEmailSender emailSender,
            IAuditService audit,
            INotificationService notifications,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _dbContext = dbContext;
            _environment = environment;
            _emailSender = emailSender;
            _audit = audit;
            _notifications = notifications;
            _logger = logger;
        }

        // =====================================================================
        // Registration and email verification
        // =====================================================================

        /// <summary>Shows the registration form / creates a client or trainer account, its profile and sends the email confirmation link.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Register()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction(nameof(Dashboard));
            return View(new RegisterViewModel());
        }

        /// <summary>Shows the registration form / creates a client or trainer account, its profile and sends the email confirmation link.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var accountType = string.Equals(model.AccountType, RoleNames.Trainer, StringComparison.OrdinalIgnoreCase)
                ? RoleNames.Trainer
                : RoleNames.Client;

            var user = new IdentityUser
            {
                UserName = model.UserName!.Trim(),
                Email = model.Email!.Trim(),
                EmailConfirmed = false
            };

            var createResult = await _userManager.CreateAsync(user, model.Password!);
            if (!createResult.Succeeded)
            {
                AddIdentityErrors(createResult);
                return View(model);
            }

            var roleResult = await _userManager.AddToRoleAsync(user, accountType);
            if (!roleResult.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                AddIdentityErrors(roleResult);
                ModelState.AddModelError(string.Empty, "The account role could not be assigned. Please try again.");
                return View(model);
            }

            try
            {
                if (accountType == RoleNames.Client)
                {
                    _dbContext.ClientProfiles.Add(new ClientProfile
                    {
                        UserId = user.Id,
                        FullName = model.FullName!.Trim(),
                        Age = model.Age!.Value,
                        Gender = model.Gender!.Trim(),
                        HeightCm = model.HeightCm!.Value,
                        WeightKg = model.WeightKg!.Value,
                        FitnessGoals = model.FitnessGoals!.Trim(),
                        ConsentAcceptedAtUtc = DateTime.UtcNow
                    });

                    // The registration weight becomes the first measurement so progress charts start immediately.
                    _dbContext.HealthMeasurements.Add(new HealthMeasurement
                    {
                        ClientId = user.Id,
                        MeasuredOn = DateTime.Today,
                        WeightKg = model.WeightKg!.Value,
                        Bmi = BmiCalculator.Calculate(model.WeightKg!.Value, model.HeightCm!.Value)
                    });
                }
                else
                {
                    _dbContext.TrainerProfiles.Add(new TrainerProfile
                    {
                        UserId = user.Id,
                        FullName = model.FullName!.Trim(),
                        Qualifications = model.Qualifications!.Trim(),
                        Specializations = model.Specializations!.Trim(),
                        Certifications = string.IsNullOrWhiteSpace(model.Certifications)
                            ? null
                            : model.Certifications.Trim(),
                        IsApproved = false,
                        ConsentAcceptedAtUtc = DateTime.UtcNow
                    });
                }

                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Failed to save a profile for the newly registered account {UserId}.", user.Id);
                await _userManager.DeleteAsync(user);
                ModelState.AddModelError(string.Empty, "We could not save your profile. Please check your details and try again.");
                return View(model);
            }

            await _audit.LogAsync("Auth", "Register", $"New {accountType} account registered", userId: user.Id, userName: user.UserName);

            if (accountType == RoleNames.Trainer)
            {
                await _notifications.NotifyRoleAsync(RoleNames.Administrator, NotificationType.ApprovalPending,
                    NotificationTemplateKeys.TrainerApprovalPending,
                    new Dictionary<string, string> { ["TrainerName"] = model.FullName!.Trim() },
                    "/Admin/Users?status=pending");
            }

            var confirmationUrl = await SendConfirmationEmailAsync(user);

            return View("RegistrationSubmitted", new RegistrationSubmittedViewModel
            {
                Email = user.Email ?? string.Empty,
                AccountType = accountType,
                RequiresTrainerApproval = accountType == RoleNames.Trainer,
                DevelopmentConfirmationUrl = _environment.IsDevelopment() ? confirmationUrl : null
            });
        }

        /// <summary>Confirms the email address using the token from the confirmation link.</summary>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> ConfirmEmail(string? userId, string? code)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(code))
                return View("EmailConfirmationFailed");

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
                return View("EmailConfirmationFailed");

            try
            {
                var decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
                var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
                if (result.Succeeded)
                {
                    await _audit.LogAsync("Auth", "Email.Confirmed", null, userId: user.Id, userName: user.UserName);
                    return View("EmailConfirmed");
                }
            }
            catch (FormatException ex)
            {
                _logger.LogInformation(ex, "An invalid email confirmation token was supplied.");
            }
            catch (ArgumentException ex)
            {
                _logger.LogInformation(ex, "An invalid email confirmation token was supplied.");
            }

            return View("EmailConfirmationFailed");
        }

        /// <summary>Sends a new email confirmation link.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ResendConfirmation() => View(new ResendConfirmationViewModel());

        /// <summary>Sends a new email confirmation link.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> ResendConfirmation(ResendConfirmationViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            string? link = null;
            var user = await _userManager.FindByEmailAsync(model.Email!.Trim());
            if (user != null && !user.EmailConfirmed)
                link = await SendConfirmationEmailAsync(user);

            // Same response whether or not the account exists (prevents account enumeration).
            return View("EmailSent", new EmailSentViewModel
            {
                Heading = "Confirmation email sent",
                Message = "If an unconfirmed account exists for that address, a new confirmation link has been sent.",
                DevelopmentLink = _environment.IsDevelopment() ? link : null
            });
        }

        // =====================================================================
        // Sign-in
        // =====================================================================

        /// <summary>Shows the sign-in page / verifies credentials, lockout, email confirmation, trainer approval and two-factor requirements.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction(nameof(Dashboard));
            return View(new LoginViewModel { ReturnUrl = returnUrl });
        }

        /// <summary>Shows the sign-in page / verifies credentials, lockout, email confirmation, trainer approval and two-factor requirements.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var identifier = model.UserNameOrEmail!.Trim();
            var user = await _userManager.FindByNameAsync(identifier) ?? await _userManager.FindByEmailAsync(identifier);

            if (user == null)
            {
                await _audit.LogAsync("Auth", "Login.Failed", $"Unknown user '{identifier}'", isSecurityEvent: true, userName: identifier);
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }

            var passwordResult = await _signInManager.CheckPasswordSignInAsync(user, model.Password!, lockoutOnFailure: true);

            if (passwordResult.IsLockedOut)
            {
                var deactivated = user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow.AddYears(10);
                if (deactivated)
                {
                    ModelState.AddModelError(string.Empty, "This account has been deactivated. Please contact an administrator.");
                }
                else
                {
                    ModelState.AddModelError(string.Empty, "This account is temporarily locked due to unsuccessful login attempts. Please try again later.");
                    await _audit.LogAsync("Auth", "Login.LockedOut", "Account locked after repeated failures", true, user.Id, user.UserName);
                    await _notifications.NotifyRoleAsync(RoleNames.Administrator, NotificationType.SecurityAlert,
                        NotificationTemplateKeys.SecurityAlert,
                        new Dictionary<string, string>
                        {
                            ["Subject"] = "account locked",
                            ["Details"] = $"The account '{user.UserName}' was locked after repeated failed sign-in attempts from {HttpContext.Connection.RemoteIpAddress}."
                        },
                        "/Admin/AuditLog?securityOnly=true",
                        $"lockout:{user.Id}:{DateTime.UtcNow:yyyyMMddHH}");
                }
                return View(model);
            }

            if (passwordResult.IsNotAllowed)
            {
                if (!user.EmailConfirmed)
                {
                    ModelState.AddModelError(string.Empty, "Please confirm your email address before signing in.");
                    ViewData["ShowResendLink"] = true;
                }
                else
                {
                    ModelState.AddModelError(string.Empty, "This account is not currently allowed to sign in.");
                }
                return View(model);
            }

            if (!passwordResult.Succeeded)
            {
                await _audit.LogAsync("Auth", "Login.Failed", "Wrong password", true, user.Id, user.UserName);
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }

            if (await _userManager.IsInRoleAsync(user, RoleNames.Trainer))
            {
                var trainerProfile = await _dbContext.TrainerProfiles.FindAsync(user.Id);
                if (trainerProfile != null && trainerProfile.IsRejected)
                {
                    ModelState.AddModelError(string.Empty, "Your trainer application was not approved." +
                        (string.IsNullOrWhiteSpace(trainerProfile.RejectionReason) ? string.Empty : " Reason: " + trainerProfile.RejectionReason));
                    return View(model);
                }
                if (trainerProfile == null || !trainerProfile.IsApproved)
                {
                    ModelState.AddModelError(string.Empty, "Your email is confirmed, but your trainer account is awaiting administrator approval.");
                    return View(model);
                }
            }

            // Two-factor authentication: let Identity issue the intermediate 2FA cookie.
            if (await _userManager.GetTwoFactorEnabledAsync(user))
            {
                var twoFactorResult = await _signInManager.PasswordSignInAsync(user, model.Password!, model.RememberMe, lockoutOnFailure: true);
                if (twoFactorResult.RequiresTwoFactor)
                    return RedirectToAction(nameof(LoginWith2fa), new { returnUrl = model.ReturnUrl, rememberMe = model.RememberMe });
            }

            await _signInManager.SignInAsync(user, model.RememberMe);
            await _audit.LogAsync("Auth", "Login.Success", null, false, user.Id, user.UserName);

            return RedirectAfterLogin(model.ReturnUrl);
        }

        /// <summary>Second sign-in step: verifies the authenticator app code.</summary>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> LoginWith2fa(bool rememberMe, string? returnUrl = null)
        {
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
                return RedirectToAction(nameof(Login));

            return View(new LoginWith2faViewModel { RememberMe = rememberMe, ReturnUrl = returnUrl });
        }

        /// <summary>Second sign-in step: verifies the authenticator app code.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> LoginWith2fa(LoginWith2faViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
                return RedirectToAction(nameof(Login));

            var code = model.TwoFactorCode!.Replace(" ", string.Empty).Replace("-", string.Empty);
            var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(code, model.RememberMe, model.RememberMachine);

            if (result.Succeeded)
            {
                await _audit.LogAsync("Auth", "Login.Success", "Signed in with two-factor authentication", false, user.Id, user.UserName);
                return RedirectAfterLogin(model.ReturnUrl);
            }
            if (result.IsLockedOut)
            {
                ModelState.AddModelError(string.Empty, "This account is temporarily locked. Please try again later.");
                return View(model);
            }

            await _audit.LogAsync("Auth", "Login.2faFailed", "Invalid authenticator code", true, user.Id, user.UserName);
            ModelState.AddModelError(string.Empty, "Invalid authenticator code.");
            return View(model);
        }

        /// <summary>Signs in with a one-time recovery code when the authenticator is unavailable.</summary>
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> LoginWithRecoveryCode(string? returnUrl = null)
        {
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
                return RedirectToAction(nameof(Login));
            return View(new LoginWithRecoveryCodeViewModel { ReturnUrl = returnUrl });
        }

        /// <summary>Signs in with a one-time recovery code when the authenticator is unavailable.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> LoginWithRecoveryCode(LoginWithRecoveryCodeViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
                return RedirectToAction(nameof(Login));

            var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(model.RecoveryCode!.Replace(" ", string.Empty));
            if (result.Succeeded)
            {
                await _audit.LogAsync("Auth", "Login.Success", "Signed in with a recovery code", true, user.Id, user.UserName);
                TempData["Warning"] = "You signed in with a recovery code. Consider generating new recovery codes.";
                return RedirectAfterLogin(model.ReturnUrl);
            }

            ModelState.AddModelError(string.Empty, "Invalid recovery code.");
            return View(model);
        }

        /// <summary>Sends each role to its own dashboard.</summary>
        [Authorize]
        [HttpGet]
        public IActionResult Dashboard()
        {
            if (User.IsInRole(RoleNames.Administrator))
                return RedirectToAction("Dashboard", "Admin");
            if (User.IsInRole(RoleNames.Trainer))
                return RedirectToAction("Dashboard", "Trainer");
            if (User.IsInRole(RoleNames.Client))
                return RedirectToAction("Dashboard", "Client");
            return RedirectToAction(nameof(AccessDenied));
        }

        /// <summary>Signs the user out (also used by the inactivity timer).</summary>
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout(bool timedOut = false)
        {
            await _audit.LogAsync("Auth", timedOut ? "Logout.Timeout" : "Logout");
            await _signInManager.SignOutAsync();
            if (timedOut)
                TempData["Info"] = "You were signed out after a period of inactivity.";
            return RedirectToAction(nameof(Login));
        }

        // =====================================================================
        // Password reset
        // =====================================================================

        /// <summary>Shows the form / emails a password reset link without revealing whether the account exists.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

        /// <summary>Shows the form / emails a password reset link without revealing whether the account exists.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            string? link = null;
            var user = await _userManager.FindByEmailAsync(model.Email!.Trim());
            if (user != null && await _userManager.IsEmailConfirmedAsync(user))
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                link = Url.Action(nameof(ResetPassword), "Account", new { userId = user.Id, code }, Request.Scheme);

                await _emailSender.SendAsync(user.Email!, "Reset your HealthTrack password",
                    $"<p>We received a request to reset your password.</p><p><a href='{WebUtility.HtmlEncode(link)}'>Reset password</a></p><p>This link expires in 3 hours. If you did not request this, you can ignore this email.</p>");
                await _audit.LogAsync("Auth", "Password.ResetRequested", null, true, user.Id, user.UserName);
            }

            return View("EmailSent", new EmailSentViewModel
            {
                Heading = "Check your email",
                Message = "If an account with a confirmed email exists for that address, we have sent a password reset link.",
                DevelopmentLink = _environment.IsDevelopment() ? link : null
            });
        }

        /// <summary>Shows the form / sets a new password using the reset token.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult ResetPassword(string? userId, string? code)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
                return View("EmailConfirmationFailed");
            return View(new ResetPasswordViewModel { UserId = userId, Code = code });
        }

        /// <summary>Shows the form / sets a new password using the reset token.</summary>
        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _userManager.FindByIdAsync(model.UserId!);
            if (user == null)
            {
                ModelState.AddModelError(string.Empty, "This reset link is invalid or has expired.");
                return View(model);
            }

            string token;
            try
            {
                token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Code!));
            }
            catch (FormatException)
            {
                ModelState.AddModelError(string.Empty, "This reset link is invalid or has expired.");
                return View(model);
            }

            var result = await _userManager.ResetPasswordAsync(user, token, model.Password!);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Code == "InvalidToken" ? "This reset link is invalid or has expired." : error.Description);
                return View(model);
            }

            await _userManager.ResetAccessFailedCountAsync(user);
            await _audit.LogAsync("Auth", "Password.Reset", null, true, user.Id, user.UserName);
            TempData["Success"] = "Your password has been reset. You can now sign in.";
            return RedirectToAction(nameof(Login));
        }

        /// <summary>Shown when a signed-in user opens a page their role cannot access.</summary>
        [AllowAnonymous]
        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Generates a confirmation link, emails it and returns it (for the Development screen).</summary>
        private async Task<string?> SendConfirmationEmailAsync(IdentityUser user)
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var confirmationUrl = Url.Action(nameof(ConfirmEmail), "Account",
                new { userId = user.Id, code = encodedToken }, Request.Scheme);

            if (confirmationUrl != null && !string.IsNullOrEmpty(user.Email))
            {
                await _emailSender.SendAsync(user.Email, "Confirm your HealthTrack account",
                    $"<p>Welcome to HealthTrack!</p><p>Please confirm your email address:</p><p><a href='{WebUtility.HtmlEncode(confirmationUrl)}'>Confirm my email</a></p>");
            }
            return confirmationUrl;
        }

        private IActionResult RedirectAfterLogin(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return LocalRedirect(returnUrl);
            return RedirectToAction(nameof(Dashboard));
        }

        private void AddIdentityErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
        }
    }
}
