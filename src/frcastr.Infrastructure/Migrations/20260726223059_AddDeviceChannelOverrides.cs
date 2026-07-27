using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceChannelOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChannelOverrides",
                table: "Devices",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChannelOverrides",
                table: "Devices");
        }
    }
}
