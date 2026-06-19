using System.Collections.Generic;
using System.Text.Json;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.EntityFrameworkCore.ValueConverters;

namespace Dignite.DocumentAI.EntityFrameworkCore;

public static class DocumentAIDbContextModelCreatingExtensions
{
    // ExportTemplate.Columns is an ordered definition array that is read and written as a whole, with no per-column query requirement.
    // Use ABP's AbpJsonValueConverter<T> to serialize it into a large text column as a whole, without binding to provider-specific native JSON
    // (SQL Server maps to nvarchar(max); other providers choose their own mapping). ExportColumn is a get-only value object, and
    // System.Text.Json deserializes it through its single parameterized constructor, matching constructor parameter names to property names.
    // SetColumns guarantees >= 1 column and whole-set replacement only, so the persisted value is always a non-empty JSON array;
    // the converter does not need null / empty-string fallback. Contrast with DocumentExtractedField: field values with query requirements
    // become first-class children (Issue #206), while JSON-like payloads without query requirements stay as strings and do not bind to native JSON.
    // This is the principle established by the Issue #206 cross-DB cleanup.
    private static readonly ValueConverter<IReadOnlyList<ExportColumn>, string> ExportColumnsConverter =
        new AbpJsonValueConverter<IReadOnlyList<ExportColumn>>();

