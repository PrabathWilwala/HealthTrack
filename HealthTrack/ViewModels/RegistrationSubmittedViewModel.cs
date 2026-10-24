namespace HealthTrack.ViewModels
{
    public class RegistrationSubmittedViewModel
    {
        public string Email { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public bool RequiresTrainerApproval { get; set; }
        public string? DevelopmentConfirmationUrl { get; set; }
    }
}
