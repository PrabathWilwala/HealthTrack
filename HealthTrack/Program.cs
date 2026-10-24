using System.Threading.RateLimiting;
using HealthTrack.Data;
using HealthTrack.Infrastructure;
using HealthTrack.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration
    .GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString)
           // The hand-maintained model snapshot can differ cosmetically from the runtime model;
           // that must not block Database.Migrate() at start-up.
           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

// ---------------------------------------------------------------------------
// Encryption keys (used for health data at rest and for auth cookies)
// ---------------------------------------------------------------------------
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("HealthTrack")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi(); // keys themselves are encrypted with Windows DPAPI

// ---------------------------------------------------------------------------
// Identity (hashed passwords: PBKDF2-HMAC-SHA512, 100,000 iterations)
// ---------------------------------------------------------------------------
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = true;
    options.SignIn.RequireConfirmedEmail = true;
    options.User.RequireUniqueEmail = true;

    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);

    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// Password reset / email confirmation links expire after 3 hours.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(3));

// Re-validate the security stamp every minute so deactivated users are signed out quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

// Secure session cookie: HttpOnly, SameSite, HTTPS-only when served over HTTPS, 30 min sliding expiry.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.Name = "HealthTrack.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = true;
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "HealthTrack.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// ---------------------------------------------------------------------------
// Brute-force protection on sign-in / password endpoints (per IP address)
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            "<h2 style='font-family:sans-serif'>Too many attempts</h2>" +
            "<p style='font-family:sans-serif'>Please wait a minute and try again.</p>" +
            "<p style='font-family:sans-serif'><a href='/Account/Login'>Back to sign in</a></p>",
            cancellationToken);
    };
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IUserDirectory, UserDirectory>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAppointmentService, AppointmentService>();
builder.Services.AddScoped<IPlanService, PlanService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IUserDataService, UserDataService>();
builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
builder.Services.AddSingleton<IAppEmailSender, EmailSender>();

// Reminder worker: registered as a singleton so administrators can trigger it manually.
builder.Services.AddSingleton<ReminderBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReminderBackgroundService>());

// MVC with a global anti-forgery (CSRF) check on every POST.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    // Only explicit [Required] attributes make a field mandatory (not C# nullability).
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/Home/HttpStatus", "?code={0}");

app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles(); // serves runtime uploads (profile pictures); build-time assets use MapStaticAssets
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Apply migrations and seed roles, the first administrator, templates and demo data.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, logger);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Database initialisation failed. Check the 'DefaultConnection' connection string in appsettings.json.");
        throw;
    }
}

// .NET 9 static files and MVC routing
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