    // ValueComparer is still hand-written: ABP does not provide a generic JSON comparer, and EF Core needs a comparer
    // to snapshot changes for collection properties converted through ValueConverter. Otherwise it falls back to reference equality and triggers model validation warnings.
    private static readonly ValueComparer<IReadOnlyList<ExportColumn>> ExportColumnsComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => JsonSerializer.Deserialize<List<ExportColumn>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new List<ExportColumn>());

    // Document.ExtractionMetadata (#210): a single typed value object (provider name + nullable archived manifest).
    // It is read and written as a whole with no per-column query requirement. Like ExportTemplate.Columns, it uses
    // AbpJsonValueConverter to serialize into a large text column (SQL Server -> nvarchar(max); other providers choose their own mapping),
    // without binding to provider-specific native JSON (#206 cross-DB principle). Nullable: not extracted / historical records are null.
    // EF stores DB null and does not call the converter when the property is null; it serializes only non-null values. The get-only value object
    // is deserialized by System.Text.Json through its single parameterized constructor, matching constructor parameter names to property names.
    private static readonly ValueConverter<DocumentTextExtractionMetadata?, string> ExtractionMetadataConverter =
        new AbpJsonValueConverter<DocumentTextExtractionMetadata?>();

    // Hand-written like ExportColumnsComparer and null-safe, because EF may snapshot / compare null values.
    // The converter / comparer generic argument uses nullable DocumentTextExtractionMetadata? to match the nullable property;
    // otherwise HasConversion triggers CS8620 nullability mismatch warnings.
    private static readonly ValueComparer<DocumentTextExtractionMetadata?> ExtractionMetadataComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<DocumentTextExtractionMetadata>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

    public static void ConfigureDocumentAI(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Document>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "Documents", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.LifecycleStatus).IsRequired();
            // #284 two-axis model: disposition axis. This used to pin the old ReviewStatus column name; after the #295 squash rebuilt the schema, the column name follows the property name.
            b.Property(x => x.ReviewDisposition).IsRequired();
            // #284: review reason set ([Flags] int single column, cross-DB portable per #206) + independent rejection reason.
            b.Property(x => x.ReviewReasons).IsRequired();
            b.Property(x => x.RejectionReason).HasMaxLength(DocumentConsts.MaxRejectionReasonLength);
            b.Property(x => x.Markdown);
            b.Property(x => x.Title).HasMaxLength(DocumentConsts.MaxTitleLength);

            // Field architecture v2: system common fields are flattened as top-level typed columns: real automatic pipeline outputs.
            b.Property(x => x.Language).HasMaxLength(DocumentConsts.MaxLanguageLength);

            // #346 container marker: non-null bool, default false (generic truth-source column, not a business field).
            b.Property(x => x.IsContainer).IsRequired();

            // #377 segmentation-completed marker: non-null bool, default false. Internal pipeline state (the precise
            // resume gate for the unified sub-document pass); not exposed at the egress.
            b.Property(x => x.IsSegmented).IsRequired();

            // #306 / #346 Scenario B back-reference: content-derived key of the source constituent (= FileOrigin.ContentHash).
            b.Property(x => x.OriginConstituentKey).HasMaxLength(DocumentConsts.MaxOriginConstituentKeyLength);

            // Text extraction provenance (#210): provider name + archived manifest, serialized as a whole into a typed JSON column (#206 cross-DB principle).
            b.Property(x => x.ExtractionMetadata)
                .HasConversion(ExtractionMetadataConverter, ExtractionMetadataComparer);

            // Field architecture v2 / Issue #206: type-bound field values are an aggregate-internal child collection (DocumentExtractedField),
            // no longer a top-level native JSON column on Document. Hard-deleting a Document cascades field-row deletion.
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

            // The DocumentPipelineRun FK + CASCADE are declared explicitly in the child-side configuration block (#216).

            // Cabinet foreign key (#194): nullable Guid reference to Cabinet (reference-by-id, no navigation property).
            // OnDelete NoAction: Cabinet uses soft delete, so rows remain and no cascade is triggered; it also blocks accidental hard delete of a still-referenced cabinet.
            // EF Core automatically creates an index for this FK, supporting list filtering by cabinet.
            b.HasOne<Cabinet>()
                .WithMany()
                .HasForeignKey(x => x.CabinetId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.NoAction);

            // Internal document type association (#207): nullable Guid reference to DocumentType (reference-by-id, no navigation property).
            // OnDelete Restrict: DocumentType uses soft delete and does not trigger the FK; only hard-deleting a referenced type is rejected by the DB.
            // Soft-deleted types can still be joined through historical read paths (DataFilter.Disable<ISoftDelete>) to obtain the current / last-known TypeCode.
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => x.LifecycleStatus);
            b.HasIndex(x => x.ReviewDisposition);
            // High-traffic list-page path: filter by (tenant layer, document type). The FK also automatically creates a single-column DocumentTypeId index for hard-delete RESTRICT checks.
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
            b.HasIndex(x => x.CreationTime);

            // #306 / #346 Scenario B back-reference: derived documents only. A filtered UNIQUE index on
            // (OriginDocumentId, OriginConstituentKey) makes sub-document routing idempotent (one derived document
            // per source constituent — figure or Markdown slice; re-routing / retry never duplicate-spawn) and also
            // serves "list a source's derived documents" (OriginDocumentId is the leading column). Filtered to
            // non-null so normally-uploaded documents (both columns null) are exempt. The HasFilter clause is
            // SQL-Server-specific; the SQL-Server-only filtered-index portability limitation is intentionally
            // accepted for v0.2.0. No FK on OriginDocumentId: it is a soft provenance pointer, not a constraint,
            // so the derived document outlives the source (the source may be hard-deleted while derived ones remain).
            b.HasIndex(x => new { x.OriginDocumentId, x.OriginConstituentKey })
                .IsUnique()
                .HasFilter("[OriginDocumentId] IS NOT NULL");
        });

        builder.Entity<DocumentExtractedField>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "DocumentExtractedFields", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            // Composite primary key (DocumentId, FieldDefinitionId, Order) (#207 + #212): single-value fields always use Order 0, unique per document and field.
            // Multi-value text fields (AllowMultiple) use multiple rows per field with Order 0/1/2... Reconcile replaces in place by (FieldDefinitionId, Order), leaving no duplicates.
            // DocumentId is also the foreign key to the Document aggregate root (identifying relationship).
            b.HasKey(x => new { x.DocumentId, x.FieldDefinitionId, x.Order });

            // Field type is not persisted on this row (#208); it is determined by the referenced FieldDefinition.DataType, and read / export paths load that entity.
            // TextValue is limited to nvarchar(256) (#209): type-bound text fields are short structured values extracted from Markdown
            // (names, numbers, currencies, case titles, and similar), not long text payloads (long text belongs in Document.Markdown).
            // The length limit lets it participate in composite index keys so equality queries can use index seek.
            // The validation limit shares the same source, DocumentExtractedFieldConsts.MaxTextValueLength, and ExtractedFieldValueValidator enforces it too.
            b.Property(x => x.TextValue).HasMaxLength(DocumentExtractedFieldConsts.MaxTextValueLength);

            // LongTextValue has no mapped length limit (no HasMaxLength -> providers map to large text types such as nvarchar(max), cross-DB portable):
            // long content payloads such as summaries / descriptions. It intentionally does not participate in any composite index below and has no single-column index.
            // Long text cannot be an index key and has no equality / range query semantics (ApplyFieldValueFilter loud-fails for LongText).
            // The App-layer MaxLongTextValueLength is only an anti-abuse limit and is not mapped as column length.
            b.Property(x => x.LongTextValue);

            // NumberValue uses precision(38,6) (32 integer digits + 6 decimal digits), covering realistic extracted numbers
            // such as amounts, ratios, and percentages without overflow / truncation. EF's default decimal(18,2) silently rounds values with more than 2 decimals and loses precision.
            // Precision is cross-DB portable, with each provider mapping it appropriately.
            // Other numeric / date value columns are provider-mapped from CLR types (long -> bigint, DateOnly -> date, DateTime -> datetime2, and so on), without provider-specific type binding.
            b.Property(x => x.NumberValue).HasPrecision(38, 6);

            // Internal field definition association (#207): FK -> FieldDefinition.Id, OnDelete Restrict. FieldDefinition uses soft delete and does not trigger the FK.
            // Only hard-deleting a definition still referenced by field values is rejected by the DB, preserving historical field value explainability. EF automatically creates an index for this FK.
            b.HasOne<FieldDefinition>()
                .WithMany()
                .HasForeignKey(x => x.FieldDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            // Field value queries start from the Documents aggregate root (narrowed by TenantId + DocumentTypeId + soft-delete global filters),
            // then apply child EXISTS on (FieldDefinitionId, typedValue). The following (TenantId, FieldDefinitionId, <typedValue>, DocumentId)
            // composite indexes support text equality plus Number / date equality and range filters. Text can enter index keys after the 256-char limit (#209).
            // Boolean gets no dedicated index: cardinality is only 2 and selectivity is too low; the (TenantId, FieldDefinitionId) prefix grouping plus AND narrowing with other fields is enough.
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.TextValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.NumberValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldDefinitionId, x.DateTimeValue, x.DocumentId });
        });

        builder.Entity<DocumentPipelineRun>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "DocumentPipelineRuns", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.PipelineCode).IsRequired().HasMaxLength(DocumentPipelineRunConsts.MaxPipelineCodeLength);
            b.Property(x => x.StatusMessage).HasMaxLength(DocumentPipelineRunConsts.MaxStatusMessageLength);

            // #216: after promotion from child entity to independent aggregate root, declare FK + CASCADE explicitly on the child side.
            // HasConstraintName previously pinned the pre-split FK name to avoid dangerous Drop+Add sequences (EF Core issue #19137 family).
            // After the #295 squash rebuilt the schema, there is no old name to preserve, so the FK name returns to EF convention generation.
            b.HasOne<Document>()
                .WithMany()
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // #216 D2 / #239: the UNIQUE index is the only data-integrity guarantee for AttemptNumber concurrency safety and is fully DB-agnostic
            // (consistent across SqlServer / PostgreSQL / MySQL). When concurrent writers collide on the same (Doc, Pipeline, Attempt), the DB throws
            // DbUpdateException. EfCoreDocumentPipelineRunRepository.InsertNewAttemptAsync catches that provider-agnostic exception type
            // (without sniffing message / error code) and translates it to a RetryInProgress BusinessException. Background jobs are retried by
            // the job framework automatically; the loser of an HTTP synchronous retry gets a friendly "attempt already in progress" instead of a raw 500.
            b.HasIndex(x => new { x.DocumentId, x.PipelineCode, x.AttemptNumber })
                .IsUnique();
        });

        builder.Entity<DocumentSegment>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "DocumentSegments", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SegmentKey).IsRequired().HasMaxLength(DocumentSegmentConsts.MaxSegmentKeyLength);
            // SliceText is a Markdown slice used to seed the derived document (nvarchar(max), like Document.Markdown);
            // not indexed.
            b.Property(x => x.SliceText).IsRequired();
            b.Property(x => x.Ordinal).IsRequired();
            // #371: which span kind this segment was carved from (Text constituent vs embedded Figure); drives the
            // container→type retraction filter (#364). PageNumber is a nullable recovery anchor (page) for
            // Figure-kind rows, parsed from the [Image OCR p:N] sentinel; neither is indexed.
            b.Property(x => x.Kind).IsRequired();
            b.Property(x => x.Status).IsRequired();

            // #346: born-digital slice -> container Document, FK + CASCADE so hard-deleting the container removes
            // its segment rows (mirrors the #306 DocumentFigure child-side declaration). RoutedDocumentId is a
            // soft pointer to the spawned derived Document with NO FK constraint: the derived document is a peer
            // that must outlive the container, so it must not cascade from / be constrained by this table.
            b.HasOne<Document>()
                .WithMany()
                .HasForeignKey(x => x.SourceDocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Idempotent spawn: one slice per (source, slice-content-hash). A job retry that re-persists the same
            // slice collides here instead of duplicate-spawning downstream. Both columns are non-nullable, so this
            // is a plain (portable) unique index, not a filtered one.
            b.HasIndex(x => new { x.SourceDocumentId, x.SegmentKey })
                .IsUnique();

            // Concurrency guard (#346): one split per container. The LLM split is non-deterministic, so two
            // concurrent segmentation runs would otherwise produce different SegmentKeys and both commit (a double
            // split). Every split numbers its slices from Ordinal 0, so this unique index makes the second
            // committer collide on Ordinal 0 and roll back its whole insert — only one split survives; the loser
            // retries and resumes from the winner's persisted rows.
            b.HasIndex(x => new { x.SourceDocumentId, x.Ordinal })
                .IsUnique();
        });

        builder.Entity<DocumentType>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "DocumentTypes", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.TypeCode).IsRequired().HasMaxLength(DocumentTypeConsts.MaxTypeCodeLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(DocumentTypeConsts.MaxDisplayNameLength);
            // Nullable: classification helper description (#262), NULL = no description.
            b.Property(x => x.Description).HasMaxLength(DocumentTypeConsts.MaxDescriptionLength);
            b.Property(x => x.ConfidenceThreshold).IsRequired();
            b.Property(x => x.Priority).IsRequired();

            // Layer-scoped uniqueness on (TenantId, TypeCode) is enforced by DocumentTypeManager in the application/domain
            // layer (#304), not by a DB index. The previous soft-delete-filtered unique index relied on SQL Server's
            // "unique index treats NULL as equal" semantics for Host rows (TenantId IS NULL) plus a HasFilter("IsDeleted = 0")
            // literal — neither portable across providers (PostgreSQL defaults to NULLS DISTINCT, which would silently drop the
            // Host-layer guarantee). Dropping it makes the schema cross-DB by construction; the accepted tradeoff is a TOCTOU
            // race on these low-frequency admin-config entities. The same applies to FieldDefinition / ExportTemplate / Cabinet below.
        });

        builder.Entity<FieldDefinition>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "FieldDefinitions", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxNameLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(FieldDefinitionConsts.MaxDisplayNameLength);
            // Prompt is optional (nullable): when empty, the LLM infers from Name + DataType only. FieldDefinition.NormalizePrompt collapses whitespace to null.
            b.Property(x => x.Prompt).IsRequired(false).HasMaxLength(FieldDefinitionConsts.MaxPromptLength);
            b.Property(x => x.DataType).IsRequired();

            // Internal association to parent document type (#207): FK -> DocumentType.Id, OnDelete Restrict. Soft delete does not trigger it; hard-deleting a referenced type is rejected by the DB.
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // Layer-scoped uniqueness on (TenantId, DocumentTypeId, Name) is enforced by FieldDefinitionManager in the
            // application/domain layer (#304), not by a DB index — see the DocumentType block above for the cross-DB rationale.

            // Non-unique index: supports listing fields by (tenant layer, type), including trash-bin paths (DataFilter.Disable<ISoftDelete>).
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
        });

        builder.Entity<ExportTemplate>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "ExportTemplates", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(ExportTemplateConsts.MaxNameLength);
            b.Property(x => x.Format).IsRequired();

            // Columns are serialized as a whole into a large text column, with no per-column query requirement and no provider-specific native JSON binding.
            // The EF Core provider chooses the column type (SQL Server -> nvarchar(max)). See the ExportColumnsConverter comment at the top of this file.
            b.Property(x => x.Columns)
                .HasConversion(ExportColumnsConverter, ExportColumnsComparer);

            // Internal association to the constrained document type (#207): FK -> DocumentType.Id. Required because after convergence to ExtractedField-only columns,
            // templates are necessarily type-bound. OnDelete Restrict: soft delete does not trigger it; hard-deleting a referenced type is rejected by the DB.
            // FieldDefinitionId inside Columns lives in serialized JSON and cannot have an FK. Field existence is validated by AppService on save,
            // and soft-deleted fields are resolved by read paths through joins as "archived fields".
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // Layer-scoped uniqueness on (TenantId, Name) is enforced by ExportTemplateManager in the application/domain
            // layer (#304), not by a DB index — see the DocumentType block above for the cross-DB rationale.
        });

        builder.Entity<Cabinet>(b =>
        {
            b.ToTable(DocumentAIDbProperties.DbTablePrefix + "Cabinets", DocumentAIDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.Name).IsRequired().HasMaxLength(CabinetConsts.MaxNameLength);
            // Nullable: cabinet selection helper description (#273), NULL = no description.
            b.Property(x => x.Description).HasMaxLength(CabinetConsts.MaxDescriptionLength);

            // Layer-scoped uniqueness on (TenantId, Name) is enforced by CabinetManager in the application/domain layer
            // (#304), not by a DB index — see the DocumentType block above for the cross-DB rationale. Cabinet has no
            // restore path, so the check considers active rows only.
        });
    }
}
