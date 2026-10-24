using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HealthTrack.Data.Migrations
{
    /// <summary>
    /// Adds every HealthTrack module: plans, logs, measurements, appointments, feedback,
    /// notifications, audit log, settings, trainer availability and new profile columns.
    /// </summary>
    public partial class CompleteHealthTrackModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentAcceptedAtUtc",
                table: "ClientProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DailyCalorieTarget",
                table: "ClientProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MedicalNotes",
                table: "ClientProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfilePicturePath",
                table: "ClientProfiles",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetWeightKg",
                table: "ClientProfiles",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrainerId",
                table: "ClientProfiles",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAtUtc",
                table: "TrainerProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "TrainerProfiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentAcceptedAtUtc",
                table: "TrainerProfiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRejected",
                table: "TrainerProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProfilePicturePath",
                table: "TrainerProfiles",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "TrainerProfiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsSecurityEvent = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TitleTemplate = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    BodyTemplate = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    WorkoutReminders = table.Column<bool>(type: "bit", nullable: false),
                    MealReminders = table.Column<bool>(type: "bit", nullable: false),
                    AppointmentAlerts = table.Column<bool>(type: "bit", nullable: false),
                    PlanUpdates = table.Column<bool>(type: "bit", nullable: false),
                    FeedbackAlerts = table.Column<bool>(type: "bit", nullable: false),
                    AdherenceAlerts = table.Column<bool>(type: "bit", nullable: false),
                    SystemAlerts = table.Column<bool>(type: "bit", nullable: false),
                    ClientLogAlerts = table.Column<int>(type: "int", nullable: false),
                    EmailNotifications = table.Column<bool>(type: "bit", nullable: false),
                    WorkoutReminderTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    MealReminderTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LinkUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    DedupKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Goal = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Intensity = table.Column<int>(type: "int", nullable: false),
                    SessionsPerWeek = table.Column<int>(type: "int", nullable: false),
                    DurationWeeks = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsTemplate = table.Column<bool>(type: "bit", nullable: false),
                    IsSystemTemplate = table.Column<bool>(type: "bit", nullable: false),
                    IsFlagged = table.Column<bool>(type: "bit", nullable: false),
                    FlagReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutPlans_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "DietPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DailyCalorieTarget = table.Column<int>(type: "int", nullable: false),
                    ProteinTargetG = table.Column<int>(type: "int", nullable: false),
                    CarbsTargetG = table.Column<int>(type: "int", nullable: false),
                    FatTargetG = table.Column<int>(type: "int", nullable: false),
                    Guidelines = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedById = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsTemplate = table.Column<bool>(type: "bit", nullable: false),
                    IsSystemTemplate = table.Column<bool>(type: "bit", nullable: false),
                    IsFlagged = table.Column<bool>(type: "bit", nullable: false),
                    FlagReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DietPlans_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutExercises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkoutPlanId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    Sets = table.Column<int>(type: "int", nullable: false),
                    Reps = table.Column<int>(type: "int", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutExercises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutExercises_WorkoutPlans_WorkoutPlanId",
                        column: x => x.WorkoutPlanId,
                        principalTable: "WorkoutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DietMeals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DietPlanId = table.Column<int>(type: "int", nullable: false),
                    MealType = table.Column<int>(type: "int", nullable: false),
                    FoodItems = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Portion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Calories = table.Column<int>(type: "int", nullable: false),
                    ProteinG = table.Column<int>(type: "int", nullable: false),
                    CarbsG = table.Column<int>(type: "int", nullable: false),
                    FatG = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietMeals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DietMeals_DietPlans_DietPlanId",
                        column: x => x.DietPlanId,
                        principalTable: "DietPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AssignedById = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    WorkoutPlanId = table.Column<int>(type: "int", nullable: true),
                    DietPlanId = table.Column<int>(type: "int", nullable: true),
                    StartDate = table.Column<DateTime>(type: "date", nullable: false),
                    EndDate = table.Column<DateTime>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanAssignments_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_PlanAssignments_DietPlans_DietPlanId",
                        column: x => x.DietPlanId,
                        principalTable: "DietPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanAssignments_WorkoutPlans_WorkoutPlanId",
                        column: x => x.WorkoutPlanId,
                        principalTable: "WorkoutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    WorkoutPlanId = table.Column<int>(type: "int", nullable: true),
                    ExerciseType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Intensity = table.Column<int>(type: "int", nullable: false),
                    CaloriesBurned = table.Column<int>(type: "int", nullable: false),
                    LogDate = table.Column<DateTime>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutLogs_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkoutLogs_WorkoutPlans_WorkoutPlanId",
                        column: x => x.WorkoutPlanId,
                        principalTable: "WorkoutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MealLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DietPlanId = table.Column<int>(type: "int", nullable: true),
                    MealType = table.Column<int>(type: "int", nullable: false),
                    FoodItems = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Portion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Calories = table.Column<int>(type: "int", nullable: false),
                    ProteinG = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: false),
                    CarbsG = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: false),
                    FatG = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: false),
                    LogDate = table.Column<DateTime>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealLogs_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MealLogs_DietPlans_DietPlanId",
                        column: x => x.DietPlanId,
                        principalTable: "DietPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HealthMeasurements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    MeasuredOn = table.Column<DateTime>(type: "date", nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Bmi = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    BodyFatPercent = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    WaistCm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    SystolicBp = table.Column<int>(type: "int", nullable: true),
                    DiastolicBp = table.Column<int>(type: "int", nullable: true),
                    RestingHeartRate = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthMeasurements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthMeasurements_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrainerFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TrainerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Target = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<int>(type: "int", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainerFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainerFeedback_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrainerFeedback_AspNetUsers_TrainerId",
                        column: x => x.TrainerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TrainerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    StartTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProposedStartTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RescheduleRequestedBy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SuggestedAlternative = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CancelledBy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ConsultationNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_ClientId",
                        column: x => x.ClientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_TrainerId",
                        column: x => x.TrainerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "TrainerAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TrainerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    EndTime = table.Column<TimeSpan>(type: "time", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainerAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainerAvailabilities_TrainerProfiles_TrainerId",
                        column: x => x.TrainerId,
                        principalTable: "TrainerProfiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientProfiles_TrainerId",
                table: "ClientProfiles",
                column: "TrainerId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAtUtc",
                table: "AuditLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                table: "AuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_Key",
                table: "NotificationTemplates",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_DedupKey",
                table: "Notifications",
                columns: new[] { "UserId", "DedupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutPlans_CreatedById",
                table: "WorkoutPlans",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DietPlans_CreatedById",
                table: "DietPlans",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutExercises_WorkoutPlanId",
                table: "WorkoutExercises",
                column: "WorkoutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_DietMeals_DietPlanId",
                table: "DietMeals",
                column: "DietPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanAssignments_ClientId",
                table: "PlanAssignments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanAssignments_DietPlanId",
                table: "PlanAssignments",
                column: "DietPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanAssignments_WorkoutPlanId",
                table: "PlanAssignments",
                column: "WorkoutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutLogs_WorkoutPlanId",
                table: "WorkoutLogs",
                column: "WorkoutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutLogs_ClientId_LogDate",
                table: "WorkoutLogs",
                columns: new[] { "ClientId", "LogDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MealLogs_DietPlanId",
                table: "MealLogs",
                column: "DietPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_MealLogs_ClientId_LogDate",
                table: "MealLogs",
                columns: new[] { "ClientId", "LogDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthMeasurements_ClientId_MeasuredOn",
                table: "HealthMeasurements",
                columns: new[] { "ClientId", "MeasuredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainerFeedback_ClientId",
                table: "TrainerFeedback",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_TrainerFeedback_TrainerId",
                table: "TrainerFeedback",
                column: "TrainerId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ClientId",
                table: "Appointments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_TrainerId_StartTime",
                table: "Appointments",
                columns: new[] { "TrainerId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainerAvailabilities_TrainerId",
                table: "TrainerAvailabilities",
                column: "TrainerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientProfiles_AspNetUsers_TrainerId",
                table: "ClientProfiles",
                column: "TrainerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientProfiles_AspNetUsers_TrainerId",
                table: "ClientProfiles");

            migrationBuilder.DropTable(name: "TrainerAvailabilities");
            migrationBuilder.DropTable(name: "Appointments");
            migrationBuilder.DropTable(name: "TrainerFeedback");
            migrationBuilder.DropTable(name: "HealthMeasurements");
            migrationBuilder.DropTable(name: "MealLogs");
            migrationBuilder.DropTable(name: "WorkoutLogs");
            migrationBuilder.DropTable(name: "PlanAssignments");
            migrationBuilder.DropTable(name: "DietMeals");
            migrationBuilder.DropTable(name: "WorkoutExercises");
            migrationBuilder.DropTable(name: "DietPlans");
            migrationBuilder.DropTable(name: "WorkoutPlans");
            migrationBuilder.DropTable(name: "Notifications");
            migrationBuilder.DropTable(name: "NotificationPreferences");
            migrationBuilder.DropTable(name: "SystemSettings");
            migrationBuilder.DropTable(name: "NotificationTemplates");
            migrationBuilder.DropTable(name: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ClientProfiles_TrainerId",
                table: "ClientProfiles");

            migrationBuilder.DropColumn(name: "ConsentAcceptedAtUtc", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "DailyCalorieTarget", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "MedicalNotes", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "ProfilePicturePath", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "TargetWeightKg", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "TrainerId", table: "ClientProfiles");
            migrationBuilder.DropColumn(name: "ApprovedAtUtc", table: "TrainerProfiles");
            migrationBuilder.DropColumn(name: "Bio", table: "TrainerProfiles");
            migrationBuilder.DropColumn(name: "ConsentAcceptedAtUtc", table: "TrainerProfiles");
            migrationBuilder.DropColumn(name: "IsRejected", table: "TrainerProfiles");
            migrationBuilder.DropColumn(name: "ProfilePicturePath", table: "TrainerProfiles");
            migrationBuilder.DropColumn(name: "RejectionReason", table: "TrainerProfiles");
        }
    }
}
