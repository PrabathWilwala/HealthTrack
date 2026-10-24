using System.Security.Claims;
using HealthTrack.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.ViewComponents;

/// <summary>Navigation-bar bell showing the unread count and the five latest notifications.</summary>
public class NotificationBellViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _db;

    public NotificationBellViewComponent(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>Loads the unread count and latest notifications for the bell dropdown.</summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = UserClaimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId == null)
            return Content(string.Empty);

        ViewData["UnreadCount"] = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
        var latest = await _db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(5)
            .ToListAsync();
        return View(latest);
    }
}
