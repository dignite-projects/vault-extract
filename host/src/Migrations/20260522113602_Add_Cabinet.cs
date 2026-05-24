using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_Cabinet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CabinetId",
                table: "PaperbaseDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaperbaseCabinets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperbaseCabinets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseDocuments_CabinetId",
                table: "PaperbaseDocuments",
                column: "CabinetId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseCabinets_TenantId_DisplayName",
                table: "PaperbaseCabinets",
                columns: new[] { "TenantId", "DisplayName" },
                unique: true,
                filter: "IsDeleted = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_PaperbaseDocuments_PaperbaseCabinets_CabinetId",
                table: "PaperbaseDocuments",
                column: "CabinetId",
                principalTable: "PaperbaseCabinets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaperbaseDocuments_PaperbaseCabinets_CabinetId",
                table: "PaperbaseDocuments");

            migrationBuilder.DropTable(
                name: "PaperbaseCabinets");

            migrationBuilder.DropIndex(
                name: "IX_PaperbaseDocuments_CabinetId",
                table: "PaperbaseDocuments");

            migrationBuilder.DropColumn(
                name: "CabinetId",
                table: "PaperbaseDocuments");
        }
    }
}
