using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpxApi.Migrations
{
    /// <inheritdoc />
    public partial class AddIsFirstSyncComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFirstSyncComplete",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFirstSyncComplete",
                table: "Users");
        }
    }
}
