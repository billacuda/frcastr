using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddForecastCacheIsHourly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ForecastCaches_SourceId_FetchedAt",
                table: "ForecastCaches");

            migrationBuilder.AddColumn<bool>(
                name: "IsHourly",
                table: "ForecastCaches",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastCaches_SourceId_IsHourly_FetchedAt",
                table: "ForecastCaches",
                columns: new[] { "SourceId", "IsHourly", "FetchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ForecastCaches_SourceId_IsHourly_FetchedAt",
                table: "ForecastCaches");

            migrationBuilder.DropColumn(
                name: "IsHourly",
                table: "ForecastCaches");

            migrationBuilder.CreateIndex(
                name: "IX_ForecastCaches_SourceId_FetchedAt",
                table: "ForecastCaches",
                columns: new[] { "SourceId", "FetchedAt" });
        }
    }
}
