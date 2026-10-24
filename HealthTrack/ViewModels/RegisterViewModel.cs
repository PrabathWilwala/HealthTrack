using System.ComponentModel.DataAnnotations;

namespace HealthTrack.ViewModels
{
    public class RegisterViewModel : IValidatableObject
    {
        [Required(ErrorMessage = "Please choose an account type.")]
        public string? AccountType { get; set; }

        [Required, StringLength(100, MinimumLength = 2)]
        [Display(Name = "Full name")]
        public string? FullName { get; set; }

        [Required, StringLength(50, MinimumLength = 3)]
        [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Use letters, numbers, dots, underscores or hyphens only.")]
        [Display(Name = "Username")]
        public string? UserName { get; set; }

        [Required, EmailAddress, StringLength(256)]
        public string? Email { get; set; }

        [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
        public string? Password { get; set; }

        [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm password")]
        public string? ConfirmPassword { get; set; }

        [Display(Name = "I agree to the privacy policy")]
        [Range(typeof(bool), "true", "true", ErrorMessage = "You must accept the privacy policy to create an account.")]
        public bool AcceptPrivacyPolicy { get; set; }

        // Client-specific fields
        public int? Age { get; set; }
        [StringLength(30)]
        public string? Gender { get; set; }
        public decimal? HeightCm { get; set; }
        public decimal? WeightKg { get; set; }
        [StringLength(1000)]
        public string? FitnessGoals { get; set; }

        // Trainer-specific fields
        [StringLength(500)]
        public string? Qualifications { get; set; }
        [StringLength(500)]
        public string? Specializations { get; set; }
        [StringLength(1000)]
        public string? Certifications { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            var role = (AccountType ?? string.Empty).Trim();
            if (!string.Equals(role, "Client", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "Trainer", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ValidationResult("Select Client or Trainer. Administrator accounts cannot be self-registered.", new[] { nameof(AccountType) });
                yield break;
            }

            if (string.Equals(role, "Client", StringComparison.OrdinalIgnoreCase))
            {
                if (!Age.HasValue || Age.Value < 1 || Age.Value > 120)
                    yield return new ValidationResult("Enter an age between 1 and 120.", new[] { nameof(Age) });

                var allowedGenders = new[] { "Female", "Male", "Non-binary", "Other", "Prefer not to say" };
                if (string.IsNullOrWhiteSpace(Gender) ||
                    !allowedGenders.Contains(Gender, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new ValidationResult("Select a valid gender option.", new[] { nameof(Gender) });
                }

                if (!HeightCm.HasValue || HeightCm.Value < 50 || HeightCm.Value > 300)
                    yield return new ValidationResult("Enter a height between 50 and 300 cm.", new[] { nameof(HeightCm) });

                if (!WeightKg.HasValue || WeightKg.Value < 10 || WeightKg.Value > 500)
                    yield return new ValidationResult("Enter a weight between 10 and 500 kg.", new[] { nameof(WeightKg) });

                if (string.IsNullOrWhiteSpace(FitnessGoals))
                    yield return new ValidationResult("Describe your fitness goals.", new[] { nameof(FitnessGoals) });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Qualifications))
                    yield return new ValidationResult("Enter your professional qualifications.", new[] { nameof(Qualifications) });

                if (string.IsNullOrWhiteSpace(Specializations))
                    yield return new ValidationResult("Enter your specializations.", new[] { nameof(Specializations) });
            }
        }
    }
}
