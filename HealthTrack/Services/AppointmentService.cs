using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Services;

// Two appointments of the same trainer that overlap in time.
public sealed record AppointmentConflict(Appointment First, Appointment Second);

public interface IAppointmentService
{
    Task<ServiceResult> RequestAsync(string clientId, string trainerId, DateTime start, int durationMinutes, string purpose);
    Task<ServiceResult> ApproveAsync(int appointmentId, string trainerId);
    Task<ServiceResult> DeclineAsync(int appointmentId, string trainerId, string reason, string? suggestedAlternative);
    Task<ServiceResult> CancelAsync(int appointmentId, string actorId, string actorRole, string reason);
    Task<ServiceResult> ProposeRescheduleAsync(int appointmentId, string actorId, string actorRole, DateTime newStart);
    Task<ServiceResult> RespondToRescheduleAsync(int appointmentId, string actorId, string actorRole, bool accept);
    Task<ServiceResult> CompleteAsync(int appointmentId, string trainerId, string? notes);
    Task<ServiceResult> AdminRescheduleAsync(int appointmentId, DateTime newStart);
    Task<List<AppointmentConflict>> FindConflictsAsync();
}

// All appointment business rules live here: booking validation, trainer availability,
// conflict detection, the cancellation policy and status transitions (with notifications).
public sealed class AppointmentService : IAppointmentService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IUserDirectory _directory;
    private readonly IAuditService _audit;

    public AppointmentService(ApplicationDbContext db, ISettingsService settings,
        INotificationService notifications, IUserDirectory directory, IAuditService audit)
    {
        _db = db;
        _settings = settings;
        _notifications = notifications;
        _directory = directory;
        _audit = audit;
    }

    // Client books a consultation: validates the trainer, the slot (notice, availability, conflicts), saves it as Pending and notifies the trainer.
    public async Task<ServiceResult> RequestAsync(string clientId, string trainerId, DateTime start, int durationMinutes, string purpose)
    {
        var trainer = await _db.TrainerProfiles.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == trainerId);
        if (trainer == null || !trainer.IsApproved)
            return ServiceResult.Fail("Please choose an approved trainer.");

        var slotError = await ValidateSlotAsync(trainerId, start, durationMinutes, null);
        if (slotError != null)
            return ServiceResult.Fail(slotError);

        var appointment = new Appointment
        {
            ClientId = clientId,
            TrainerId = trainerId,
            StartTime = start,
            DurationMinutes = durationMinutes,
            Purpose = purpose.Trim(),
            Status = AppointmentStatus.Pending
        };
        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync();

        var clientName = await _directory.GetDisplayNameAsync(clientId);
        await _notifications.NotifyFromTemplateAsync(trainerId, NotificationType.AppointmentUpdate,
            NotificationTemplateKeys.AppointmentRequested,
            new Dictionary<string, string>
            {
                ["ClientName"] = clientName,
                ["Start"] = NotificationFormat.When(start),
                ["Purpose"] = appointment.Purpose
            },
            "/Appointments/Details/" + appointment.Id);

        await _audit.LogAsync("Appointment", "Appointment.Requested", $"Appointment #{appointment.Id} with trainer {trainer.FullName}");
        return ServiceResult.Ok();
    }

    // Trainer approves a pending request after re-checking for clashes with already approved appointments.
    public async Task<ServiceResult> ApproveAsync(int appointmentId, string trainerId)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.TrainerId == trainerId);
        if (appointment == null) return ServiceResult.Fail("Appointment not found.");
        if (appointment.Status != AppointmentStatus.Pending)
            return ServiceResult.Fail("Only pending requests can be approved.");
        if (appointment.StartTime <= DateTime.Now)
            return ServiceResult.Fail("This request is in the past. Decline it or propose a new time.");

        var conflict = await FindTrainerConflictAsync(trainerId, appointment.StartTime, appointment.DurationMinutes, appointment.Id, approvedOnly: true);
        if (conflict != null)
            return ServiceResult.Fail($"You already have an approved appointment at {NotificationFormat.When(conflict.StartTime)}.");

        appointment.Status = AppointmentStatus.Approved;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await NotifyClientAsync(appointment, NotificationTemplateKeys.AppointmentApproved, new Dictionary<string, string>());
        await _audit.LogAsync("Appointment", "Appointment.Approved", $"Appointment #{appointment.Id}");
        return ServiceResult.Ok();
    }

    // Trainer declines a pending request with a reason and optional alternative times; the client is notified.
    public async Task<ServiceResult> DeclineAsync(int appointmentId, string trainerId, string reason, string? suggestedAlternative)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.TrainerId == trainerId);
        if (appointment == null) return ServiceResult.Fail("Appointment not found.");
        if (appointment.Status != AppointmentStatus.Pending)
            return ServiceResult.Fail("Only pending requests can be declined.");
        if (string.IsNullOrWhiteSpace(reason))
            return ServiceResult.Fail("Please give a reason for declining.");

        appointment.Status = AppointmentStatus.Declined;
        appointment.StatusReason = reason.Trim();
        appointment.SuggestedAlternative = string.IsNullOrWhiteSpace(suggestedAlternative) ? null : suggestedAlternative.Trim();
        appointment.ProposedStartTime = null;
        appointment.RescheduleRequestedBy = null;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var reasonText = appointment.StatusReason +
            (appointment.SuggestedAlternative != null ? $" Suggested alternative: {appointment.SuggestedAlternative}" : string.Empty);
        await NotifyClientAsync(appointment, NotificationTemplateKeys.AppointmentDeclined,
            new Dictionary<string, string> { ["Reason"] = reasonText });
        await _audit.LogAsync("Appointment", "Appointment.Declined", $"Appointment #{appointment.Id}");
        return ServiceResult.Ok();
    }

    // Cancels a pending/approved appointment (client cancellations must satisfy the cancellation policy) and notifies the other party.
    public async Task<ServiceResult> CancelAsync(int appointmentId, string actorId, string actorRole, string reason)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId);
        if (appointment == null || !CanAct(appointment, actorId, actorRole))
            return ServiceResult.Fail("Appointment not found.");
        if (appointment.Status != AppointmentStatus.Pending && appointment.Status != AppointmentStatus.Approved)
            return ServiceResult.Fail("This appointment can no longer be cancelled.");
        if (string.IsNullOrWhiteSpace(reason))
            return ServiceResult.Fail("Please give a reason for the cancellation.");

        // Cancellation policy applies to clients only (administrators enforce it; trainers manage their diary).
        if (actorRole == RoleNames.Client)
        {
            var policyError = await CheckClientCancellationPolicyAsync(appointment);
            if (policyError != null)
                return ServiceResult.Fail(policyError);
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.StatusReason = reason.Trim();
        appointment.CancelledBy = actorRole;
        appointment.ProposedStartTime = null;
        appointment.RescheduleRequestedBy = null;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var tokens = new Dictionary<string, string>
        {
            ["Start"] = NotificationFormat.When(appointment.StartTime),
            ["CancelledBy"] = actorRole.ToLowerInvariant(),
            ["Reason"] = appointment.StatusReason
        };
        var link = "/Appointments/Details/" + appointment.Id;
        if (actorId != appointment.ClientId)
            await _notifications.NotifyFromTemplateAsync(appointment.ClientId, NotificationType.AppointmentUpdate,
                NotificationTemplateKeys.AppointmentCancelled, tokens, link);
        if (actorId != appointment.TrainerId)
            await _notifications.NotifyFromTemplateAsync(appointment.TrainerId, NotificationType.AppointmentUpdate,
                NotificationTemplateKeys.AppointmentCancelled, tokens, link);

        await _audit.LogAsync("Appointment", "Appointment.Cancelled", $"Appointment #{appointment.Id} by {actorRole}");
        return ServiceResult.Ok();
    }

    // Either party proposes a new start time; the slot is validated and the other party is asked to confirm.
    public async Task<ServiceResult> ProposeRescheduleAsync(int appointmentId, string actorId, string actorRole, DateTime newStart)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId);
        if (appointment == null || !CanAct(appointment, actorId, actorRole))
            return ServiceResult.Fail("Appointment not found.");
        if (appointment.Status != AppointmentStatus.Pending && appointment.Status != AppointmentStatus.Approved)
            return ServiceResult.Fail("Only pending or approved appointments can be rescheduled.");

        var slotError = await ValidateSlotAsync(appointment.TrainerId, newStart, appointment.DurationMinutes, appointment.Id);
        if (slotError != null)
            return ServiceResult.Fail(slotError);

        appointment.ProposedStartTime = newStart;
        appointment.RescheduleRequestedBy = actorRole;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var otherPartyId = actorRole == RoleNames.Client ? appointment.TrainerId : appointment.ClientId;
        await _notifications.NotifyFromTemplateAsync(otherPartyId, NotificationType.AppointmentUpdate,
            NotificationTemplateKeys.RescheduleRequested,
            new Dictionary<string, string>
            {
                ["RequesterName"] = await _directory.GetDisplayNameAsync(actorId),
                ["Start"] = NotificationFormat.When(appointment.StartTime),
                ["ProposedStart"] = NotificationFormat.When(newStart)
            },
            "/Appointments/Details/" + appointment.Id);

        await _audit.LogAsync("Appointment", "Appointment.RescheduleProposed", $"Appointment #{appointment.Id} by {actorRole}");
        return ServiceResult.Ok();
    }

    // The other party accepts (time is moved and appointment approved) or rejects a proposed new time.
    public async Task<ServiceResult> RespondToRescheduleAsync(int appointmentId, string actorId, string actorRole, bool accept)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId);
        if (appointment == null || !CanAct(appointment, actorId, actorRole))
            return ServiceResult.Fail("Appointment not found.");
        if (appointment.ProposedStartTime == null)
            return ServiceResult.Fail("There is no pending reschedule request.");
        if (appointment.RescheduleRequestedBy == actorRole)
            return ServiceResult.Fail("You cannot respond to your own reschedule request.");

        var requesterId = appointment.RescheduleRequestedBy == RoleNames.Client ? appointment.ClientId : appointment.TrainerId;

        if (accept)
        {
            var newStart = appointment.ProposedStartTime.Value;
            var slotError = await ValidateSlotAsync(appointment.TrainerId, newStart, appointment.DurationMinutes, appointment.Id);
            if (slotError != null)
                return ServiceResult.Fail(slotError);

            appointment.StartTime = newStart;
            // Whoever accepts confirms the new time, so the appointment becomes approved.
            appointment.Status = AppointmentStatus.Approved;
        }

        appointment.ProposedStartTime = null;
        appointment.RescheduleRequestedBy = null;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (accept)
            await _notifications.NotifyFromTemplateAsync(requesterId, NotificationType.AppointmentUpdate,
                NotificationTemplateKeys.AppointmentRescheduled,
                new Dictionary<string, string> { ["Start"] = NotificationFormat.When(appointment.StartTime) },
                "/Appointments/Details/" + appointment.Id);
        else
            await _notifications.NotifyAsync(requesterId, NotificationType.AppointmentUpdate,
                "Reschedule request declined",
                $"Your proposed new time was declined. The appointment remains on {NotificationFormat.When(appointment.StartTime)}.",
                "/Appointments/Details/" + appointment.Id);

        await _audit.LogAsync("Appointment", accept ? "Appointment.RescheduleAccepted" : "Appointment.RescheduleRejected",
            $"Appointment #{appointment.Id}");
        return ServiceResult.Ok();
    }

    // Trainer marks a consultation as completed and saves encrypted consultation notes for the client.
    public async Task<ServiceResult> CompleteAsync(int appointmentId, string trainerId, string? notes)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId && a.TrainerId == trainerId);
        if (appointment == null) return ServiceResult.Fail("Appointment not found.");
        if (appointment.Status != AppointmentStatus.Approved && appointment.Status != AppointmentStatus.Completed)
            return ServiceResult.Fail("Only approved appointments can be completed.");
        if (appointment.StartTime > DateTime.Now)
            return ServiceResult.Fail("You can complete a consultation once it has started.");

        var hadNotes = !string.IsNullOrWhiteSpace(appointment.ConsultationNotes);
        appointment.Status = AppointmentStatus.Completed;
        appointment.ConsultationNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(appointment.ConsultationNotes))
            await NotifyClientAsync(appointment, NotificationTemplateKeys.ConsultationNotesAdded,
                new Dictionary<string, string>(), NotificationType.Feedback,
                dedupKey: hadNotes ? null : $"notes:{appointment.Id}");

        await _audit.LogAsync("Appointment", "Appointment.Completed", $"Appointment #{appointment.Id}");
        return ServiceResult.Ok();
    }

    // Conflict resolution by an administrator: moves the appointment directly.
    public async Task<ServiceResult> AdminRescheduleAsync(int appointmentId, DateTime newStart)
    {
        var appointment = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId);
        if (appointment == null) return ServiceResult.Fail("Appointment not found.");
        if (newStart <= DateTime.Now) return ServiceResult.Fail("The new time must be in the future.");

        var conflict = await FindTrainerConflictAsync(appointment.TrainerId, newStart, appointment.DurationMinutes, appointment.Id, approvedOnly: false);
        if (conflict != null)
            return ServiceResult.Fail($"The trainer already has appointment #{conflict.Id} at that time.");

        appointment.StartTime = newStart;
        appointment.ProposedStartTime = null;
        appointment.RescheduleRequestedBy = null;
        appointment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var tokens = new Dictionary<string, string> { ["Start"] = NotificationFormat.When(newStart) };
        var link = "/Appointments/Details/" + appointment.Id;
        await _notifications.NotifyFromTemplateAsync(appointment.ClientId, NotificationType.AppointmentUpdate, NotificationTemplateKeys.AppointmentRescheduled, tokens, link);
        await _notifications.NotifyFromTemplateAsync(appointment.TrainerId, NotificationType.AppointmentUpdate, NotificationTemplateKeys.AppointmentRescheduled, tokens, link);
        await _audit.LogAsync("Admin", "Appointment.AdminRescheduled", $"Appointment #{appointment.Id} moved to {newStart:g}");
        return ServiceResult.Ok();
    }

    // Finds pairs of active appointments for the same trainer that overlap.
    public async Task<List<AppointmentConflict>> FindConflictsAsync()
    {
        var now = DateTime.Now;
        var active = await _db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Approved) && a.StartTime >= now.AddDays(-1))
            .OrderBy(a => a.TrainerId).ThenBy(a => a.StartTime)
            .ToListAsync();

        var conflicts = new List<AppointmentConflict>();
        foreach (var group in active.GroupBy(a => a.TrainerId))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count && list[j].StartTime < list[i].EndTime; j++)
                    conflicts.Add(new AppointmentConflict(list[i], list[j]));
        }
        return conflicts;
    }

    // ------------------------------------------------------------------ helpers

    // Validates a requested time: in the future (with minimum notice), inside the trainer's
    // published availability (if any) and not overlapping another active appointment.
    // Returns an error message, or null when the slot is acceptable.
    private async Task<string?> ValidateSlotAsync(string trainerId, DateTime start, int durationMinutes, int? ignoreAppointmentId)
    {
        if (durationMinutes < 15 || durationMinutes > 180)
            return "Duration must be between 15 and 180 minutes.";

        var minNoticeHours = await _settings.GetIntAsync(SettingKeys.MinBookingNoticeHours);
        if (start < DateTime.Now.AddHours(minNoticeHours))
            return $"Appointments must be booked at least {minNoticeHours} hour(s) in advance.";
        if (start > DateTime.Now.AddMonths(6))
            return "Appointments can be booked up to 6 months ahead.";

        var end = start.AddMinutes(durationMinutes);
        if (end.Date != start.Date)
            return "An appointment must start and finish on the same day.";

        var slots = await _db.TrainerAvailabilities.AsNoTracking()
            .Where(s => s.TrainerId == trainerId).ToListAsync();
        if (slots.Count > 0)
        {
            var fits = slots.Any(s => s.DayOfWeek == start.DayOfWeek &&
                                      s.StartTime <= start.TimeOfDay &&
                                      s.EndTime >= end.TimeOfDay);
            if (!fits)
            {
                var sameDay = slots.Where(s => s.DayOfWeek == start.DayOfWeek)
                    .Select(s => $"{s.StartTime:hh\\:mm}–{s.EndTime:hh\\:mm}").ToList();
                return sameDay.Count == 0
                    ? $"This trainer is not available on {start.DayOfWeek}s."
                    : $"This trainer is available on {start.DayOfWeek}s at: {string.Join(", ", sameDay)}.";
            }
        }

        var conflict = await FindTrainerConflictAsync(trainerId, start, durationMinutes, ignoreAppointmentId, approvedOnly: false);
        if (conflict != null)
            return "The trainer already has an appointment at that time. Please choose another slot.";

        return null;
    }

    private async Task<Appointment?> FindTrainerConflictAsync(string trainerId, DateTime start, int durationMinutes, int? ignoreId, bool approvedOnly)
    {
        var end = start.AddMinutes(durationMinutes);
        var dayStart = start.Date;
        var dayEnd = dayStart.AddDays(1);

        var query = _db.Appointments.AsNoTracking()
            .Where(a => a.TrainerId == trainerId && a.StartTime >= dayStart && a.StartTime < dayEnd);
        query = approvedOnly
            ? query.Where(a => a.Status == AppointmentStatus.Approved)
            : query.Where(a => a.Status == AppointmentStatus.Approved || a.Status == AppointmentStatus.Pending);
        var sameDay = await query.ToListAsync();

        return sameDay.FirstOrDefault(a => a.Id != ignoreId && a.Overlaps(start, end));
    }

    // Applies the administrator's cancellation policy to a client cancellation.
    private async Task<string?> CheckClientCancellationPolicyAsync(Appointment appointment)
    {
        if (appointment.Status == AppointmentStatus.Approved)
        {
            var noticeHours = await _settings.GetIntAsync(SettingKeys.MinCancellationNoticeHours);
            if (appointment.StartTime < DateTime.Now.AddHours(noticeHours))
                return $"Approved appointments must be cancelled at least {noticeHours} hour(s) in advance. Please contact your trainer.";
        }

        var maxPerMonth = await _settings.GetIntAsync(SettingKeys.MaxCancellationsPerMonth);
        if (maxPerMonth > 0)
        {
            var monthStart = DateHelpers.StartOfMonth(DateTime.Now);
            var monthStartUtc = monthStart.ToUniversalTime();
            var cancellations = await _db.Appointments.CountAsync(a =>
                a.ClientId == appointment.ClientId &&
                a.Status == AppointmentStatus.Cancelled &&
                a.CancelledBy == RoleNames.Client &&
                a.UpdatedAtUtc >= monthStartUtc);
            if (cancellations >= maxPerMonth)
                return $"You have reached the limit of {maxPerMonth} cancellations this month. Please contact your trainer.";
        }
        return null;
    }

    private static bool CanAct(Appointment appointment, string actorId, string actorRole) => actorRole switch
    {
        RoleNames.Client => appointment.ClientId == actorId,
        RoleNames.Trainer => appointment.TrainerId == actorId,
        RoleNames.Administrator => true,
        _ => false
    };

    private async Task NotifyClientAsync(Appointment appointment, string templateKey, Dictionary<string, string> tokens,
        NotificationType type = NotificationType.AppointmentUpdate, string? dedupKey = null)
    {
        tokens["TrainerName"] = await _directory.GetDisplayNameAsync(appointment.TrainerId);
        tokens["Start"] = NotificationFormat.When(appointment.StartTime);
        await _notifications.NotifyFromTemplateAsync(appointment.ClientId, type, templateKey, tokens,
            "/Appointments/Details/" + appointment.Id, dedupKey);
    }
}
