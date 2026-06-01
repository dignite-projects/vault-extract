using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_FileOrigin_Indexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseDocuments_FileOrigin_BlobName",
                table: "PaperbaseDocuments",
                column: "FileOrigin_BlobName");

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseDocuments_FileOrigin_ContentHash",
                table: "PaperbaseDocuments",
                column: "FileOrigin_ContentHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperbaseDocuments_FileOrigin_BlobName",
                table: "PaperbaseDocuments");

            migrationBuilder.DropIndex(
                name: "IX_PaperbaseDocuments_FileOrigin_ContentHash",
                table: "PaperbaseDocuments");
        }
    }
}
