using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.EntityFrameworkCore.ValueConverters;

namespace Dignite.Paperbase.EntityFrameworkCore;

public static class PaperbaseDbContextModelCreatingExtensions
{
    // 单一来源（#216）：与 PaperbaseHostDbContextModelSnapshot 中 EF 生成的同名 FK 锚定，
    // 防止后续重命名 source 侧 string 时静默触发 DropForeignKey + AddForeignKey 迁移。
    private const string DocumentPipelineRunDocumentForeignKey =
        "FK_PaperbaseDocumentPipelineRuns_PaperbaseDocuments_DocumentId";

    // ExportTemplate.Columns 是有序列定义数组，整体读写（无单列查询需求）——走 ABP 框架的
    // AbpJsonValueConverter<T> 整体序列化成大文本列（不绑 provider-specific native json：SQL Server 落
    // nvarchar(max)，其它 provider 自选）。ExportColumn 为 get-only 值对象，System.Text.Json 用其唯一带参
    // 构造函数反序列化（参数名匹配属性名）。SetColumns 保证 ≥1 列、只整体替换，故持久化值恒为非空 JSON 数组——
    // 转换器无需兜底 null/空串。对比 DocumentExtractedField：有查询诉求的字段值拆一等 child（Issue #206），
    // 无查询诉求的 JSON-like payload 留字符串、不绑 native json 类型——这是 Issue #206 cross-DB 清理确立的原则。
    private static readonly ValueConverter<IReadOnlyList<ExportColumn>, string> ExportColumnsConverter =
        new AbpJsonValueConverter<IReadOnlyList<ExportColumn>>();

