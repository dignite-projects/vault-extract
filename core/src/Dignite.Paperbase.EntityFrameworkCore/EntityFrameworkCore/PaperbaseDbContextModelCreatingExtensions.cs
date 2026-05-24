using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Paperbase.EntityFrameworkCore;

public static class PaperbaseDbContextModelCreatingExtensions
{
    // EF Core 10 SQL Server provider 暂未原生支持 Dictionary<string, JsonElement> ↔ json 列直接映射
    // （会抛 "could not be mapped to the database type 'json'" 异常）。用 ValueConverter
    // 显式把 Dictionary 序列化为 JSON 字符串，存储到 native json 列上——数据 round-trip 工作；
    // LINQ 翻译能力受限（动态键查询要走 EF.Functions.JsonValue / JsonContains 而非
    // d.ExtractedFields["x"]）——动态字典与 LINQ 翻译模型互斥，不是 EF Core 版本能解决的限制。
    private static readonly ValueConverter<Dictionary<string, JsonElement>?, string?> ExtractedFieldsConverter =
        new(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null));

    private static readonly ValueComparer<Dictionary<string, JsonElement>?> ExtractedFieldsComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

    public static void ConfigurePaperbase(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Document>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "Documents", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.OriginalFileBlobName).IsRequired().HasMaxLength(DocumentConsts.MaxOriginalFileBlobNameLength);
            b.Property(x => x.SourceType).IsRequired();
            b.Property(x => x.DocumentTypeCode).HasMaxLength(DocumentConsts.MaxDocumentTypeCodeLength);
            b.Property(x => x.LifecycleStatus).IsRequired();
            b.Property(x => x.ReviewStatus).IsRequired();
            b.Property(x => x.ClassificationReason);
            b.Property(x => x.Markdown);
            b.Property(x => x.Title).HasMaxLength(DocumentConsts.MaxTitleLength);

            // 字段架构 v2：系统通用字段平铺顶层 typed columns —— 真 pipeline 自动产物
            b.Property(x => x.Language).HasMaxLength(DocumentConsts.MaxLanguageLength);
            b.Property(x => x.OcrConfidence);

            // 字段架构 v2：ExtractedFields 是动态 schema (Dictionary<string, JsonElement>)。
            // ValueConverter 序列化为 JSON 字符串，存到 SQL Server 2025 native json 列；
            // 旧 compat level 自动 fallback 到 nvarchar(max)。
            // 解读 X：源由 Document.TenantId 决定（Host 文档用 Host 字段定义；租户文档用租户字段定义）；
            // 两层 mutually exclusive 同一 Document 只有一层抽取结果，无分桶。
            b.Property(x => x.ExtractedFields)
                .HasColumnType("json")
                .HasConversion(ExtractedFieldsConverter, ExtractedFieldsComparer);

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

            b.HasIndex(x => x.LifecycleStatus);
            b.HasIndex(x => x.ReviewStatus);
            b.HasIndex(x => x.DocumentTypeCode);
            b.HasIndex(x => x.CreationTime);
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

            b.Property(x => x.DocumentTypeCode).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxDocumentTypeCodeLength);
            b.Property(x => x.Name).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxDisplayNameLength);
            b.Property(x => x.Prompt).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxPromptLength);
            b.Property(x => x.DataType).IsRequired();

            // 唯一约束：每租户每类型下字段名唯一
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeCode, x.Name })
                .IsUnique()
                .HasFilter("IsDeleted = 0");

            b.HasIndex(x => new { x.TenantId, x.DocumentTypeCode });
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
