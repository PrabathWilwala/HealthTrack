using System.ComponentModel.DataAnnotations;
using HealthTrack.Models;

namespace HealthTrack.ViewModels;

// Editable profile for every role. Role-specific sections are shown conditionally.
public class ProfileViewModel
{
    public string Role { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? ProfilePicturePath { get; set; }
    public bool TwoFactorEnabled { get; set; }

    [Required, StringLength(100, MinimumLength = 2)]
    [Display(Name = "Full name")]
    public string? FullName { get; set; }

    [Phone, StringLength(30)]
    [Display(Name = "Phone number")]
    public string? PhoneNumber { get; set; }

    [Display(Name = "Profile picture")]
    public IFormFile? ProfilePicture { get; set; }

    public bool RemovePicture { get; set; }

    // ----- Client -----
    [Range(1, 120)]
    public int? Age { get; set; }

    [StringLength(30)]
    public string? Gender { get; set; }

    [Range(typeof(decimal), "50", "300")]
    [Display(Name = "Height (cm)")]
    public decimal? HeightCm { get; set; }

    [Range(typeof(decimal), "10", "500")]
    [Display(Name = "Target weight (kg)")]
    public decimal? TargetWeightKg { get; set; }

    [Range(800, 6000)]
    [Display(Name = "Personal daily calorie target")]
    public int? DailyCalorieTarget { get; set; }

    [StringLength(1000)]
    [Display(Name = "Fitness goals")]
    public string? FitnessGoals { get; set; }

    [StringLength(2000)]
    [Display(Name = "Medical notes (private, encrypted)")]
    public string? MedicalNotes { get; set; }

    public string? TrainerName { get; set; }
    public decimal CurrentWeightKg { get; set; }

    // ----- Trainer -----
    [StringLength(500)]
    public string? Qualifications { get; set; }

    [StringLength(500)]
    public string? Specializations { get; set; }

    [StringLength(1000)]
    public string? Certifications { get; set; }

    [StringLength(1000)]
    [Display(Name = "About me")]
    public string? Bio { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string? CurrentPassword { get; set; }

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    [Display(Name = "New password")]
    public string? NewPassword { get; set; }

    [Required, DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm new password")]
    public string? ConfirmPassword { get; set; }
}

public class ChangeEmailViewModel
{
    [Required, EmailAddress, StringLength(256)]
    [Display(Name = "New email address")]
    public string? NewEmail { get; set; }
}

public class TwoFactorViewModel
{
    public bool IsEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
    public bool IsAdministrator { get; set; }
}

public class EnableAuthenticatorViewModel
{
    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;

    [Required, StringLength(7, MinimumLength = 6)]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Enter the 6-digit code.")]
    [Display(Name = "Verification code")]
    public string? Code { get; set; }
}

public class DeleteAccountViewModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Confirm with your password")]
    public string? Password { get; set; }

    [Range(typeof(bool), "true", "true", ErrorMessage = "Please confirm that you understand this cannot be undone.")]
    public bool ConfirmDeletion { get; set; }
}

public class TrainerAvailabilityViewModel
{
    public List<TrainerAvailability> Slots { get; set; } = new();

    [Display(Name = "Day")]
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    [Required, DataType(DataType.Time)]
    [Display(Name = "From")]
    public TimeSpan? StartTime { get; set; } = new TimeSpan(9, 0, 0);

    [Required, DataType(DataType.Time)]
    [Display(Name = "To")]
    public TimeSpan? EndTime { get; set; } = new TimeSpan(17, 0, 0);
}
