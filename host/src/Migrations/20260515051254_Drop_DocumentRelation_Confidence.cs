using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Drop_DocumentRelation_Confidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "PaperbaseDocumentRelations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "PaperbaseDocumentRelations",
                type: "float",
                nullable: true);
        }
    }
}
