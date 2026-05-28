using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Paperbase.EntityFrameworkCore;

public static class PaperbaseDbContextModelCreatingExtensions
{
    // ExportTemplate.Columns 是有序列定义数组，整体读写（无单列查询需求）——走 ValueConverter 整体序列化
    // 成大文本列（不绑 provider-specific native json：SQL Server 落 nvarchar(max)，其它 provider 自选）。
    // ExportColumn 为 get-only 值对象，System.Text.Json 用其唯一带参构造函数反序列化（参数名匹配属性名）。
    // 对比 DocumentExtractedField：有查询诉求的字段值拆一等 child（Issue #206），无查询诉求的 JSON-like
    // payload 留字符串、不绑 native json 类型——这是 Issue #206 cross-DB 清理确立的原则。
    private static readonly ValueConverter<IReadOnlyList<ExportColumn>, string> ExportColumnsConverter =
        new(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrEmpty(v)
                ? new List<ExportColumn>()
                : (JsonSerializer.Deserialize<List<ExportColumn>>(v, (JsonSerializerOptions?)null) ?? new List<ExportColumn>()));

    private static readonly ValueComparer<IReadOnlyList<ExportColumn>> ExportColumnsComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => JsonSerializer.Deserialize<List<ExportColumn>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new List<ExportColumn>());

    public static void ConfigurePaperbase(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Document>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "Documents", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.OriginalFileBlobName).IsRequired().HasMaxLength(DocumentConsts.MaxOriginalFileBlobNameLength);
            b.Property(x => x.SourceType).IsRequired();
            b.Property(x => x.LifecycleStatus).IsRequired();
            b.Property(x => x.ReviewStatus).IsRequired();
            b.Property(x => x.ClassificationReason);
            b.Property(x => x.Markdown);
            b.Property(x => x.Title).HasMaxLength(DocumentConsts.MaxTitleLength);

            // 字段架构 v2：系统通用字段平铺顶层 typed columns —— 真 pipeline 自动产物
            b.Property(x => x.Language).HasMaxLength(DocumentConsts.MaxLanguageLength);

            // 字段架构 v2 / Issue #206：类型绑定字段值是聚合内 child 集合（DocumentExtractedField），
            // 不再是 Document 顶层的 native json 列。硬删 Document 时级联删除字段行。
            b.HasMany(x => x.ExtractedFieldValues)
                .WithOne()
                .HasForeignKey(f => f.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            b.OwnsOne(x => x.FileOrigin, fo =>
            {
                fo.Property(x => x.UploadedByUserName)
                    .IsRequired()
                    .HasMaxLength(FileOriginConsts.MaxUploadedByUserNameLength);
                fo.Property(x => x.OriginalFileName).HasMaxLength(FileOriginConsts.MaxOriginalFileNameLength);
                fo.Property(x => x.ContentType)
                    .IsRequired()
                    .HasMaxLength(FileOriginConsts.MaxContentTypeLength);
                fo.Property(x => x.ContentHash)
                    .IsRequired()
                    .HasMaxLength(FileOriginConsts.MaxContentHashLength);
            });

            b.HasMany(x => x.PipelineRuns)
                .WithOne()
                .HasForeignKey(pr => pr.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // 文件柜外键（#194）：可空 Guid 引用 Cabinet（reference-by-id，无导航属性）。
            // OnDelete NoAction——Cabinet 走软删除（行保留，不触发级联）；同时阻止误硬删仍被引用的柜。
            // EF Core 自动为该 FK 建索引，支撑列表按柜筛选。
            b.HasOne<Cabinet>()
                .WithMany()
                .HasForeignKey(x => x.CabinetId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.NoAction);

            // 文档类型内部关联（#207）：可空 Guid 引用 DocumentType（reference-by-id，无导航属性）。
            // OnDelete Restrict——DocumentType 走软删除（不触发 FK），仅硬删被引用类型时由 DB 拒绝；
            // soft-deleted 类型仍可被历史读路径穿透 join 到（DataFilter.Disable<ISoftDelete>），拿当前/最后已知 TypeCode。
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => x.LifecycleStatus);
            b.HasIndex(x => x.ReviewStatus);
            // 列表页高频路径：按 (租户层, 文档类型) 过滤。FK 另自动建 DocumentTypeId 单列索引（硬删 RESTRICT 检查用）。
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
            b.HasIndex(x => x.CreationTime);
        });

        builder.Entity<DocumentExtractedField>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentExtractedFields", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            // 复合主键 (DocumentId, FieldDefinitionId) = 字段集自然键（#207）：同文档同字段唯一，reconcile 整组替换不留重复行。
            // DocumentId 同时是指向 Document 聚合根的外键（identifying relationship）。
            b.HasKey(x => new { x.DocumentId, x.FieldDefinitionId });

            // 字段类型不在本行持久化（#208）：由所引用 FieldDefinition.DataType 决定，读 / 导出路径已 load 该实体。
            // StringValue 不限长（nvarchar(max) / text）：忠实存储，不截断；故不进索引键。
            // DecimalValue 用 precision(38,6)（32 位整数 + 6 位小数）——覆盖任何现实抽取数值（金额 / 比率 / 百分比）
            // 而不溢出 / 截断；EF 默认 decimal(18,2) 会静默把 >2 位小数四舍五入，丢精度。precision 跨库可移植（provider 各自映射）。
            // 其余数字 / 日期值列由 provider 按 CLR 类型自动映射（long→bigint、DateOnly→date、DateTime→datetime2 等），不绑 provider-specific 类型。
            b.Property(x => x.DecimalValue).HasPrecision(38, 6);

            // 字段定义内部关联（#207）：FK → FieldDefinition.Id，OnDelete Restrict——FieldDefinition 走软删除（不触发 FK），
            // 仅硬删仍被字段值引用的定义时由 DB 拒绝（保护历史字段值可解释）。EF 自动为该 FK 建索引。
            b.HasOne<FieldDefinition>()
                .WithMany()
                .HasForeignKey(x => x.FieldDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            // 字段值查询从 Documents 聚合根起手（按 TenantId + DocumentTypeId + 软删全局过滤收窄），再对 child 走
            // (FieldDefinitionId, typedValue) EXISTS。下列 (TenantId, FieldDefinitionId, <typedValue>, DocumentId) 复合索引
            // 支撑 Number / 日期字段的等值 + 范围；其 (TenantId, FieldDefinitionId) 前缀也覆盖 String / Boolean 等值收窄
            // （StringValue 是 nvarchar(max) 不能进索引键，靠前缀分组 + 与其他选择性字段 AND 收窄）。
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DecimalValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateTimeValue, x.DocumentId });
        });

        builder.Entity<DocumentPipelineRun>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentPipelineRuns", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.PipelineCode).IsRequired().HasMaxLength(DocumentPipelineRunConsts.MaxPipelineCodeLength);
            b.Property(x => x.StatusMessage).HasMaxLength(DocumentPipelineRunConsts.MaxStatusMessageLength);

            b.HasIndex(x => new { x.DocumentId, x.PipelineCode, x.AttemptNumber });
        });

        builder.Entity<DocumentType>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentTypes", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.TypeCode).IsRequired().HasMaxLength(DocumentTypeConsts.MaxTypeCodeLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(DocumentTypeConsts.MaxDisplayNameLength);
            b.Property(x => x.ConfidenceThreshold).IsRequired();
            b.Property(x => x.Priority).IsRequired();

            // 唯一约束：(TenantId, TypeCode)；Host (TenantId IS NULL) 和租户私有可共用相同 TypeCode
            // （但建议租户用 "tenant." 前缀避免歧义）。软删过滤。
            b.HasIndex(x => new { x.TenantId, x.TypeCode })
                .IsUnique()
                .HasFilter("IsDeleted = 0");
        });

        builder.Entity<FieldDefinition>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "FieldDefinitions", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxDisplayNameLength);
            b.Property(x => x.Prompt).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxPromptLength);
            b.Property(x => x.DataType).IsRequired();

            // 父文档类型内部关联（#207）：FK → DocumentType.Id，OnDelete Restrict（软删不触发，硬删被引用类型由 DB 拒绝）。
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // 唯一约束：每租户每类型下字段名唯一（#207：按 DocumentTypeId）。软删过滤。
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId, x.Name })
                .IsUnique()
                .HasFilter("IsDeleted = 0");

            // 非过滤索引：支撑回收站（DataFilter.Disable<ISoftDelete>）下按 (租户层, 类型) 列字段（unique 索引带 IsDeleted=0 filter 时用不上）。
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
        });

        builder.Entity<ExportTemplate>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "ExportTemplates", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(ExportTemplateConsts.MaxNameLength);
            b.Property(x => x.Format).IsRequired();

            // Columns 整体序列化进大文本列（无单列查询需求，不绑 provider-specific native json）——
            // EF Core provider 自选列类型（SQL Server → nvarchar(max)）。见文件首 ExportColumnsConverter 注释。
            b.Property(x => x.Columns)
                .HasConversion(ExportColumnsConverter, ExportColumnsComparer);

            // 限定文档类型内部关联（#207）：FK → DocumentType.Id（必填——收敛为 ExtractedField-only 列后模板必然类型绑定），
            // OnDelete Restrict（软删不触发，硬删被引用类型由 DB 拒绝）。列内 FieldDefinitionId 在序列化 JSON 内，无法建 FK——
            // 字段存在性由 AppService 在保存时校验，软删字段由读路径穿透 join 解析为"已归档字段"。
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // 唯一约束：(TenantId, Name)；跨层同名是合法的两行。软删过滤。
            b.HasIndex(x => new { x.TenantId, x.Name })
                .IsUnique()
                .HasFilter("IsDeleted = 0");
        });

        builder.Entity<Cabinet>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "Cabinets", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(CabinetConsts.MaxDisplayNameLength);

            // 唯一约束：(TenantId, DisplayName)，层内不可重名（Host 与租户各自独立宇宙）。软删过滤。
            b.HasIndex(x => new { x.TenantId, x.DisplayName })
                .IsUnique()
                .HasFilter("IsDeleted = 0");
        });
    }
}
