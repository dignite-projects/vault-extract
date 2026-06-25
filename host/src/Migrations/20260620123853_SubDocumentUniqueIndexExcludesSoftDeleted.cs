using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations
{
    /// <inheritdoc />
    public partial class SubDocumentUniqueIndexExcludesSoftDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExtractDocuments_OriginDocumentId_OriginConstituentKey",
                table: "ExtractDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractDocuments_OriginDocumentId_OriginConstituentKey",
                table: "ExtractDocuments",
                columns: new[] { "OriginDocumentId", "OriginConstituentKey" },
                unique: true,
                filter: "[OriginDocumentId] IS NOT NULL AND [IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExtractDocuments_OriginDocumentId_OriginConstituentKey",
                table: "ExtractDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractDocuments_OriginDocumentId_OriginConstituentKey",
                table: "ExtractDocuments",
                columns: new[] { "OriginDocumentId", "OriginConstituentKey" },
                unique: true,
                filter: "[OriginDocumentId] IS NOT NULL");
        }
    }
}
