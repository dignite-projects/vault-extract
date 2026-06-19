using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.DocumentAI.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_Document_IsSegmented_Marker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSegmented",
                table: "DocAIDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSegmented",
                table: "DocAIDocuments");
        }
    }
}
