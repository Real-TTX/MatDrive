using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDrive.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficeBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfficeFormatsBinding",
                table: "SystemSetting",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfficeProblem",
                table: "SystemSetting",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfficeFormatsBinding",
                table: "SystemSetting");

            migrationBuilder.DropColumn(
                name: "OfficeProblem",
                table: "SystemSetting");
        }
    }
}
