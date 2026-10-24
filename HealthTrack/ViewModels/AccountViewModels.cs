using System.ComponentModel.DataAnnotations;

namespace HealthTrack.ViewModels;

public class ForgotPasswordViewModel
{
    [Required, EmailAddress, StringLength(256)]
    [Display(Name = "Email address")]
    public string? Email { get; set; }
}

public class ResendConfirmationViewModel
{
    [Required, EmailAddress, StringLength(256)]
    [Display(Name = "Email address")]
    public string? Email { get; set; }
}

/// <summary>Shown after a request that sends an email (reset or confirmation).</summary>
public class EmailSentViewModel
{
    public string Heading { get; set; } = "Check your email";
    public string Message { get; set; } = string.Empty;
    /// <summary>In Development the link is shown on screen because no real email is sent.</summary>
    public string? DevelopmentLink { get; set; }
}

public class ResetPasswordViewModel
{
    [Required]
    public string? UserId { get; set; }

    [Required]
    public string? Code { get; set; }

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    [Display(Name = "New password")]
    public string? Password { get; set; }

    [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm new password")]
    public string? ConfirmPassword { get; set; }
}

public class LoginWith2faViewModel
{
    [Required, StringLength(7, MinimumLength = 6)]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    [Display(Name = "Authenticator code")]
    public string? TwoFactorCode { get; set; }

    [Display(Name = "Remember this device")]
    public bool RememberMachine { get; set; }

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class LoginWithRecoveryCodeViewModel
{
    [Required, StringLength(20)]
    [Display(Name = "Recovery code")]
    public string? RecoveryCode { get; set; }

    public string? ReturnUrl { get; set; }
}
