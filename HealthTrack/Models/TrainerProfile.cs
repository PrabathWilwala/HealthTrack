using System.ComponentModel.DataAnnotations;

namespace HealthTrack.Models
{
    /// <summary>
    /// Professional profile for a user in the Trainer role. The primary key is the Identity user id.
    /// </summary>
    public class TrainerProfile
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string FullName { get; set; } = string.Empty;

        [Required, StringLength(500)]
        public string Qualifications { get; set; } = string.Empty;

        [Required, StringLength(500)]
        public string Specializations { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Certifications { get; set; }

        [StringLength(1000)]
        public string? Bio { get; set; }

        [StringLength(260)]
        public string? ProfilePicturePath { get; set; }

        // New trainer registrations remain pending until an administrator approves them.
        public bool IsApproved { get; set; } = false;

        public bool IsRejected { get; set; }

        [StringLength(500)]
        public string? RejectionReason { get; set; }

        public DateTime? ApprovedAtUtc { get; set; }

        public DateTime? ConsentAcceptedAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<TrainerAvailability> Availability { get; set; } = new List<TrainerAvailability>();
    }

    /// <summary>A weekly time window in which a trainer accepts consultations.</summary>
    public class TrainerAvailability
    {
        public int Id { get; set; }

        [Required, StringLength(450)]
        public string TrainerId { get; set; } = string.Empty;

        public DayOfWeek DayOfWeek { get; set; }

        public TimeSpan StartTime { get; set; }

        public TimeSpan EndTime { get; set; }
    }
}
