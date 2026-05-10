using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Drop_ChatConversation_Scope_Columns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentTypeCode",
                table: "PaperbaseChatConversations");

            migrationBuilder.DropColumn(
                name: "MinScore",
                table: "PaperbaseChatConversations");

            migrationBuilder.DropColumn(
                name: "TopK",
                table: "PaperbaseChatConversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentTypeCode",
                table: "PaperbaseChatConversations",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinScore",
                table: "PaperbaseChatConversations",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TopK",
                table: "PaperbaseChatConversations",
                type: "int",
                nullable: true);
        }
    }
}
