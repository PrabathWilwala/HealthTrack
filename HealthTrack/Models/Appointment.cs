using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;

namespace HealthTrack.Models;

// A consultation between a client and a trainer/healthcare professional.
// Times are stored in the server's local time zone.
public class Appointment
{
    public int Id { get; set; }

    [Required, StringLength(450)]
    public string ClientId { get; set; } = string.Empty;

    [Required, StringLength(450)]
    public string TrainerId { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }

    [Range(15, 180)]
    public int DurationMinutes { get; set; } = 30;

    [Required, StringLength(300)]
    public string Purpose { get; set; } = string.Empty;

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Pending;

    // A new time proposed by either party while a reschedule is pending.
    public DateTime? ProposedStartTime { get; set; }

    // "Client" or "Trainer" — who proposed the pending reschedule.
    [StringLength(20)]
    public string? RescheduleRequestedBy { get; set; }

    // Reason given when the appointment was declined or cancelled.
    [StringLength(500)]
    public string? StatusReason { get; set; }

    // Alternative times suggested by a trainer when declining.
    [StringLength(200)]
    public string? SuggestedAlternative { get; set; }

    // "Client", "Trainer" or "Administrator".
    [StringLength(20)]
    public string? CancelledBy { get; set; }

    // Post-consultation notes written by the trainer. Encrypted at rest.
    [Encrypted]
    public string? ConsultationNotes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime EndTime => StartTime.AddMinutes(DurationMinutes);

    // True if this appointment's time range overlaps the given range.
    public bool Overlaps(DateTime start, DateTime end) => StartTime < end && start < EndTime;
}
