using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;

namespace HealthTrack.Models
{
    /// <summary>
    /// Health profile for a user in the Client role. The primary key is the Identity user id.
    /// </summary>
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

        /// <summary>Most recent known weight. Updated whenever a measurement is logged.</summary>
        [Range(typeof(decimal), "10", "500")]
        public decimal WeightKg { get; set; }

        [StringLength(1000)]
        public string? FitnessGoals { get; set; }

        /// <summary>Optional goal weight used for goal-progress tracking.</summary>
        [Range(typeof(decimal), "10", "500")]
        public decimal? TargetWeightKg { get; set; }

        /// <summary>Optional personal calorie target used when no diet plan is assigned.</summary>
        [Range(800, 6000)]
        public int? DailyCalorieTarget { get; set; }

        /// <summary>Sensitive free-text health information. Encrypted at rest.</summary>
        [Encrypted]
        public string? MedicalNotes { get; set; }

        [StringLength(260)]
        public string? ProfilePicturePath { get; set; }

        /// <summary>The trainer assigned to this client by an administrator.</summary>
        [StringLength(450)]
        public string? TrainerId { get; set; }

        /// <summary>When the user accepted the privacy policy (GDPR consent).</summary>
        public DateTime? ConsentAcceptedAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
