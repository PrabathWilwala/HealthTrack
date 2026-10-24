using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;

namespace HealthTrack.Models
{
    // Health profile for a user in the Client role. The primary key is the Identity user id.
    public class ClientProfile
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string FullName { get; set; } = string.Empty;

        [Range(1, 120)]
        public int Age { get; set; }

        [Required, StringLength(30)]
        public string Gender { get; set; } = string.Empty;

        [Range(typeof(decimal), "50", "300")]
        public decimal HeightCm { get; set; }

        // Most recent known weight. Updated whenever a measurement is logged.
        [Range(typeof(decimal), "10", "500")]
        public decimal WeightKg { get; set; }

        [StringLength(1000)]
        public string? FitnessGoals { get; set; }

        // Optional goal weight used for goal-progress tracking.
        [Range(typeof(decimal), "10", "500")]
        public decimal? TargetWeightKg { get; set; }

        // Optional personal calorie target used when no diet plan is assigned.
        [Range(800, 6000)]
        public int? DailyCalorieTarget { get; set; }

        // Sensitive free-text health information. Encrypted at rest.
        [Encrypted]
        public string? MedicalNotes { get; set; }

        [StringLength(260)]
        public string? ProfilePicturePath { get; set; }

        // The trainer assigned to this client by an administrator.
        [StringLength(450)]
        public string? TrainerId { get; set; }

        // When the user accepted the privacy policy (GDPR consent).
        public DateTime? ConsentAcceptedAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
