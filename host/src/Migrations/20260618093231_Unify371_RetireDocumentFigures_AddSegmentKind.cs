using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Unify371_RetireDocumentFigures_AddSegmentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #374 (accepted, ops note): dropping this table orphans the #306 figure-crop blobs persisted at
            // figures/{documentId}/{contentHash} — DocumentFigure.CropBlobName was the only handle that
            // PermanentDeleteAsync used to reclaim them, and it is gone with the rows. The leak is bounded and
            // NON-GROWING (the unified pass #371 spawns TEXT-ONLY figure sub-documents and never writes new crops),
            // and the blast radius is ~0 (the #306 path only reached main for v0.2.0, so no production deployment
            // wrote any). A complete reclaim is impossible here anyway: the crop blob names live only on these rows,
            // and IBlobContainer has no list API, so once this DropTable runs the names are unrecoverable. Decision:
            // ACCEPT the leak rather than couple blob IO into a migration for ~0 data. Any environment that DID run
            // the #306 path (dev/staging) should one-off delete the "figures/" blob-store prefix manually.
            migrationBuilder.DropTable(
                name: "DocAIDocumentFigures");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "DocAIDocumentSegments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PageNumber",
                table: "DocAIDocumentSegments",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "DocAIDocumentSegments");

            migrationBuilder.DropColumn(
                name: "PageNumber",
                table: "DocAIDocumentSegments");

            migrationBuilder.CreateTable(
                name: "DocAIDocumentFigures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CropBlobName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: true),
                    RoutedDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Transcription = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocAIDocumentFigures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocAIDocumentFigures_DocAIDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "DocAIDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocAIDocumentFigures_SourceDocumentId_ContentHash",
                table: "DocAIDocumentFigures",
                columns: new[] { "SourceDocumentId", "ContentHash" },
                unique: true);
        }
    }
}
