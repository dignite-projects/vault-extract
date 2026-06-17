using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Added_DocumentContainerMarkerAndConstituentKeyRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OriginFigureKey",
                table: "DocAIDocuments",
                newName: "OriginConstituentKey");

            migrationBuilder.RenameIndex(
                name: "IX_DocAIDocuments_OriginDocumentId_OriginFigureKey",
                table: "DocAIDocuments",
                newName: "IX_DocAIDocuments_OriginDocumentId_OriginConstituentKey");

            migrationBuilder.AddColumn<bool>(
                name: "IsContainer",
                table: "DocAIDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsContainer",
                table: "DocAIDocuments");

            migrationBuilder.RenameColumn(
                name: "OriginConstituentKey",
                table: "DocAIDocuments",
                newName: "OriginFigureKey");

            migrationBuilder.RenameIndex(
                name: "IX_DocAIDocuments_OriginDocumentId_OriginConstituentKey",
                table: "DocAIDocuments",
                newName: "IX_DocAIDocuments_OriginDocumentId_OriginFigureKey");
        }
    }
}
