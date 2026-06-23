using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Extract.Host.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUniqueKey",
                table: "ExtractFieldDefinitions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DuplicateAllowed",
                table: "ExtractDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FieldFingerprint",
                table: "ExtractDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtractDocuments_TenantId_DocumentTypeId_FieldFingerprint",
                table: "ExtractDocuments",
                columns: new[] { "TenantId", "DocumentTypeId", "FieldFingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExtractDocuments_TenantId_DocumentTypeId_FieldFingerprint",
                table: "ExtractDocuments");

            migrationBuilder.DropColumn(
                name: "IsUniqueKey",
                table: "ExtractFieldDefinitions");

            migrationBuilder.DropColumn(
                name: "DuplicateAllowed",
                table: "ExtractDocuments");

            migrationBuilder.DropColumn(
                name: "FieldFingerprint",
                table: "ExtractDocuments");
        }
    }
}
