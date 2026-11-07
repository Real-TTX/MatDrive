using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatDrive.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocationConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcceptAnyCertificate",
                table: "StorageLocation",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "StorageLocation",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivateKeyProtected",
                table: "StorageLocation",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Url",
                table: "StorageLocation",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseTls",
                table: "StorageLocation",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptAnyCertificate",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "PrivateKeyProtected",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "Url",
                table: "StorageLocation");

            migrationBuilder.DropColumn(
                name: "UseTls",
                table: "StorageLocation");
        }
    }
}
