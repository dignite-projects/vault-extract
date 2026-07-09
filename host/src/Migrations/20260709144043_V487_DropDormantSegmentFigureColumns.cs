using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations
{
    /// <inheritdoc />
    public partial class V487_DropDormantSegmentFigureColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FigureContentHash",
                table: "VaultDocumentSegments");

            migrationBuilder.DropColumn(
                name: "PageNumber",
                table: "VaultDocumentSegments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FigureContentHash",
                table: "VaultDocumentSegments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PageNumber",
                table: "VaultDocumentSegments",
                type: "int",
                nullable: true);
        }
    }
}
