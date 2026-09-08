using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpxApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StravaAthleteId = table.Column<long>(type: "bigint", nullable: false),
                    StravaName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EncryptedAccessToken = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TokenExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    StravaActivityId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Distance = table.Column<double>(type: "float", nullable: false),
                    MovingTime = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalElevationGain = table.Column<double>(type: "float", nullable: false),
                    ActivityJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityStreams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityRecordId = table.Column<int>(type: "int", nullable: false),
                    EncryptedData = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    IV = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityStreams_Activities_ActivityRecordId",
                        column: x => x.ActivityRecordId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Intervals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActivityRecordId = table.Column<int>(type: "int", nullable: false),
                    IntervalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Intervals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Intervals_Activities_ActivityRecordId",
                        column: x => x.ActivityRecordId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_UserId_StravaActivityId",
                table: "Activities",
                columns: new[] { "UserId", "StravaActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityStreams_ActivityRecordId",
                table: "ActivityStreams",
                column: "ActivityRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Intervals_ActivityRecordId",
                table: "Intervals",
                column: "ActivityRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_StravaAthleteId",
                table: "Users",
                column: "StravaAthleteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityStreams");

            migrationBuilder.DropTable(
                name: "Intervals");

            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
