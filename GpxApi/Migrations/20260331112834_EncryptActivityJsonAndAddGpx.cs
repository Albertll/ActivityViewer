using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpxApi.Migrations
{
    /// <inheritdoc />
    public partial class EncryptActivityJsonAndAddGpx : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityJson",
                table: "Activities");

            migrationBuilder.AddColumn<byte[]>(
                name: "ActivityJsonIV",
                table: "Activities",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedActivityJson",
                table: "Activities",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedGpxData",
                table: "Activities",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "GpxIV",
                table: "Activities",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityJsonIV",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "EncryptedActivityJson",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "EncryptedGpxData",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "GpxIV",
                table: "Activities");

            migrationBuilder.AddColumn<string>(
                name: "ActivityJson",
                table: "Activities",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
