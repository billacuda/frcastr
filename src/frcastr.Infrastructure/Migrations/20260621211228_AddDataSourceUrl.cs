using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace frcastr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Url",
                table: "DataSources",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Url",
                table: "DataSources");
        }
    }
}
