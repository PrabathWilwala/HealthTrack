using System.Reflection;
using HealthTrack.Infrastructure;
using HealthTrack.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HealthTrack.Data
{
    // EF Core database context: ASP.NET Core Identity tables plus all HealthTrack tables.
    public class ApplicationDbContext : IdentityDbContext
    {
        // Data Protection purpose string used for encrypting health data at rest.
        public const string HealthDataProtectionPurpose = "HealthTrack.HealthData.v1";

        private readonly IDataProtector _healthDataProtector;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            IDataProtectionProvider dataProtectionProvider)
            : base(options)
        {
            _healthDataProtector = dataProtectionProvider.CreateProtector(HealthDataProtectionPurpose);
        }

        public DbSet<ClientProfile> ClientProfiles => Set<ClientProfile>();
        public DbSet<TrainerProfile> TrainerProfiles => Set<TrainerProfile>();
        public DbSet<TrainerAvailability> TrainerAvailabilities => Set<TrainerAvailability>();
        public DbSet<WorkoutPlan> WorkoutPlans => Set<WorkoutPlan>();
        public DbSet<WorkoutExercise> WorkoutExercises => Set<WorkoutExercise>();
        public DbSet<DietPlan> DietPlans => Set<DietPlan>();
        public DbSet<DietMeal> DietMeals => Set<DietMeal>();
        public DbSet<PlanAssignment> PlanAssignments => Set<PlanAssignment>();
        public DbSet<WorkoutLog> WorkoutLogs => Set<WorkoutLog>();
        public DbSet<MealLog> MealLogs => Set<MealLog>();
        public DbSet<HealthMeasurement> HealthMeasurements => Set<HealthMeasurement>();
        public DbSet<TrainerFeedback> TrainerFeedback => Set<TrainerFeedback>();
        public DbSet<Appointment> Appointments => Set<Appointment>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
        public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ---------- Profiles ----------
            builder.Entity<ClientProfile>(entity =>
            {
                entity.HasKey(profile => profile.UserId);
                entity.Property(profile => profile.UserId).HasMaxLength(450);
                entity.Property(profile => profile.FullName).HasMaxLength(100).IsRequired();
                entity.Property(profile => profile.Gender).HasMaxLength(30).IsRequired();
                entity.Property(profile => profile.HeightCm).HasPrecision(5, 2);
                entity.Property(profile => profile.WeightKg).HasPrecision(5, 2);
                entity.Property(profile => profile.TargetWeightKg).HasPrecision(5, 2);
                entity.Property(profile => profile.TrainerId).HasMaxLength(450);
                entity.Property(profile => profile.ProfilePicturePath).HasMaxLength(260);
                entity.HasIndex(profile => profile.TrainerId);
                entity.HasOne<IdentityUser>()
                    .WithOne()
                    .HasForeignKey<ClientProfile>(profile => profile.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                // The assigned trainer. NoAction avoids multiple cascade paths in SQL Server;
                // the application clears this column when a trainer account is deleted.
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(profile => profile.TrainerId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            builder.Entity<TrainerProfile>(entity =>
            {
                entity.HasKey(profile => profile.UserId);
                entity.Property(profile => profile.UserId).HasMaxLength(450);
                entity.Property(profile => profile.FullName).HasMaxLength(100).IsRequired();
                entity.Property(profile => profile.Qualifications).HasMaxLength(500).IsRequired();
                entity.Property(profile => profile.Specializations).HasMaxLength(500).IsRequired();
                entity.Property(profile => profile.Certifications).HasMaxLength(1000);
                entity.Property(profile => profile.Bio).HasMaxLength(1000);
                entity.Property(profile => profile.ProfilePicturePath).HasMaxLength(260);
                entity.Property(profile => profile.RejectionReason).HasMaxLength(500);
                entity.HasOne<IdentityUser>()
                    .WithOne()
                    .HasForeignKey<TrainerProfile>(profile => profile.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(profile => profile.Availability)
                    .WithOne()
                    .HasForeignKey(slot => slot.TrainerId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<TrainerAvailability>(entity =>
            {
                entity.Property(slot => slot.TrainerId).HasMaxLength(450).IsRequired();
            });

            // ---------- Plans ----------
            builder.Entity<WorkoutPlan>(entity =>
            {
                entity.Property(plan => plan.Title).HasMaxLength(120).IsRequired();
                entity.Property(plan => plan.Description).HasMaxLength(1000);
                entity.Property(plan => plan.Goal).HasMaxLength(200);
                entity.Property(plan => plan.FlagReason).HasMaxLength(500);
                entity.Property(plan => plan.CreatedById).HasMaxLength(450);
                entity.HasIndex(plan => plan.CreatedById);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(plan => plan.CreatedById)
                    .OnDelete(DeleteBehavior.NoAction);
                entity.HasMany(plan => plan.Exercises)
                    .WithOne()
                    .HasForeignKey(exercise => exercise.WorkoutPlanId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<WorkoutExercise>(entity =>
            {
                entity.Property(exercise => exercise.Name).HasMaxLength(120).IsRequired();
                entity.Property(exercise => exercise.Notes).HasMaxLength(300);
            });

            builder.Entity<DietPlan>(entity =>
            {
                entity.Property(plan => plan.Title).HasMaxLength(120).IsRequired();
                entity.Property(plan => plan.Description).HasMaxLength(1000);
                entity.Property(plan => plan.Guidelines).HasMaxLength(1000);
                entity.Property(plan => plan.FlagReason).HasMaxLength(500);
                entity.Property(plan => plan.CreatedById).HasMaxLength(450);
                entity.HasIndex(plan => plan.CreatedById);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(plan => plan.CreatedById)
                    .OnDelete(DeleteBehavior.NoAction);
                entity.HasMany(plan => plan.Meals)
                    .WithOne()
                    .HasForeignKey(meal => meal.DietPlanId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<DietMeal>(entity =>
            {
                entity.Property(meal => meal.FoodItems).HasMaxLength(300).IsRequired();
                entity.Property(meal => meal.Portion).HasMaxLength(100);
            });

            builder.Entity<PlanAssignment>(entity =>
            {
                entity.Property(a => a.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(a => a.AssignedById).HasMaxLength(450);
                entity.Property(a => a.StartDate).HasColumnType("date");
                entity.Property(a => a.EndDate).HasColumnType("date");
                entity.HasIndex(a => a.ClientId);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(a => a.ClientId)
                    .OnDelete(DeleteBehavior.NoAction);
                entity.HasOne(a => a.WorkoutPlan)
                    .WithMany(plan => plan.Assignments)
                    .HasForeignKey(a => a.WorkoutPlanId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(a => a.DietPlan)
                    .WithMany(plan => plan.Assignments)
                    .HasForeignKey(a => a.DietPlanId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ---------- Client logs ----------
            builder.Entity<WorkoutLog>(entity =>
            {
                entity.Property(log => log.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(log => log.ExerciseType).HasMaxLength(120).IsRequired();
                entity.Property(log => log.Notes).HasMaxLength(500);
                entity.Property(log => log.LogDate).HasColumnType("date");
                entity.HasIndex(log => new { log.ClientId, log.LogDate });
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(log => log.ClientId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(log => log.WorkoutPlan)
                    .WithMany()
                    .HasForeignKey(log => log.WorkoutPlanId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<MealLog>(entity =>
            {
                entity.Property(log => log.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(log => log.FoodItems).HasMaxLength(300).IsRequired();
                entity.Property(log => log.Portion).HasMaxLength(100);
                entity.Property(log => log.Notes).HasMaxLength(500);
                entity.Property(log => log.ProteinG).HasPrecision(6, 1);
                entity.Property(log => log.CarbsG).HasPrecision(6, 1);
                entity.Property(log => log.FatG).HasPrecision(6, 1);
                entity.Property(log => log.LogDate).HasColumnType("date");
                entity.HasIndex(log => new { log.ClientId, log.LogDate });
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(log => log.ClientId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(log => log.DietPlan)
                    .WithMany()
                    .HasForeignKey(log => log.DietPlanId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<HealthMeasurement>(entity =>
            {
                entity.Property(m => m.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(m => m.MeasuredOn).HasColumnType("date");
                entity.Property(m => m.WeightKg).HasPrecision(5, 2);
                entity.Property(m => m.Bmi).HasPrecision(5, 2);
                entity.Property(m => m.BodyFatPercent).HasPrecision(4, 1);
                entity.Property(m => m.WaistCm).HasPrecision(5, 1);
                entity.HasIndex(m => new { m.ClientId, m.MeasuredOn });
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(m => m.ClientId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<TrainerFeedback>(entity =>
            {
                entity.Property(f => f.TrainerId).HasMaxLength(450).IsRequired();
                entity.Property(f => f.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(f => f.Message).HasMaxLength(1000).IsRequired();
                entity.HasIndex(f => f.ClientId);
                entity.HasIndex(f => f.TrainerId);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(f => f.ClientId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(f => f.TrainerId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            // ---------- Appointments ----------
            builder.Entity<Appointment>(entity =>
            {
                entity.Property(a => a.ClientId).HasMaxLength(450).IsRequired();
                entity.Property(a => a.TrainerId).HasMaxLength(450).IsRequired();
                entity.Property(a => a.Purpose).HasMaxLength(300).IsRequired();
                entity.Property(a => a.RescheduleRequestedBy).HasMaxLength(20);
                entity.Property(a => a.StatusReason).HasMaxLength(500);
                entity.Property(a => a.SuggestedAlternative).HasMaxLength(200);
                entity.Property(a => a.CancelledBy).HasMaxLength(20);
                entity.HasIndex(a => new { a.TrainerId, a.StartTime });
                entity.HasIndex(a => a.ClientId);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(a => a.ClientId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(a => a.TrainerId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            // ---------- Notifications ----------
            builder.Entity<Notification>(entity =>
            {
                entity.Property(n => n.UserId).HasMaxLength(450).IsRequired();
                entity.Property(n => n.Title).HasMaxLength(150).IsRequired();
                entity.Property(n => n.Message).HasMaxLength(1000).IsRequired();
                entity.Property(n => n.LinkUrl).HasMaxLength(300);
                entity.Property(n => n.DedupKey).HasMaxLength(150);
                entity.HasIndex(n => new { n.UserId, n.IsRead });
                entity.HasIndex(n => new { n.UserId, n.DedupKey });
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(n => n.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<NotificationPreference>(entity =>
            {
                entity.HasKey(p => p.UserId);
                entity.Property(p => p.UserId).HasMaxLength(450);
                entity.HasOne<IdentityUser>()
                    .WithOne()
                    .HasForeignKey<NotificationPreference>(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<NotificationTemplate>(entity =>
            {
                entity.Property(t => t.Key).HasMaxLength(60).IsRequired();
                entity.Property(t => t.Name).HasMaxLength(100).IsRequired();
                entity.Property(t => t.TitleTemplate).HasMaxLength(150).IsRequired();
                entity.Property(t => t.BodyTemplate).HasMaxLength(1000).IsRequired();
                entity.HasIndex(t => t.Key).IsUnique();
            });

            // ---------- System ----------
            builder.Entity<AuditLog>(entity =>
            {
                entity.Property(a => a.UserId).HasMaxLength(450);
                entity.Property(a => a.UserName).HasMaxLength(256);
                entity.Property(a => a.Category).HasMaxLength(50).IsRequired();
                entity.Property(a => a.Action).HasMaxLength(100).IsRequired();
                entity.Property(a => a.Details).HasMaxLength(1000);
                entity.Property(a => a.IpAddress).HasMaxLength(64);
                entity.HasIndex(a => a.CreatedAtUtc);
                entity.HasIndex(a => a.UserId);
            });

            builder.Entity<SystemSetting>(entity =>
            {
                entity.HasKey(s => s.Key);
                entity.Property(s => s.Key).HasMaxLength(100);
                entity.Property(s => s.Value).HasMaxLength(500).IsRequired();
            });

            ApplyEncryption(builder);
        }

        // Finds every string property marked with [Encrypted] and attaches the encrypting converter.
        private void ApplyEncryption(ModelBuilder builder)
        {
            var converter = new EncryptedStringConverter(_healthDataProtector);

            foreach (var entityType in builder.Model.GetEntityTypes().ToList())
            {
                var clrType = entityType.ClrType;
                foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.PropertyType == typeof(string) &&
                        property.GetCustomAttribute<EncryptedAttribute>() != null)
                    {
                        builder.Entity(clrType).Property(property.Name).HasConversion(converter);
                    }
                }
            }
        }
    }
}
