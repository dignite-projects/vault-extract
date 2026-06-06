using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_CabinetDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "PaperbaseCabinets",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "PaperbaseCabinets");
        }
    }
}
