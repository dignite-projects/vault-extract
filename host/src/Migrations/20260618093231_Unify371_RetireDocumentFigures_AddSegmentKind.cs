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
