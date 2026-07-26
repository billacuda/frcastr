using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WeatherChannelRecords_ChannelName",
                table: "WeatherChannelRecords");

            migrationBuilder.AddColumn<int>(
                name: "DeviceId",
                table: "WeatherReadings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeviceId",
                table: "WeatherReadingAggregates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeviceId",
                table: "WeatherChannelRecords",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceId = table.Column<int>(type: "int", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsOnline = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OfflineThresholdMinutes = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_DataSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherReadings_DeviceId_ChannelName_Timestamp",
                table: "WeatherReadings",
                columns: new[] { "DeviceId", "ChannelName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherReadingAggregates_DeviceId_ChannelName_Granularity_PeriodStart",
                table: "WeatherReadingAggregates",
                columns: new[] { "DeviceId", "ChannelName", "Granularity", "PeriodStart" });

            migrationBuilder.CreateIndex(
                name: "IX_WeatherChannelRecords_ChannelName_DeviceId",
                table: "WeatherChannelRecords",
                columns: new[] { "ChannelName", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherChannelRecords_DeviceId",
                table: "WeatherChannelRecords",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceId",
                table: "Devices",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_LastSeenAt",
                table: "Devices",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SourceId",
                table: "Devices",
                column: "SourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherChannelRecords_Devices_DeviceId",
                table: "WeatherChannelRecords",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherReadingAggregates_Devices_DeviceId",
                table: "WeatherReadingAggregates",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WeatherReadings_Devices_DeviceId",
                table: "WeatherReadings",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WeatherChannelRecords_Devices_DeviceId",
                table: "WeatherChannelRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherReadingAggregates_Devices_DeviceId",
                table: "WeatherReadingAggregates");

            migrationBuilder.DropForeignKey(
                name: "FK_WeatherReadings_Devices_DeviceId",
                table: "WeatherReadings");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_WeatherReadings_DeviceId_ChannelName_Timestamp",
                table: "WeatherReadings");

            migrationBuilder.DropIndex(
                name: "IX_WeatherReadingAggregates_DeviceId_ChannelName_Granularity_PeriodStart",
                table: "WeatherReadingAggregates");

            migrationBuilder.DropIndex(
                name: "IX_WeatherChannelRecords_ChannelName_DeviceId",
                table: "WeatherChannelRecords");

            migrationBuilder.DropIndex(
                name: "IX_WeatherChannelRecords_DeviceId",
                table: "WeatherChannelRecords");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "WeatherReadings");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "WeatherReadingAggregates");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "WeatherChannelRecords");

            migrationBuilder.CreateIndex(
                name: "IX_WeatherChannelRecords_ChannelName",
                table: "WeatherChannelRecords",
                column: "ChannelName",
                unique: true);
        }
    }
}
