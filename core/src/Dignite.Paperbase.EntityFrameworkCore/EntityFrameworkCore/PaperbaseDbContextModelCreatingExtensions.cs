using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Chat;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Paperbase.EntityFrameworkCore;

public static class PaperbaseDbContextModelCreatingExtensions
{
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

            b.HasIndex(x => x.LifecycleStatus);
            b.HasIndex(x => x.ReviewStatus);
            b.HasIndex(x => x.DocumentTypeCode);
            b.HasIndex(x => x.CreationTime);

            // 每租户范围内按文件字节级 SHA-256 唯一；NULLS NOT DISTINCT 让单租户场景下 (NULL, hash) 也能正确判重。
            // 跨 owned-entity 索引 EF Core 不直接支持，由迁移文件用 raw SQL 创建唯一索引。
        });

        builder.Entity<DocumentPipelineRun>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentPipelineRuns", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.PipelineCode).IsRequired().HasMaxLength(DocumentPipelineRunConsts.MaxPipelineCodeLength);
            b.Property(x => x.StatusMessage).HasMaxLength(DocumentPipelineRunConsts.MaxStatusMessageLength);

            // 联合索引：(DocumentId, PipelineCode, AttemptNumber DESC)
            b.HasIndex(x => new { x.DocumentId, x.PipelineCode, x.AttemptNumber });
        });

        builder.Entity<DocumentRelation>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "DocumentRelations", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Description).IsRequired().HasMaxLength(DocumentRelationConsts.MaxDescriptionLength);
            b.Property(x => x.Source).IsRequired();

            b.HasIndex(x => x.SourceDocumentId);
            b.HasIndex(x => x.TargetDocumentId);
        });

        builder.Entity<ChatConversation>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "ChatConversations", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Title).IsRequired().HasMaxLength(ChatConsts.MaxTitleLength);
            // Issue #100: DocumentTypeCode / TopK / MinScore moved off the aggregate
            // (per-turn intent-driven scope replaces the old conversation-level pinning).
            // Migration Drop_ChatConversation_Scope_Columns drops the underlying columns.
            b.HasMany(x => x.Messages)
                .WithOne()
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.TenantId, x.CreatorId, x.CreationTime });
        });

        builder.Entity<ChatMessage>(b =>
        {
            b.ToTable(PaperbaseDbProperties.DbTablePrefix + "ChatMessages", PaperbaseDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Content).IsRequired().HasMaxLength(ChatConsts.MaxMessageLength);
            b.Property(x => x.CitationsJson);
            b.Property(x => x.IsDegraded).IsRequired();
            b.Property(x => x.Role).IsRequired();

            b.HasIndex(x => new { x.ConversationId, x.CreationTime });

            // Partial unique index: enforces per-conversation idempotency for user turns only.
            // ClientTurnId is null for assistant messages, so a full unique index would conflict.
            b.HasIndex(x => new { x.ConversationId, x.ClientTurnId })
                .IsUnique()
                .HasFilter("[ClientTurnId] IS NOT NULL");
        });

        // RAG chunk storage is external to the core EF model and is owned by the
        // configured provider. The open-source host uses Qdrant through
        // IDocumentKnowledgeIndex, so this DbContext stays free of vector-store
        // mappings and provider packages.
    }
}
