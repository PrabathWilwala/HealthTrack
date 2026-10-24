using System.Diagnostics;
using HealthTrack.Models;
using Microsoft.AspNetCore.Mvc;

namespace HealthTrack.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        // Public landing page. Signed-in users go straight to their dashboard.
        public IActionResult Index()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Dashboard", "Account");
            return View();
        }

        // Privacy policy (GDPR principles: consent, minimisation, access and erasure).
        public IActionResult Privacy()
        {
            return View();
        }

        // Generic error page.
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            _logger.LogError("Unhandled error page shown for request {RequestId}", requestId);
            return View(new ErrorViewModel { RequestId = requestId });
        }

        // Friendly pages for 404, 403, 429 and other status codes.
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult HttpStatus(int code)
        {
            ViewData["Code"] = code;
            return View();
        }
    }
}
