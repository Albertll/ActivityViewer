using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpxApi.Migrations
{
    /// <inheritdoc />
    public partial class SpeedColumnsAndUtcDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AverageSpeed",
                table: "Activities",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxSpeed",
                table: "Activities",
                type: "float",
                nullable: true);

            // Dotychczas StartDate zapisywano w czasie lokalnym (DateTime.Parse konwertował "Z" na Local).
            // Od tej migracji trzymamy UTC - przelicz istniejące wiersze.
            // Bez AT TIME ZONE (wymaga SQL 2016+, lokalny serwer to 2014): reguła DST Europy liczona ręcznie -
            // czas letni (UTC+2) od ostatniej niedzieli marca 02:00 do ostatniej niedzieli października 03:00,
            // poza tym zima (UTC+1). '19000107' to niedziela - stąd wyznaczanie ostatniej niedzieli miesiąca.
            migrationBuilder.Sql(
                @"UPDATE a SET StartDate = DATEADD(HOUR,
                      CASE WHEN a.StartDate >= DATEADD(HOUR, 2, dst.MarLastSun)
                            AND a.StartDate <  DATEADD(HOUR, 3, dst.OctLastSun)
                           THEN -2 ELSE -1 END,
                      a.StartDate)
                  FROM Activities a
                  CROSS APPLY (SELECT
                      DATEADD(DAY, -(DATEDIFF(DAY, '19000107', DATEFROMPARTS(YEAR(a.StartDate), 3, 31)) % 7),
                          CAST(DATEFROMPARTS(YEAR(a.StartDate), 3, 31) AS datetime2)) AS MarLastSun,
                      DATEADD(DAY, -(DATEDIFF(DAY, '19000107', DATEFROMPARTS(YEAR(a.StartDate), 10, 31)) % 7),
                          CAST(DATEFROMPARTS(YEAR(a.StartDate), 10, 31) AS datetime2)) AS OctLastSun
                  ) dst
                  WHERE a.StartDate IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AverageSpeed",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "MaxSpeed",
                table: "Activities");
        }
    }
}
