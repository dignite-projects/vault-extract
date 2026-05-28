using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Paperbase.Host.Migrations
{
    /// <inheritdoc />
    public partial class Merge_FieldDataType_Integer_Decimal_Into_Number : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 数据保全：Integer + Decimal 合并为 Number（decimal 存储）。#208 后 DataType 仅存于 FieldDefinition。──

            // 1) 历史 IntegerValue 迁入 DecimalValue（一行只存一个类型化值，故这些行 DecimalValue 必为空），再删列不丢数。
            migrationBuilder.Sql(
                "UPDATE [PaperbaseDocumentExtractedFields] SET [DecimalValue] = [IntegerValue] " +
                "WHERE [IntegerValue] IS NOT NULL AND [DecimalValue] IS NULL;");

            // 2) 重映射 FieldDefinition.DataType 枚举序号（连续化）：
            //    旧 String=0, Integer=1, Decimal=2, Boolean=3, Date=4, DateTime=5
            //    新 String=0, Number=1,            Boolean=2, Date=3, DateTime=4
            //    按源值升序逐条更新——每个新目标都小于其源值且源值递增，故不会被后续步骤二次命中。
            //    Integer(1)→Number(1) 与 String(0) 序号不变，无需 UPDATE。
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 1 WHERE [DataType] = 2;"); // Decimal → Number
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 2 WHERE [DataType] = 3;"); // Boolean
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 3 WHERE [DataType] = 4;"); // Date
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 4 WHERE [DataType] = 5;"); // DateTime

            migrationBuilder.DropIndex(
                name: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_IntegerValue_DocumentId",
                table: "PaperbaseDocumentExtractedFields");

            migrationBuilder.DropColumn(
                name: "IntegerValue",
                table: "PaperbaseDocumentExtractedFields");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IntegerValue",
                table: "PaperbaseDocumentExtractedFields",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperbaseDocumentExtractedFields_TenantId_FieldDefinitionId_IntegerValue_DocumentId",
                table: "PaperbaseDocumentExtractedFields",
                columns: new[] { "TenantId", "FieldDefinitionId", "IntegerValue", "DocumentId" });

            // 反向重映射 DataType（按源值降序，避免二次命中）。合并有损：原 Integer 已并入 Number、无法区分，
            // 一律还原为 Decimal（数值超集，语义不丢）。
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 5 WHERE [DataType] = 4;"); // DateTime
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 4 WHERE [DataType] = 3;"); // Date
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 3 WHERE [DataType] = 2;"); // Boolean
            migrationBuilder.Sql("UPDATE [PaperbaseFieldDefinitions] SET [DataType] = 2 WHERE [DataType] = 1;"); // Number → Decimal

            // 注：不反向回填 DecimalValue → IntegerValue——decimal 100.0 与整数 100 合并后不可区分，
            // 强行回填会臆造精度语义。Down 仅为开发期回滚结构；历史数值保留在 DecimalValue。
        }
    }
}
