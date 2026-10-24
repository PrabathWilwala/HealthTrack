using System.ComponentModel.DataAnnotations;
using HealthTrack.Models;
using HealthTrack.Services;

namespace HealthTrack.ViewModels;

public class AppointmentRequestViewModel
{
    [Required(ErrorMessage = "Please choose a trainer.")]
    [Display(Name = "Trainer / health professional")]
    public string? TrainerId { get; set; }

    [Required]
    [Display(Name = "Date and time")]
    public DateTime? StartTime { get; set; }

    [Range(15, 180)]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 30;

    [Required, StringLength(300, MinimumLength = 5)]
    [Display(Name = "Purpose of the consultation")]
    public string? Purpose { get; set; }

    public List<TrainerOption> Trainers { get; set; } = new();
    public string? AssignedTrainerId { get; set; }

    // Human-readable weekly availability per trainer id.
    public Dictionary<string, List<string>> Availability { get; set; } = new();
}

public class AppointmentIndexViewModel
{
    public string Role { get; set; } = string.Empty;
    public string Tab { get; set; } = "upcoming";
    public AppointmentStatus? StatusFilter { get; set; }
    public List<AppointmentListItem> Items { get; set; } = new();
    public int PendingCount { get; set; }
    public int RescheduleRequests { get; set; }
}

public class AppointmentDetailsViewModel
{
    public Appointment Appointment { get; set; } = new();
    public string ClientName { get; set; } = string.Empty;
    public string TrainerName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public bool CanApproveOrDecline { get; set; }
    public bool CanCancel { get; set; }
    public bool CanProposeReschedule { get; set; }
    public bool CanRespondToReschedule { get; set; }
    public bool CanComplete { get; set; }
    public bool CanExport { get; set; }
}

public class RescheduleViewModel
{
    public int Id { get; set; }

    [Required]
    [Display(Name = "New date and time")]
    public DateTime? NewStart { get; set; }

    public DateTime CurrentStart { get; set; }
    public string OtherPartyName { get; set; } = string.Empty;
    public List<string> Availability { get; set; } = new();
}

public class CompleteAppointmentViewModel
{
    public int Id { get; set; }

    [StringLength(4000)]
    [Display(Name = "Consultation notes (visible to the client)")]
    public string? Notes { get; set; }

    public string ClientName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public string Purpose { get; set; } = string.Empty;
}

public class CalendarDay
{
    public DateTime Date { get; set; }
    public bool InCurrentMonth { get; set; }
    public List<AppointmentListItem> Items { get; set; } = new();
}

public class CalendarViewModel
{
    // "day", "week" or "month".
    public string View { get; set; } = "month";
    public DateTime Date { get; set; } = DateTime.Today;
    public string Title { get; set; } = string.Empty;
    public DateTime Previous { get; set; }
    public DateTime Next { get; set; }
    public List<CalendarDay> Days { get; set; } = new();
}
