using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DashboardLayouts_OwnerId",
                table: "DashboardLayouts");

            migrationBuilder.AddColumn<string>(
                name: "DashboardName",
                table: "WidgetDefinitions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "DashboardLayouts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_WidgetDefinitions_DashboardName",
                table: "WidgetDefinitions",
                column: "DashboardName");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardLayouts_OwnerId_Name",
                table: "DashboardLayouts",
                columns: new[] { "OwnerId", "Name" },
                unique: true,
                filter: "[OwnerId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WidgetDefinitions_DashboardName",
                table: "WidgetDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_DashboardLayouts_OwnerId_Name",
                table: "DashboardLayouts");

            migrationBuilder.DropColumn(
                name: "DashboardName",
                table: "WidgetDefinitions");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "DashboardLayouts");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardLayouts_OwnerId",
                table: "DashboardLayouts",
                column: "OwnerId",
                filter: "[OwnerId] IS NOT NULL");
        }
    }
}
