using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations
{
    /// <inheritdoc />
    public partial class V481_FileOriginRequired_SpawnIdempotencyToLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #481 tripwire (sanctioned one-off hand edit, no model impact - approved 2026-07-09; see the
            // ef-migration-safety review): the scaffolded ALTER COLUMN ... NOT NULL steps below carry defaultValue
            // coercions that silently rewrite any surviving NULL FileOrigin into '' / 0 instead of failing. The
            // one-time backfill script (PR deploy notes) must run first; this guard turns skipping it into a loud,
            // transactional failure in every environment this migration ever replays against.
            migrationBuilder.Sql(
                @"IF EXISTS (SELECT 1 FROM [VaultDocuments] WHERE [FileOrigin_BlobName] IS NULL OR [FileOrigin_UploadedByUserName] IS NULL OR [FileOrigin_ContentType] IS NULL OR [FileOrigin_ContentHash] IS NULL OR [FileOrigin_FileSize] IS NULL)
    THROW 50481, N'#481: NULL FileOrigin rows remain - run the pre-apply backfill script (PR deploy notes) before applying this migration.', 1;");

            migrationBuilder.DropIndex(
                name: "IX_VaultDocuments_OriginDocumentId_OriginConstituentKey",
                table: "VaultDocuments");

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_UploadedByUserName",
                table: "VaultDocuments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "FileOrigin_FileSize",
                table: "VaultDocuments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_ContentType",
                table: "VaultDocuments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_ContentHash",
                table: "VaultDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_BlobName",
                table: "VaultDocuments",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocuments_OriginDocumentId",
                table: "VaultDocuments",
                column: "OriginDocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultDocuments_OriginDocumentId",
                table: "VaultDocuments");

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_UploadedByUserName",
                table: "VaultDocuments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<long>(
                name: "FileOrigin_FileSize",
                table: "VaultDocuments",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_ContentType",
                table: "VaultDocuments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_ContentHash",
                table: "VaultDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "FileOrigin_BlobName",
                table: "VaultDocuments",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);

            migrationBuilder.CreateIndex(
                name: "IX_VaultDocuments_OriginDocumentId_OriginConstituentKey",
                table: "VaultDocuments",
                columns: new[] { "OriginDocumentId", "OriginConstituentKey" },
                unique: true,
                filter: "[OriginDocumentId] IS NOT NULL AND [IsDeleted] = 0");
        }
    }
}
