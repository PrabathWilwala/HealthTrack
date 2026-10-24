using System.ComponentModel.DataAnnotations;
using HealthTrack.Infrastructure;

namespace HealthTrack.Models;

/// <summary>
/// A consultation between a client and a trainer/healthcare professional.
/// Times are stored in the server's local time zone.
/// </summary>
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

    /// <summary>A new time proposed by either party while a reschedule is pending.</summary>
    public DateTime? ProposedStartTime { get; set; }

    /// <summary>"Client" or "Trainer" — who proposed the pending reschedule.</summary>
    [StringLength(20)]
    public string? RescheduleRequestedBy { get; set; }

    /// <summary>Reason given when the appointment was declined or cancelled.</summary>
    [StringLength(500)]
    public string? StatusReason { get; set; }

    /// <summary>Alternative times suggested by a trainer when declining.</summary>
    [StringLength(200)]
    public string? SuggestedAlternative { get; set; }

    /// <summary>"Client", "Trainer" or "Administrator".</summary>
    [StringLength(20)]
    public string? CancelledBy { get; set; }

    /// <summary>Post-consultation notes written by the trainer. Encrypted at rest.</summary>
    [Encrypted]
    public string? ConsultationNotes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime EndTime => StartTime.AddMinutes(DurationMinutes);

    /// <summary>True if this appointment's time range overlaps the given range.</summary>
    public bool Overlaps(DateTime start, DateTime end) => StartTime < end && start < EndTime;
}
