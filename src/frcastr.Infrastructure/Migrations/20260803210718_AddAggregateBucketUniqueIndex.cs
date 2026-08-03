using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAggregateBucketUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Databases written before the aggregation cutoffs were snapped to bucket boundaries
            // can hold more than one row per bucket, and the index below would fail against them —
            // taking the app down, because migrations run at startup. Keep the row covering the
            // most readings and drop the rest.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id], ROW_NUMBER() OVER (
                        PARTITION BY [ChannelName], [SourceId], [DeviceId], [Granularity], [PeriodStart]
                        ORDER BY [Count] DESC, [Id] ASC) AS rn
                    FROM [WeatherReadingAggregates])
                DELETE FROM [WeatherReadingAggregates]
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WeatherReadingAggregates_ChannelName_SourceId_DeviceId_Granularity_PeriodStart",
                table: "WeatherReadingAggregates",
                columns: new[] { "ChannelName", "SourceId", "DeviceId", "Granularity", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WeatherReadingAggregates_ChannelName_SourceId_DeviceId_Granularity_PeriodStart",
                table: "WeatherReadingAggregates");
        }
    }
}
