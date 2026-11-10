using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDrive.Data.Migrations
{
    /// <inheritdoc />
    public partial class IdentityQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IdentityQuotaBytes",
                table: "User",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IdentityQuotaSet",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdentityQuotaBytes",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IdentityQuotaSet",
                table: "User");
        }
    }
}
