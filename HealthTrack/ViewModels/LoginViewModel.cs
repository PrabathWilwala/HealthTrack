using System.ComponentModel.DataAnnotations;

namespace HealthTrack.ViewModels
{
    public class LoginViewModel
    {
        [Required]
        [Display(Name = "Username or email")]
        public string? UserNameOrEmail { get; set; }

        [Required, DataType(DataType.Password)]
        public string? Password { get; set; }

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }

        public string? ReturnUrl { get; set; }
    }
}
