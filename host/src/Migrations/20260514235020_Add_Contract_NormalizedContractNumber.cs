using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Add_Contract_NormalizedContractNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedContractNumber",
                table: "PaperbaseContracts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            // 硬伤一 (Phase 1) best-effort backfill — handles ASCII-form contract numbers
            // ("HT-2024-001" -> "HT2024001"). Full Unicode normalization (full-width digits,
            // em-dashes, NFKC compatibility folding) requires CLR; rows containing such
            // content remain NULL NormalizedContractNumber and get populated on the next
            // AI extraction / user correction (Contract.ApplyFields / CorrectFields).
            // Operators with Unicode-heavy data can run a one-off admin job that re-saves
            // affected rows. SQL is SQL-92 compatible (SQL Server / PostgreSQL / SQLite).
            migrationBuilder.Sql(@"
UPDATE PaperbaseContracts
SET NormalizedContractNumber = UPPER(
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
        ContractNumber,
        '-', ''), '/', ''), '.', ''), '_', ''), '\', ''), ' ', ''), '	', '')
)
WHERE ContractNumber IS NOT NULL AND ContractNumber <> '';");

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseContracts_NormalizedContractNumber",
                table: "PaperbaseContracts",
                column: "NormalizedContractNumber",
                filter: "NormalizedContractNumber IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperbaseContracts_NormalizedContractNumber",
                table: "PaperbaseContracts");

            migrationBuilder.DropColumn(
                name: "NormalizedContractNumber",
                table: "PaperbaseContracts");
        }
    }
}