    // ValueComparer 仍手写：ABP 未提供泛型 JSON 比较器，而 EF Core 对经 ValueConverter 转换的集合属性需要
    // 比较器做变更快照（否则退化为引用相等，且触发模型校验告警）。
    private static readonly ValueComparer<IReadOnlyList<ExportColumn>> ExportColumnsComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => JsonSerializer.Deserialize<List<ExportColumn>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new List<ExportColumn>());

    // Document.ExtractionMetadata（#210）：单个 typed 值对象（provider 名 + 可空归档 manifest），
    // 整体读写、无单列查询需求——同 ExportTemplate.Columns 走 AbpJsonValueConverter 序列化进大文本列（SQL Server → nvarchar(max)，
    // 其它 provider 自选），不绑 provider-specific native json（#206 cross-DB 原则）。可空：未提取 / 历史记录为 null（EF 在
    // 属性为 null 时存 DB null、不调转换器；非空时才序列化）。get-only 值对象由 System.Text.Json 经唯一带参构造反序列化（参数名匹配属性名）。
    private static readonly ValueConverter<DocumentTextExtractionMetadata?, string> ExtractionMetadataConverter =
        new AbpJsonValueConverter<DocumentTextExtractionMetadata?>();

    // 同 ExportColumnsComparer 手写：null-safe（EF 可能对 null 值取快照 / 比较）。converter / comparer 的泛型实参用可空
    // DocumentTextExtractionMetadata?，与可空属性匹配——否则 HasConversion 触发 CS8620 可空性差异警告。
    private static readonly ValueComparer<DocumentTextExtractionMetadata?> ExtractionMetadataComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<DocumentTextExtractionMetadata>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

    public static void ConfigurePaperbase(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Document>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "Documents", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.LifecycleStatus).IsRequired();
            b.Property(x => x.ReviewStatus).IsRequired();
            b.Property(x => x.ClassificationReason).HasMaxLength(DocumentConsts.MaxClassificationReasonLength);
            b.Property(x => x.Markdown);
            b.Property(x => x.Title).HasMaxLength(DocumentConsts.MaxTitleLength);

            // 字段架构 v2：系统通用字段平铺顶层 typed columns —— 真 pipeline 自动产物
            b.Property(x => x.Language).HasMaxLength(DocumentConsts.MaxLanguageLength);

            // 文本提取 provenance（#210）：provider 名 + 归档 manifest 整体序列化进 typed JSON 列（#206 cross-DB 原则）。
            b.Property(x => x.ExtractionMetadata)
                .HasConversion(ExtractionMetadataConverter, ExtractionMetadataComparer);

            // 字段架构 v2 / Issue #206：类型绑定字段值是聚合内 child 集合（DocumentExtractedField），
            // 不再是 Document 顶层的 native json 列。硬删 Document 时级联删除字段行。
            b.HasMany(x => x.ExtractedFieldValues)
                .WithOne()
                .HasForeignKey(f => f.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            b.OwnsOne(x => x.FileOrigin, fo =>
            {
                fo.Property(x => x.BlobName)
                    .IsRequired()
                    .HasMaxLength(FileOriginConsts.MaxBlobNameLength);
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

                fo.HasIndex(x => x.BlobName);
                fo.HasIndex(x => x.ContentHash);
            });

            // DocumentPipelineRun 的 FK + CASCADE 由子侧配置块（#216）显式声明。

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

            // 复合主键 (DocumentId, FieldDefinitionId, Order)（#207 + #212）：单值字段 Order 恒 0（同文档同字段唯一）；
            // 多值文本字段（AllowMultiple）一字段多行、Order 0/1/2…。reconcile 按 (FieldDefinitionId, Order) 原地替换不留重复行。
            // DocumentId 同时是指向 Document 聚合根的外键（identifying relationship）。
            b.HasKey(x => new { x.DocumentId, x.FieldDefinitionId, x.Order });

            // 字段类型不在本行持久化（#208）：由所引用 FieldDefinition.DataType 决定，读 / 导出路径已 load 该实体。
            // TextValue 限长 nvarchar(256)（#209）：类型绑定 文本字段是从 Markdown 抽取的结构化短值（姓名 / 编号 /
            // 币种 / 案由等），不承载长文本（长文本归 Document.Markdown）——限长换来它能进复合索引键，等值查询走 index seek。
            // 校验上限同源 DocumentExtractedFieldConsts.MaxTextValueLength（ExtractedFieldValueValidator 一并卡住）。
            b.Property(x => x.TextValue).HasMaxLength(DocumentExtractedFieldConsts.MaxTextValueLength);

            // LongTextValue 不限长（不调 HasMaxLength → provider 映射为 nvarchar(max) 等大文本类型，跨库可移植）：
            // 长内容载荷（摘要 / 描述等）。刻意不进下方任何复合索引、也无单列索引——长文本既进不了索引键，也无等值 / 区间查询语义
            // （ApplyFieldValueFilter 对 LongText loud fail）。App 层 MaxLongTextValueLength 仅作反滥用上限，不映射列长。
            b.Property(x => x.LongTextValue);

            // NumberValue 用 precision(38,6)（32 位整数 + 6 位小数）——覆盖任何现实抽取数值（金额 / 比率 / 百分比）
            // 而不溢出 / 截断；EF 默认 decimal(18,2) 会静默把 >2 位小数四舍五入，丢精度。precision 跨库可移植（provider 各自映射）。
            // 其余数字 / 日期值列由 provider 按 CLR 类型自动映射（long→bigint、DateOnly→date、DateTime→datetime2 等），不绑 provider-specific 类型。
            b.Property(x => x.NumberValue).HasPrecision(38, 6);

            // 字段定义内部关联（#207）：FK → FieldDefinition.Id，OnDelete Restrict——FieldDefinition 走软删除（不触发 FK），
            // 仅硬删仍被字段值引用的定义时由 DB 拒绝（保护历史字段值可解释）。EF 自动为该 FK 建索引。
            b.HasOne<FieldDefinition>()
                .WithMany()
                .HasForeignKey(x => x.FieldDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            // 字段值查询从 Documents 聚合根起手（按 TenantId + DocumentTypeId + 软删全局过滤收窄），再对 child 走
            // (FieldDefinitionId, typedValue) EXISTS。下列 (TenantId, FieldDefinitionId, <typedValue>, DocumentId) 复合索引
            // 支撑 文本等值 + Number / 日期字段的等值 + 范围（文本限长 256 后可进索引键，#209）。Boolean 不单建索引——
            // 基数仅 2，selectivity 太低，靠 (TenantId, FieldDefinitionId) 前缀分组 + 与其他字段 AND 收窄即可。
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.TextValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.NumberValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateTimeValue, x.DocumentId });
        });

        builder.Entity<DocumentPipelineRun>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentPipelineRuns", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.PipelineCode).IsRequired().HasMaxLength(DocumentPipelineRunConsts.MaxPipelineCodeLength);
            b.Property(x => x.StatusMessage).HasMaxLength(DocumentPipelineRunConsts.MaxStatusMessageLength);

            // #216：从 child entity 升为独立聚合根后，由子侧显式声明 FK + CASCADE。
            // HasConstraintName 显式锁定 FK 名 = 拆分前 EF 自动生成的名字，避免 HasMany→HasOne 迁移
            // 误判为"换 FK"产生 DropForeignKey+AddForeignKey 危险序列（EF Core issue #19137 家族）。
            b.HasOne<Document>()
                .WithMany()
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName(DocumentPipelineRunDocumentForeignKey);

            // #216 D2 / #239：UNIQUE 索引是 AttemptNumber 并发安全的唯一数据完整性保证，完全 DB 无关
            // （SqlServer / PostgreSQL / MySQL 一致）。并发撞同 (Doc, Pipeline, Attempt) 时 DB 抛
            // DbUpdateException，由 EfCoreDocumentPipelineRunRepository.InsertNewAttemptAsync 抓住该 provider
            // 无关异常类型（不嗅探 message / 错误码）→ 翻译成 RetryInProgress BusinessException。背景作业由
            // job 框架自动重试；HTTP 同步重试的 loser 拿到友好的"已有进行中的尝试"而非裸 500。
            b.HasIndex(x => new { x.DocumentId, x.PipelineCode, x.AttemptNumber })
                .IsUnique();
        });

        builder.Entity<DocumentType>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentTypes", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.TypeCode).IsRequired().HasMaxLength(DocumentTypeConsts.MaxTypeCodeLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(DocumentTypeConsts.MaxDisplayNameLength);
            // 可空：分类辅助说明（#262），NULL = 无说明。
            b.Property(x => x.Description).HasMaxLength(DocumentTypeConsts.MaxDescriptionLength);
            b.Property(x => x.ConfidenceThreshold).IsRequired();
            b.Property(x => x.Priority).IsRequired();

            // 唯一约束：(TenantId, TypeCode)；跨层可共用相同 TypeCode。软删过滤。
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
            // Prompt 选填（可空）：留空时 LLM 仅靠 Name + DataType 推断（FieldDefinition.NormalizePrompt 把空白收敛为 null）。
            b.Property(x => x.Prompt).IsRequired(false).HasMaxLength(FieldDefinitionConsts.MaxPromptLength);
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

            b.Property(x => x.Name).IsRequired().HasMaxLength(CabinetConsts.MaxNameLength);
            // 可空：选柜辅助说明（#273），NULL = 无说明。
            b.Property(x => x.Description).HasMaxLength(CabinetConsts.MaxDescriptionLength);

            // 唯一约束：(TenantId, Name)，层内不可重名（Host 与租户各自独立宇宙）。软删过滤。
            b.HasIndex(x => new { x.TenantId, x.Name })
                .IsUnique()
                .HasFilter("IsDeleted = 0");
        });
    }
}
