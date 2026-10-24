using HealthTrack.Infrastructure;
using HealthTrack.Models;
using HealthTrack.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Data;

// Runs at start-up: applies migrations, creates roles, the initial administrator,
// notification templates, default policies and (optionally) demonstration data.
public static class DbSeeder
{
    // Applies migrations, then seeds roles, the first administrator, notification templates, default policies and optional demo data.
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();

        // Apply any pending EF Core migrations (creates the database on first run).
        await db.Database.MigrateAsync();

        foreach (var role in RoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Could not create role '{role}': " +
                        string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }

        await SeedAdministratorAsync(userManager, configuration, logger);
        await SeedTemplatesAsync(db);
        await SeedSettingsAsync(db);

        if (configuration.GetValue("SeedDemoData", false))
            await SeedDemoDataAsync(db, userManager, logger);
    }

    // Administrators cannot self-register, so the first one is created from configuration.
    private static async Task SeedAdministratorAsync(UserManager<IdentityUser> userManager, IConfiguration configuration, ILogger logger)
    {
        if ((await userManager.GetUsersInRoleAsync(RoleNames.Administrator)).Count > 0)
            return;

        var section = configuration.GetSection("SeedAdmin");
        var userName = section["UserName"] ?? "admin";
        var email = section["Email"] ?? "admin@healthtrack.local";
        var password = section["Password"] ?? "Admin@12345";

        var admin = await userManager.FindByNameAsync(userName);
        if (admin == null)
        {
            admin = new IdentityUser { UserName = userName, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(admin, password);
            if (!created.Succeeded)
            {
                logger.LogError("Could not create the seed administrator: {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
        }
        await userManager.AddToRoleAsync(admin, RoleNames.Administrator);
        logger.LogWarning("Seed administrator '{UserName}' created. Change its password after first sign-in.", userName);
    }

    private static async Task SeedTemplatesAsync(ApplicationDbContext db)
    {
        var existing = await db.NotificationTemplates.Select(t => t.Key).ToListAsync();
        foreach (var (key, name, title, body) in NotificationTemplateKeys.Defaults)
        {
            if (!existing.Contains(key))
                db.NotificationTemplates.Add(new NotificationTemplate { Key = key, Name = name, TitleTemplate = title, BodyTemplate = body });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedSettingsAsync(ApplicationDbContext db)
    {
        var existing = await db.SystemSettings.Select(s => s.Key).ToListAsync();
        foreach (var setting in SettingKeys.Defaults.Where(d => !existing.Contains(d.Key)))
            db.SystemSettings.Add(new SystemSetting { Key = setting.Key, Value = setting.Value });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------
    // Demonstration data so every dashboard and chart has something to show.
    // ---------------------------------------------------------------------

    private static async Task SeedDemoDataAsync(ApplicationDbContext db, UserManager<IdentityUser> userManager, ILogger logger)
    {
        if (await userManager.FindByNameAsync("trainer") != null)
            return; // already seeded

        var random = new Random(2026);
        var today = DateTime.Today;

        // --- Trainers ---
        var trainer = await CreateUserAsync(userManager, "trainer", "trainer@healthtrack.local", "Trainer@12345", RoleNames.Trainer);
        var trainer2 = await CreateUserAsync(userManager, "trainer2", "trainer2@healthtrack.local", "Trainer@12345", RoleNames.Trainer);
        var pendingTrainer = await CreateUserAsync(userManager, "newtrainer", "newtrainer@healthtrack.local", "Trainer@12345", RoleNames.Trainer);
        if (trainer == null || trainer2 == null || pendingTrainer == null)
        {
            logger.LogError("Demo data could not be created.");
            return;
        }

        db.TrainerProfiles.AddRange(
            new TrainerProfile
            {
                UserId = trainer.Id, FullName = "Nimal Perera", Qualifications = "BSc Sports Science",
                Specializations = "Weight management, strength training", Certifications = "ACE Certified Personal Trainer",
                Bio = "Helping clients build sustainable habits for 8 years.", IsApproved = true,
                ApprovedAtUtc = DateTime.UtcNow, ConsentAcceptedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow.AddDays(-90)
            },
            new TrainerProfile
            {
                UserId = trainer2.Id, FullName = "Ayesha Fernando", Qualifications = "MSc Clinical Nutrition",
                Specializations = "Nutrition, diabetes-friendly diets", IsApproved = true,
                ApprovedAtUtc = DateTime.UtcNow, ConsentAcceptedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow.AddDays(-60)
            },
            new TrainerProfile
            {
                UserId = pendingTrainer.Id, FullName = "Kasun Silva", Qualifications = "Diploma in Fitness Instruction",
                Specializations = "Yoga, mobility", IsApproved = false, ConsentAcceptedAtUtc = DateTime.UtcNow
            });

        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            db.TrainerAvailabilities.Add(new TrainerAvailability { TrainerId = trainer.Id, DayOfWeek = day, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(17, 0, 0) });
        db.TrainerAvailabilities.Add(new TrainerAvailability { TrainerId = trainer.Id, DayOfWeek = DayOfWeek.Saturday, StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(12, 0, 0) });
        await db.SaveChangesAsync();

        // --- Clients ---
        var client = await CreateUserAsync(userManager, "client", "client@healthtrack.local", "Client@12345", RoleNames.Client);
        var client2 = await CreateUserAsync(userManager, "client2", "client2@healthtrack.local", "Client@12345", RoleNames.Client);
        var client3 = await CreateUserAsync(userManager, "client3", "client3@healthtrack.local", "Client@12345", RoleNames.Client);
        if (client == null || client2 == null || client3 == null)
        {
            logger.LogError("Demo clients could not be created.");
            return;
        }

        db.ClientProfiles.AddRange(
            new ClientProfile
            {
                UserId = client.Id, FullName = "Dilini Jayasuriya", Age = 29, Gender = "Female", HeightCm = 162, WeightKg = 68.5m,
                TargetWeightKg = 62, DailyCalorieTarget = 1800, FitnessGoals = "Lose 6 kg and run a 5K",
                TrainerId = trainer.Id, ConsentAcceptedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow.AddDays(-60)
            },
            new ClientProfile
            {
                UserId = client2.Id, FullName = "Ruwan Bandara", Age = 41, Gender = "Male", HeightCm = 175, WeightKg = 88,
                TargetWeightKg = 80, FitnessGoals = "Reduce weight and blood pressure", TrainerId = trainer.Id,
                ConsentAcceptedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow.AddDays(-45)
            },
            new ClientProfile
            {
                UserId = client3.Id, FullName = "Tharindu Wickramasinghe", Age = 24, Gender = "Male", HeightCm = 180, WeightKg = 70,
                TargetWeightKg = 75, FitnessGoals = "Build muscle", ConsentAcceptedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow.AddDays(-20)
            });
        await db.SaveChangesAsync();

        // --- Plans ---
        var workoutPlan = new WorkoutPlan
        {
            Title = "Fat Loss Foundations", Description = "Full-body strength plus steady cardio.", Goal = "Weight loss",
            Intensity = IntensityLevel.Moderate, SessionsPerWeek = 4, DurationWeeks = 8, CreatedById = trainer.Id,
            Exercises = new List<WorkoutExercise>
            {
                new() { Name = "Brisk walk / jog", DayOfWeek = DayOfWeek.Monday, DurationMinutes = 30, SortOrder = 0 },
                new() { Name = "Goblet squat", DayOfWeek = DayOfWeek.Monday, Sets = 3, Reps = 12, SortOrder = 1 },
                new() { Name = "Push-ups", DayOfWeek = DayOfWeek.Wednesday, Sets = 3, Reps = 10, SortOrder = 2 },
                new() { Name = "Cycling", DayOfWeek = DayOfWeek.Wednesday, DurationMinutes = 25, SortOrder = 3 },
                new() { Name = "Dumbbell rows", DayOfWeek = DayOfWeek.Friday, Sets = 3, Reps = 12, SortOrder = 4 },
                new() { Name = "Plank", DayOfWeek = DayOfWeek.Friday, Sets = 3, DurationMinutes = 1, SortOrder = 5 },
                new() { Name = "Long walk", DayOfWeek = DayOfWeek.Saturday, DurationMinutes = 45, SortOrder = 6 }
            }
        };
        var templatePlan = new WorkoutPlan
        {
            Title = "Beginner Strength (template)", Description = "3-day full-body routine.", Goal = "Strength",
            Intensity = IntensityLevel.Low, SessionsPerWeek = 3, DurationWeeks = 6, CreatedById = trainer.Id, IsTemplate = true,
            Exercises = new List<WorkoutExercise>
            {
                new() { Name = "Bodyweight squat", Sets = 3, Reps = 15, SortOrder = 0 },
                new() { Name = "Incline push-up", Sets = 3, Reps = 10, SortOrder = 1 },
                new() { Name = "Glute bridge", Sets = 3, Reps = 15, SortOrder = 2 }
            }
        };
        var systemTemplate = new WorkoutPlan
        {
            Title = "HealthTrack Starter Cardio", Description = "System template: gentle cardio for new members.",
            Intensity = IntensityLevel.Low, SessionsPerWeek = 3, DurationWeeks = 4, IsTemplate = true, IsSystemTemplate = true,
            Exercises = new List<WorkoutExercise> { new() { Name = "Brisk walk", DurationMinutes = 30, SortOrder = 0 } }
        };
        var dietPlan = new DietPlan
        {
            Title = "Balanced 1800 kcal", Description = "High-protein, moderate-carb plan.", DailyCalorieTarget = 1800,
            ProteinTargetG = 120, CarbsTargetG = 180, FatTargetG = 60, Guidelines = "Drink 2–3 L of water. Limit sugary drinks.",
            CreatedById = trainer.Id,
            Meals = new List<DietMeal>
            {
                new() { MealType = MealType.Breakfast, FoodItems = "Oats, banana, milk", Portion = "1 bowl", Calories = 400, ProteinG = 18, CarbsG = 65, FatG = 8, SortOrder = 0 },
                new() { MealType = MealType.Lunch, FoodItems = "Red rice, chicken curry, dhal, salad", Portion = "1 plate", Calories = 650, ProteinG = 45, CarbsG = 70, FatG = 18, SortOrder = 1 },
                new() { MealType = MealType.Snack, FoodItems = "Yoghurt and fruit", Portion = "1 cup", Calories = 200, ProteinG = 12, CarbsG = 25, FatG = 5, SortOrder = 2 },
                new() { MealType = MealType.Dinner, FoodItems = "String hoppers, fish curry, sambol", Portion = "10 pieces", Calories = 550, ProteinG = 40, CarbsG = 60, FatG = 15, SortOrder = 3 }
            }
        };
        db.WorkoutPlans.AddRange(workoutPlan, templatePlan, systemTemplate);
        db.DietPlans.Add(dietPlan);
        await db.SaveChangesAsync();

        var planStart = today.AddDays(-42);
        foreach (var c in new[] { client, client2 })
        {
            db.PlanAssignments.Add(new PlanAssignment { ClientId = c.Id, AssignedById = trainer.Id, WorkoutPlanId = workoutPlan.Id, StartDate = planStart, EndDate = today.AddDays(14) });
            db.PlanAssignments.Add(new PlanAssignment { ClientId = c.Id, AssignedById = trainer.Id, DietPlanId = dietPlan.Id, StartDate = planStart, EndDate = today.AddDays(14) });
        }
        await db.SaveChangesAsync();

        // --- Logs: 45 days of realistic activity ---
        string[] exercises = { "Jogging", "Strength training", "Cycling", "Brisk walk", "Swimming" };
        var demoClients = new[] { (client, 70.4m, -0.07m, 0.75), (client2, 90.0m, -0.05m, 0.45), (client3, 69.0m, 0.04m, 0.6) };
        foreach (var (user, startWeight, dailyTrend, activityRate) in demoClients)
        {
            var height = user.Id == client.Id ? 162m : user.Id == client2.Id ? 175m : 180m;
            var onPlan = user.Id != client3.Id;
            for (var offset = 45; offset >= 0; offset--)
            {
                var date = today.AddDays(-offset);
                // Client 2 becomes less active over the last 10 days (drives the adherence alert).
                var rate = user.Id == client2.Id && offset < 10 ? 0.15 : activityRate;

                if (offset % 3 == 0)
                {
                    var weight = Math.Round(startWeight + dailyTrend * (45 - offset) + (decimal)(random.NextDouble() - 0.5) * 0.6m, 1);
                    db.HealthMeasurements.Add(new HealthMeasurement
                    {
                        ClientId = user.Id, MeasuredOn = date, WeightKg = weight, Bmi = BmiCalculator.Calculate(weight, height),
                        RestingHeartRate = 62 + random.Next(0, 12),
                        SystolicBp = user.Id == client2.Id ? 132 + random.Next(0, 10) : 112 + random.Next(0, 10),
                        DiastolicBp = user.Id == client2.Id ? 86 + random.Next(0, 6) : 72 + random.Next(0, 6)
                    });
                }

                if (random.NextDouble() < rate)
                {
                    var minutes = 25 + random.Next(0, 40);
                    db.WorkoutLogs.Add(new WorkoutLog
                    {
                        ClientId = user.Id, WorkoutPlanId = onPlan ? workoutPlan.Id : (int?)null,
                        ExerciseType = exercises[random.Next(exercises.Length)], DurationMinutes = minutes,
                        Intensity = (IntensityLevel)random.Next(1, 4), CaloriesBurned = minutes * (6 + random.Next(0, 5)),
                        LogDate = date, CreatedAtUtc = date.AddHours(18).ToUniversalTime()
                    });
                }

                if (random.NextDouble() < rate + 0.2)
                {
                    foreach (var meal in dietPlan.Meals)
                    {
                        var factor = 0.8m + (decimal)random.NextDouble() * 0.45m;
                        db.MealLogs.Add(new MealLog
                        {
                            ClientId = user.Id, DietPlanId = onPlan ? dietPlan.Id : (int?)null, MealType = meal.MealType,
                            FoodItems = meal.FoodItems, Portion = meal.Portion, Calories = (int)(meal.Calories * factor),
                            ProteinG = Math.Round(meal.ProteinG * factor, 1), CarbsG = Math.Round(meal.CarbsG * factor, 1),
                            FatG = Math.Round(meal.FatG * factor, 1), LogDate = date, CreatedAtUtc = date.AddHours(13).ToUniversalTime()
                        });
                    }
                }
            }

            // Audit entries so engagement/last-active statistics are populated.
            for (var i = 0; i < 12; i++)
            {
                var when = DateTime.UtcNow.AddDays(-random.Next(0, 30)).AddHours(-random.Next(0, 12));
                db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.UserName, Category = "Auth", Action = "Login.Success", CreatedAtUtc = when });
                db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.UserName, Category = "Workout", Action = "Workout.Logged", CreatedAtUtc = when.AddMinutes(5) });
            }
        }
        await db.SaveChangesAsync();

        // Latest weights become the profile's current weight.
        foreach (var profile in await db.ClientProfiles.ToListAsync())
        {
            var latest = await db.HealthMeasurements.Where(m => m.ClientId == profile.UserId).OrderByDescending(m => m.MeasuredOn).FirstOrDefaultAsync();
            if (latest != null) profile.WeightKg = latest.WeightKg;
        }

        // --- Appointments ---
        var nextWeekday = today.AddDays(1);
        while (nextWeekday.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) nextWeekday = nextWeekday.AddDays(1);
        var pastWeekday = today.AddDays(-7);
        while (pastWeekday.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) pastWeekday = pastWeekday.AddDays(-1);

        db.Appointments.AddRange(
            new Appointment
            {
                ClientId = client.Id, TrainerId = trainer.Id, StartTime = pastWeekday.AddHours(10), DurationMinutes = 45,
                Purpose = "Monthly progress review", Status = AppointmentStatus.Completed,
                ConsultationNotes = "Great progress: -2.8 kg. Increase Friday session to 40 minutes and keep protein at 120 g/day."
            },
            new Appointment
            {
                ClientId = client.Id, TrainerId = trainer.Id, StartTime = nextWeekday.AddDays(1).AddHours(11), DurationMinutes = 30,
                Purpose = "Running technique session", Status = AppointmentStatus.Approved
            },
            new Appointment
            {
                ClientId = client2.Id, TrainerId = trainer.Id, StartTime = nextWeekday.AddHours(14), DurationMinutes = 30,
                Purpose = "Discuss blood pressure readings and diet", Status = AppointmentStatus.Pending
            },
            new Appointment
            {
                ClientId = client3.Id, TrainerId = trainer2.Id, StartTime = today.AddDays(-3).AddHours(9), DurationMinutes = 30,
                Purpose = "Nutrition intro", Status = AppointmentStatus.Cancelled, CancelledBy = RoleNames.Client, StatusReason = "Work commitment"
            });

        // --- Feedback ---
        var recentWorkout = await db.WorkoutLogs.Where(w => w.ClientId == client.Id).OrderByDescending(w => w.LogDate).FirstOrDefaultAsync();
        db.TrainerFeedback.Add(new TrainerFeedback
        {
            TrainerId = trainer.Id, ClientId = client.Id, Target = FeedbackTarget.Workout, TargetId = recentWorkout?.Id,
            Message = "Nice consistency this week! Try adding 5 minutes to your jog.", CreatedAtUtc = DateTime.UtcNow.AddDays(-5)
        });
        db.TrainerFeedback.Add(new TrainerFeedback
        {
            TrainerId = trainer.Id, ClientId = client2.Id, Target = FeedbackTarget.General,
            Message = "Your sessions have dropped recently — let's find times that suit you.", CreatedAtUtc = DateTime.UtcNow.AddDays(-2)
        });

        // --- A few notifications so every notification centre has content ---
        db.Notifications.AddRange(
            new Notification { UserId = client.Id, Type = NotificationType.PlanUpdate, Title = "New workout plan: Fat Loss Foundations", Message = "Nimal Perera assigned you a new workout plan.", LinkUrl = "/Client/Plans", CreatedAtUtc = DateTime.UtcNow.AddDays(-42) },
            new Notification { UserId = client.Id, Type = NotificationType.Feedback, Title = "New feedback from Nimal Perera", Message = "Nice consistency this week! Try adding 5 minutes to your jog.", LinkUrl = "/Client/Feedback", CreatedAtUtc = DateTime.UtcNow.AddDays(-5) },
            new Notification { UserId = trainer.Id, Type = NotificationType.AppointmentUpdate, Title = "New appointment request", Message = "Ruwan Bandara requested a consultation.", LinkUrl = "/Appointments", CreatedAtUtc = DateTime.UtcNow.AddHours(-3) });

        await db.SaveChangesAsync();
        logger.LogWarning("Demo data created: trainer/Trainer@12345, client/Client@12345 (see README).");
    }

    private static async Task<IdentityUser?> CreateUserAsync(UserManager<IdentityUser> userManager, string userName, string email, string password, string role)
    {
        var user = new IdentityUser { UserName = userName, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded) return null;
        await userManager.AddToRoleAsync(user, role);
        return user;
    }
}
