using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Refactor_DocumentReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #284：ClassificationReason 收敛为 RejectionReason（拒绝理由专用）。EF 识别为列重命名，
            // Rejected 行的拒绝理由天然保留；非 Rejected 行的旧值实为已弃用的 AI 分类理由，下面清空。
            migrationBuilder.RenameColumn(
                name: "ClassificationReason",
                table: "PaperbaseDocuments",
                newName: "RejectionReason");

            // #284：新增待审原因集合（[Flags] int）。存量默认 0——不溯及历史（#284 决策）。
            migrationBuilder.AddColumn<int>(
                name: "ReviewReasons",
                table: "PaperbaseDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 数据回填（顺序敏感，均在列就位之后）：
            // 1) 非 Rejected 行（ReviewStatus<>30）的 RejectionReason 实为旧 AI 分类理由 → 清空
            //    （新模型只有 Rejected 行该有 RejectionReason）。
            migrationBuilder.Sql(
                "UPDATE [PaperbaseDocuments] SET [RejectionReason] = NULL WHERE [ReviewStatus] <> 30;");

            // 2) 旧 PendingReview(10) 拆为 处置=NotReviewed(0) + 原因=UnresolvedClassification(1)。
            //    Reviewed(20)→Confirmed(20)、Rejected(30)、None(0) 数值不变，无需迁移。
            migrationBuilder.Sql(
                "UPDATE [PaperbaseDocuments] SET [ReviewStatus] = 0, [ReviewReasons] = 1 WHERE [ReviewStatus] = 10;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 反向拆分：处置=NotReviewed(0) 且含 UnresolvedClassification 原因位 → 还原旧 PendingReview(10)。
            // 须在 DropColumn ReviewReasons 之前执行（之后读不到该列）。被清空的旧 AI 理由无法恢复（可接受）。
            migrationBuilder.Sql(
                "UPDATE [PaperbaseDocuments] SET [ReviewStatus] = 10 WHERE [ReviewStatus] = 0 AND ([ReviewReasons] & 1) = 1;");

            migrationBuilder.DropColumn(
                name: "ReviewReasons",
                table: "PaperbaseDocuments");

            migrationBuilder.RenameColumn(
                name: "RejectionReason",
                table: "PaperbaseDocuments",
                newName: "ClassificationReason");
        }
    }
}
