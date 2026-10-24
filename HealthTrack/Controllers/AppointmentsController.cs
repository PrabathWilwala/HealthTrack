using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using HealthTrack.Services.Reporting;
using HealthTrack.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Controllers;

/// <summary>
/// Appointment screens for clients (request, reschedule, cancel, export) and trainers
/// (approve, decline, reschedule, cancel, complete with notes, calendar).
/// Business rules are implemented in <see cref="IAppointmentService"/>.
/// </summary>
[Authorize]
public class AppointmentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAppointmentService _appointments;
    private readonly IUserDirectory _directory;

    public AppointmentsController(ApplicationDbContext db, IAppointmentService appointments, IUserDirectory directory)
    {
        _db = db;
        _appointments = appointments;
        _directory = directory;
    }

    private string CurrentRole =>
        User.IsInRole(RoleNames.Administrator) ? RoleNames.Administrator :
        User.IsInRole(RoleNames.Trainer) ? RoleNames.Trainer : RoleNames.Client;

    // =====================================================================
    // Lists and details
    // =====================================================================

    /// <summary>Upcoming or past appointments for the current client or trainer, optionally filtered by status.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(string tab = "upcoming", AppointmentStatus? status = null)
    {
        var role = CurrentRole;
        if (role == RoleNames.Administrator)
            return RedirectToAction("Appointments", "Admin");

        var userId = User.GetUserId();
        var query = role == RoleNames.Client
            ? _db.Appointments.AsNoTracking().Where(a => a.ClientId == userId)
            : _db.Appointments.AsNoTracking().Where(a => a.TrainerId == userId);

        var now = DateTime.Now;
        var pendingCount = await query.CountAsync(a => a.Status == AppointmentStatus.Pending && a.StartTime >= now);
        var rescheduleCount = await query.CountAsync(a => a.ProposedStartTime != null && a.RescheduleRequestedBy != role);

        if (status.HasValue)
            query = query.Where(a => a.Status == status.Value);

        query = tab == "past"
            ? query.Where(a => a.StartTime < now).OrderByDescending(a => a.StartTime)
            : query.Where(a => a.StartTime >= now).OrderBy(a => a.StartTime);

        var appointments = await query.Take(200).ToListAsync();
        var names = await _directory.GetDisplayNamesAsync(
            appointments.Select(a => role == RoleNames.Client ? a.TrainerId : a.ClientId));

        return View(new AppointmentIndexViewModel
        {
            Role = role,
            Tab = tab == "past" ? "past" : "upcoming",
            StatusFilter = status,
            PendingCount = pendingCount,
            RescheduleRequests = rescheduleCount,
            Items = appointments.Select(a => new AppointmentListItem(a,
                names[role == RoleNames.Client ? a.TrainerId : a.ClientId])).ToList()
        });
    }

    /// <summary>Appointment details with the actions the current user is allowed to take.</summary>
    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var appointment = await FindAccessibleAsync(id);
        if (appointment == null) return NotFound();

        var role = CurrentRole;
        var names = await _directory.GetDisplayNamesAsync(new[] { appointment.ClientId, appointment.TrainerId });
        var isActive = appointment.Status is AppointmentStatus.Pending or AppointmentStatus.Approved;
        var isFuture = appointment.StartTime > DateTime.Now;

        return View(new AppointmentDetailsViewModel
        {
            Appointment = appointment,
            ClientName = names[appointment.ClientId],
            TrainerName = names[appointment.TrainerId],
            Role = role,
            CanApproveOrDecline = role == RoleNames.Trainer && appointment.Status == AppointmentStatus.Pending,
            CanCancel = isActive && isFuture,
            CanProposeReschedule = isActive && isFuture && appointment.ProposedStartTime == null && role != RoleNames.Administrator,
            CanRespondToReschedule = appointment.ProposedStartTime != null && appointment.RescheduleRequestedBy != role && role != RoleNames.Administrator,
            CanComplete = role == RoleNames.Trainer &&
                          (appointment.Status == AppointmentStatus.Approved || appointment.Status == AppointmentStatus.Completed) &&
                          appointment.StartTime <= DateTime.Now,
            CanExport = isActive || appointment.Status == AppointmentStatus.Completed
        });
    }

    // =====================================================================
    // Client: request a consultation
    // =====================================================================

    /// <summary>Client: shows trainers and their availability / submits an appointment request.</summary>
    [Authorize(Roles = RoleNames.Client)]
    [HttpGet]
    public async Task<IActionResult> Create(string? trainerId)
    {
        var model = new AppointmentRequestViewModel { TrainerId = trainerId };
        await FillRequestOptionsAsync(model);
        model.TrainerId ??= model.AssignedTrainerId;
        model.StartTime = NextRoundHour(DateTime.Now.AddDays(1));
        return View(model);
    }

    /// <summary>Client: shows trainers and their availability / submits an appointment request.</summary>
    [Authorize(Roles = RoleNames.Client)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AppointmentRequestViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await _appointments.RequestAsync(User.GetUserId(), model.TrainerId!, model.StartTime!.Value,
                model.DurationMinutes, model.Purpose!);
            if (result.Succeeded)
            {
                TempData["Success"] = "Your appointment request has been sent. You will be notified when the trainer responds.";
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddModelError(string.Empty, result.Error!);
        }

        await FillRequestOptionsAsync(model);
        return View(model);
    }

    // =====================================================================
    // Trainer: approve / decline / complete
    // =====================================================================

    /// <summary>Trainer: approves a pending request.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? returnUrl)
    {
        var result = await _appointments.ApproveAsync(id, User.GetUserId());
        SetResultMessage(result, "Appointment approved and the client has been notified.");
        return RedirectBack(returnUrl, id);
    }

    /// <summary>Trainer: declines a request with a reason and suggested alternative.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(int id, string? reason, string? suggestedAlternative, string? returnUrl)
    {
        var result = await _appointments.DeclineAsync(id, User.GetUserId(), reason ?? string.Empty, suggestedAlternative);
        SetResultMessage(result, "Appointment declined and the client has been notified.");
        return RedirectBack(returnUrl, id);
    }

    /// <summary>Trainer: shows / saves consultation notes and marks the appointment completed.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpGet]
    public async Task<IActionResult> Complete(int id)
    {
        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TrainerId == User.GetUserId());
        if (appointment == null) return NotFound();

        return View(new CompleteAppointmentViewModel
        {
            Id = id,
            Notes = appointment.ConsultationNotes,
            ClientName = await _directory.GetDisplayNameAsync(appointment.ClientId),
            StartTime = appointment.StartTime,
            Purpose = appointment.Purpose
        });
    }

    /// <summary>Trainer: shows / saves consultation notes and marks the appointment completed.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(CompleteAppointmentViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _appointments.CompleteAsync(model.Id, User.GetUserId(), model.Notes);
        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Details), new { id = model.Id });
        }
        TempData["Success"] = "Consultation marked as completed. The client can now read your notes.";
        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    // =====================================================================
    // Both parties: reschedule and cancel
    // =====================================================================

    /// <summary>Client or trainer: proposes a new time.</summary>
    [Authorize(Roles = RoleNames.Client + "," + RoleNames.Trainer)]
    [HttpGet]
    public async Task<IActionResult> Reschedule(int id)
    {
        var appointment = await FindAccessibleAsync(id);
        if (appointment == null) return NotFound();

        var role = CurrentRole;
        return View(new RescheduleViewModel
        {
            Id = id,
            CurrentStart = appointment.StartTime,
            NewStart = appointment.StartTime.AddDays(1),
            OtherPartyName = await _directory.GetDisplayNameAsync(role == RoleNames.Client ? appointment.TrainerId : appointment.ClientId),
            Availability = await DescribeAvailabilityAsync(appointment.TrainerId)
        });
    }

    /// <summary>Client or trainer: proposes a new time.</summary>
    [Authorize(Roles = RoleNames.Client + "," + RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reschedule(RescheduleViewModel model)
    {
        var appointment = await FindAccessibleAsync(model.Id);
        if (appointment == null) return NotFound();

        if (ModelState.IsValid)
        {
            var result = await _appointments.ProposeRescheduleAsync(model.Id, User.GetUserId(), CurrentRole, model.NewStart!.Value);
            if (result.Succeeded)
            {
                TempData["Success"] = "New time proposed. The other party has been asked to confirm it.";
                return RedirectToAction(nameof(Details), new { id = model.Id });
            }
            ModelState.AddModelError(string.Empty, result.Error!);
        }

        model.CurrentStart = appointment.StartTime;
        model.OtherPartyName = await _directory.GetDisplayNameAsync(CurrentRole == RoleNames.Client ? appointment.TrainerId : appointment.ClientId);
        model.Availability = await DescribeAvailabilityAsync(appointment.TrainerId);
        return View(model);
    }

    /// <summary>Accepts or rejects a proposed new time.</summary>
    [Authorize(Roles = RoleNames.Client + "," + RoleNames.Trainer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RespondReschedule(int id, bool accept)
    {
        var result = await _appointments.RespondToRescheduleAsync(id, User.GetUserId(), CurrentRole, accept);
        SetResultMessage(result, accept ? "The new time has been confirmed." : "The proposed time was declined.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Cancels an appointment with a reason (policy enforced for clients).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason, string? returnUrl)
    {
        var result = await _appointments.CancelAsync(id, User.GetUserId(), CurrentRole, reason ?? string.Empty);
        SetResultMessage(result, "The appointment has been cancelled.");
        return RedirectBack(returnUrl, id);
    }

    // =====================================================================
    // Calendar export and trainer calendar
    // =====================================================================

    /// <summary>Downloads the appointment as an iCalendar (.ics) file.</summary>
    [HttpGet]
    public async Task<IActionResult> Ics(int id)
    {
        var appointment = await FindAccessibleAsync(id);
        if (appointment == null) return NotFound();

        var names = await _directory.GetDisplayNamesAsync(new[] { appointment.ClientId, appointment.TrainerId });
        var other = CurrentRole == RoleNames.Client ? names[appointment.TrainerId] : names[appointment.ClientId];
        var bytes = IcsBuilder.Build(appointment, $"HealthTrack consultation with {other}", appointment.Purpose, names[appointment.TrainerId]);
        return File(bytes, "text/calendar", $"healthtrack-appointment-{appointment.Id}.ics");
    }

    /// <summary>Trainer calendar in day, week or month view.</summary>
    [Authorize(Roles = RoleNames.Trainer)]
    [HttpGet]
    public async Task<IActionResult> Calendar(string view = "month", DateTime? date = null)
    {
        var trainerId = User.GetUserId();
        var anchor = (date ?? DateTime.Today).Date;
        view = view is "day" or "week" ? view : "month";

        DateTime start, end, previous, next;
        string title;
        switch (view)
        {
            case "day":
                start = anchor;
                end = anchor;
                previous = anchor.AddDays(-1);
                next = anchor.AddDays(1);
                title = anchor.ToString("dddd, dd MMMM yyyy");
                break;
            case "week":
                start = DateHelpers.StartOfWeek(anchor);
                end = start.AddDays(6);
                previous = anchor.AddDays(-7);
                next = anchor.AddDays(7);
                title = $"Week of {start:dd MMM} – {end:dd MMM yyyy}";
                break;
            default:
                var monthStart = DateHelpers.StartOfMonth(anchor);
                start = DateHelpers.StartOfWeek(monthStart); // grid starts on a Monday
                end = start.AddDays(41);                      // 6 rows × 7 days
                previous = monthStart.AddMonths(-1);
                next = monthStart.AddMonths(1);
                title = monthStart.ToString("MMMM yyyy");
                break;
        }

        var appointments = await _db.Appointments.AsNoTracking()
            .Where(a => a.TrainerId == trainerId && a.StartTime >= start && a.StartTime < end.AddDays(1) &&
                        a.Status != AppointmentStatus.Declined && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.StartTime).ToListAsync();
        var names = await _directory.GetDisplayNamesAsync(appointments.Select(a => a.ClientId));

        var model = new CalendarViewModel { View = view, Date = anchor, Title = title, Previous = previous, Next = next };
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var current = day;
            model.Days.Add(new CalendarDay
            {
                Date = current,
                InCurrentMonth = view != "month" || current.Month == anchor.Month,
                Items = appointments.Where(a => a.StartTime.Date == current)
                    .Select(a => new AppointmentListItem(a, names[a.ClientId])).ToList()
            });
        }
        return View(model);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>Loads an appointment only if the current user is a participant (or an administrator).</summary>
    private async Task<Appointment?> FindAccessibleAsync(int id)
    {
        var appointment = await _db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (appointment == null) return null;
        if (User.IsInRole(RoleNames.Administrator)) return appointment;

        var userId = User.GetUserId();
        return appointment.ClientId == userId || appointment.TrainerId == userId ? appointment : null;
    }

    private async Task FillRequestOptionsAsync(AppointmentRequestViewModel model)
    {
        var clientId = User.GetUserId();
        model.Trainers = await _directory.GetApprovedTrainersAsync();
        model.AssignedTrainerId = await _db.ClientProfiles.Where(c => c.UserId == clientId).Select(c => c.TrainerId).FirstOrDefaultAsync();

        // The assigned trainer is listed first.
        model.Trainers = model.Trainers.OrderByDescending(t => t.UserId == model.AssignedTrainerId).ThenBy(t => t.FullName).ToList();
        foreach (var trainer in model.Trainers)
            model.Availability[trainer.UserId] = await DescribeAvailabilityAsync(trainer.UserId);
    }

    private async Task<List<string>> DescribeAvailabilityAsync(string trainerId)
    {
        var slots = await _db.TrainerAvailabilities.AsNoTracking().Where(s => s.TrainerId == trainerId).ToListAsync();
        return slots
            .OrderBy(s => ((int)s.DayOfWeek + 6) % 7).ThenBy(s => s.StartTime)
            .Select(s => $"{s.DayOfWeek}: {s.StartTime:hh\\:mm}–{s.EndTime:hh\\:mm}")
            .ToList();
    }

    private static DateTime NextRoundHour(DateTime value)
    {
        var rounded = new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0).AddHours(1);
        return rounded.Hour < 9 ? rounded.Date.AddHours(10) : rounded.Hour >= 17 ? rounded.Date.AddDays(1).AddHours(10) : rounded;
    }

    private void SetResultMessage(ServiceResult result, string successMessage)
    {
        if (result.Succeeded) TempData["Success"] = successMessage;
        else TempData["Error"] = result.Error;
    }

    private IActionResult RedirectBack(string? returnUrl, int id)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id });
    }
}
