using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations
{
    /// <inheritdoc />
    public partial class V680_AddNotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotifNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotificationName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EntityTypeName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotifNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotifNotificationSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NotificationNameKey = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    EntityTypeName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ScopeKey = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotifNotificationSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotifPushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    TokenKey = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CultureName = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotifPushDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotifUserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotifUserNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotifications_CreationTime",
                table: "NotifNotifications",
                column: "CreationTime");

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotifications_TenantId_CreationTime",
                table: "NotifNotifications",
                columns: new[] { "TenantId", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotifications_TenantId_NotificationName_CreationTime",
                table: "NotifNotifications",
                columns: new[] { "TenantId", "NotificationName", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotificationSubscriptions_TenantKey_NotificationNameKey_ScopeKey",
                table: "NotifNotificationSubscriptions",
                columns: new[] { "TenantKey", "NotificationNameKey", "ScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotificationSubscriptions_TenantKey_UserId_NotificationNameKey",
                table: "NotifNotificationSubscriptions",
                columns: new[] { "TenantKey", "UserId", "NotificationNameKey" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifNotificationSubscriptions_TenantKey_UserId_NotificationNameKey_ScopeKey",
                table: "NotifNotificationSubscriptions",
                columns: new[] { "TenantKey", "UserId", "NotificationNameKey", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotifPushDevices_TenantId_UserId",
                table: "NotifPushDevices",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifPushDevices_TokenKey",
                table: "NotifPushDevices",
                column: "TokenKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_State_CreationTime",
                table: "NotifUserNotifications",
                columns: new[] { "State", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_TenantId_NotificationId",
                table: "NotifUserNotifications",
                columns: new[] { "TenantId", "NotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_TenantId_State_CreationTime",
                table: "NotifUserNotifications",
                columns: new[] { "TenantId", "State", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_TenantId_UserId_NotificationName_State_CreationTime",
                table: "NotifUserNotifications",
                columns: new[] { "TenantId", "UserId", "NotificationName", "State", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_TenantId_UserId_State_CreationTime",
                table: "NotifUserNotifications",
                columns: new[] { "TenantId", "UserId", "State", "CreationTime" });

            migrationBuilder.CreateIndex(
                name: "IX_NotifUserNotifications_UserId_NotificationId",
                table: "NotifUserNotifications",
                columns: new[] { "UserId", "NotificationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotifNotifications");

            migrationBuilder.DropTable(
                name: "NotifNotificationSubscriptions");

            migrationBuilder.DropTable(
                name: "NotifPushDevices");

            migrationBuilder.DropTable(
                name: "NotifUserNotifications");
        }
    }
}
