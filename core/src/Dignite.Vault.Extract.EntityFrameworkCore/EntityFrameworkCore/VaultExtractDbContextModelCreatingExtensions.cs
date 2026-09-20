using System.Collections.Generic;
using System.Text.Json;
using Dignite.Abp.FlexFields.EntityFrameworkCore;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.EntityFrameworkCore.ValueConverters;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

public static class VaultExtractDbContextModelCreatingExtensions
{
    // Document.ExtractionMetadata (#210): a single typed value object (provider name + nullable archived manifest).
    // It is read and written as a whole with no per-column query requirement, so it uses ABP's AbpJsonValueConverter<T>
    // to serialize into a large text column (SQL Server -> nvarchar(max); other providers choose their own mapping),
    // without binding to provider-specific native JSON. Contrast with the FlexFields value bag below: field values with
    // query requirements are decomposed into the derived DocumentFlexFieldIndex (Issue #206 / #558), while JSON-like
    // payloads without query requirements stay as strings and do not bind to native JSON — the principle established by
    // the Issue #206 cross-DB cleanup.
    // Nullable: not extracted / historical records are null.
    // EF stores DB null and does not call the converter when the property is null; it serializes only non-null values. The get-only value object
    // is deserialized by System.Text.Json through its single parameterized constructor, matching constructor parameter names to property names.
    private static readonly ValueConverter<DocumentParseMetadata?, string> ExtractionMetadataConverter =
        new AbpJsonValueConverter<DocumentParseMetadata?>();

    // Hand-written because ABP does not provide a generic JSON comparer, and EF Core needs one to snapshot changes for
    // properties converted through a ValueConverter (otherwise it falls back to reference equality and triggers model
    // validation warnings). Null-safe, because EF may snapshot / compare null values.
    // The converter / comparer generic argument uses nullable DocumentParseMetadata? to match the nullable property;
    // otherwise HasConversion triggers CS8620 nullability mismatch warnings.
    private static readonly ValueComparer<DocumentParseMetadata?> ExtractionMetadataComparer =
        new(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => v == null ? null : JsonSerializer.Deserialize<DocumentParseMetadata>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

    public static void ConfigureVaultExtract(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Document>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "Documents", VaultExtractDbProperties.DbSchema);
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

            // #306 / #346 Scenario B back-reference: SHA-256 of the clean constituent Markdown slice.
            b.Property(x => x.OriginConstituentKey).HasMaxLength(DocumentConsts.MaxOriginConstituentKeyLength);

            // Text extraction provenance (#210): provider name + archived manifest, serialized as a whole into a typed JSON column (#206 cross-DB principle).
            b.Property(x => x.ExtractionMetadata)
                .HasConversion(ExtractionMetadataConverter, ExtractionMetadataComparer);

            // Field architecture v3 (#558): the authoritative flex-field value bag, one JSON column. Mapped by
            // the kernel, which also installs the value comparer a converted mutable dictionary needs - without
            // it EF compares the bag by reference and an in-place field write on a loaded document never
            // persists. The sole truth source for field-value queries and persistence (#561, #593: v2's
            // DocumentExtractedField child-row collection was removed once the v3 data migration ran).
            b.ConfigureFlexFieldsProperty<Document>();

            // The bag is read back through AbpJsonValueConverter, i.e.
            // JsonSerializer.Deserialize<FlexFieldDictionary>(columnValue) - and an empty string is not
            // JSON, so that call throws "The input does not contain any JSON tokens" (verified against
            // rc.5, not assumed). Without this default, EF generates the column with the CLR default ""
            // for a non-nullable string, which is harmless on a new table but not here: VaultDocuments is
            // populated, so every document predating v3 would become unreadable on its next load rather
            // than failing at migration time. "{}" is the empty bag such a document actually has.
            b.Property(x => x.FlexFields).HasDefaultValueSql("'{}'");

            // #527 §4: field validation warnings are an aggregate-internal child collection (one merged warning per
            // field), separate from field values. Hard-deleting a Document cascades warning-row deletion; the
            // blocking FieldValidationWarning review bit is coupled to this collection in
            // Document.ReplaceFieldValidationWarnings.
            b.HasMany(x => x.FieldValidationWarnings)
                .WithOne()
                .HasForeignKey(w => w.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // FileOrigin is optional: user-uploaded documents always have one (their own upload blob); derived
            // sub-documents spawned from a container carry none (they seed Markdown from the segment slice and have
            // no file of their own to parse or download). Owned columns are nullable so a sub-document row can write
            // null for the whole owned entity.
            b.OwnsOne(x => x.FileOrigin, fo =>
            {
                fo.Property(x => x.BlobName).HasMaxLength(FileOriginConsts.MaxBlobNameLength);
                fo.Property(x => x.UploadedByUserName).HasMaxLength(FileOriginConsts.MaxUploadedByUserNameLength);
                fo.Property(x => x.OriginalFileName).HasMaxLength(FileOriginConsts.MaxOriginalFileNameLength);
                fo.Property(x => x.ContentType).HasMaxLength(FileOriginConsts.MaxContentTypeLength);
                fo.Property(x => x.ContentHash).HasMaxLength(FileOriginConsts.MaxContentHashLength);

                fo.HasIndex(x => x.BlobName);
                fo.HasIndex(x => x.ContentHash);
            });
            b.Navigation(x => x.FileOrigin).IsRequired(false);

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

            // #306 / #346 Scenario B back-reference (#481: no longer a uniqueness constraint here). OriginDocumentId
            // is PASSIVE provenance (Option A) — this plain, non-unique, non-filtered index serves the #354
            // "list a source's derived documents" read and the #508 delete-guard EXISTS (OriginDocumentId is the
            // leading and only column; it is also portable across every DB provider, unlike the retired
            // SQL-Server-only HasFilter). Spawn idempotency no longer lives on this table: it is now the
            // DocumentSegment ledger's unique (SourceDocumentId, SegmentKey) index plus the segment row's Status
            // transition + optimistic concurrency (see DerivedDocumentSpawner) that makes a sequential retry abort
            // cleanly and a concurrent double-spawn lose on the ConcurrencyStamp at commit. This also dissolves the
            // #391 soft-delete-filtered-unique-index complication: a retracted (soft-deleted) child and its
            // re-spawned successor may now freely share the same OriginConstituentKey, since nothing on Document
            // enforces uniqueness over that pair any more. Still no FK on OriginDocumentId: it is a soft provenance
            // pointer, not a constraint — the invariant that a source outlives its children is enforced at the
            // application layer by the #508 DocumentAppService delete guards, not by the database.
            b.HasIndex(x => x.OriginDocumentId);

            // #411: duplicate-detection fingerprint (SHA-256 hex of this type's normalized unique-key field values).
            // DuplicateAllowed (the operator's "not a duplicate" override) is a plain bool, auto-mapped by convention.
            b.Property(x => x.FieldFingerprint).HasMaxLength(DocumentConsts.MaxFieldFingerprintLength);

            // Collision lookup (FindDuplicateCandidateIdsAsync): other documents in the same layer + type with the
            // same fingerprint. (TenantId, DocumentTypeId, FieldFingerprint) matches the query WHERE (the IMultiTenant
            // filter supplies TenantId; the repository filters DocumentTypeId + FieldFingerprint). The leading
            // (TenantId, DocumentTypeId) prefix also covers the list-page filter, but the existing dedicated
            // (TenantId, DocumentTypeId) index above is kept to avoid an index drop on a populated table.
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId, x.FieldFingerprint });
        });

        builder.Entity<DocumentFieldValidationWarning>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "DocumentFieldValidationWarnings", VaultExtractDbProperties.DbSchema);
            b.ConfigureByConvention();

            // Composite primary key (DocumentId, FieldDefinitionId) (#527 §4): one merged warning per field.
            // DocumentId is also the identifying foreign key to the Document aggregate root.
            b.HasKey(x => new { x.DocumentId, x.FieldDefinitionId });

            b.Property(x => x.Message).IsRequired().HasMaxLength(DocumentFieldValidationWarningConsts.MaxMessageLength);

            // Internal field association (#207): FK -> Field.Id, OnDelete Restrict. Field uses soft delete and does not
            // trigger the FK; only hard-deleting a field still referenced by a warning is rejected by the DB — the same
            // soft-delete-preserves-history safety v2's DocumentExtractedField relied on. (§7's
            // ClearFieldValidationWarnings fires on Document *type change* — reclassify / unclassify / container — NOT on
            // field deletion, so it is not what guards this FK.) EF automatically creates an index for this FK. This row
            // participates in no field-value index and is never consulted by field-value search / filtering (#527 §11).
            //
            // Repointed from FieldDefinition to Field by #562: the warning names the field that produced it, and once
            // extraction resolves warnings against v3 rows, a warning written for one would have violated an FK still
            // pointing at the other. The column keeps its name — FieldDefinitionId is on the wire in
            // ResolveFieldValidationWarningsInput and in the review-reason detail DTO.
            b.HasOne<Field>()
                .WithMany()
                .HasForeignKey(x => x.FieldDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentPipelineRun>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "DocumentPipelineRuns", VaultExtractDbProperties.DbSchema);
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
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "DocumentSegments", VaultExtractDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SegmentKey).IsRequired().HasMaxLength(DocumentSegmentConsts.MaxSegmentKeyLength);
            // SliceText is a Markdown slice used to seed the derived document (nvarchar(max), like Document.Markdown);
            // not indexed.
            b.Property(x => x.SliceText).IsRequired();
            b.Property(x => x.Ordinal).IsRequired();
            // #371: which span kind this segment was carved from (Text constituent vs embedded Figure); drives the
            // container→type retraction filter (#364). PageNumber is a nullable recovery anchor (page) for
            // Figure-kind rows, parsed from the *[Image OCR p:N]* marker; neither is indexed.
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

            // Reading-order uniqueness (#346/#372): no two segment rows of one source share an Ordinal. This is a
            // structural guard, NOT the primary double-split backstop. The authoritative concurrency backstops are the
            // Document concurrency stamp — both runs re-check !IsSegmented and then UpdateAsync the source via
            // MarkSegmented in the same UoW as the rows, so the loser's commit conflicts (#377) — and the
            // (SourceDocumentId, SegmentKey) unique index above. A FRESH split numbers from Ordinal 0, so two
            // CONCURRENT fresh splits also collide here (both write Ordinal 0) and one rolls back; but a SEQUENTIAL
            // cross-mode re-split (a concrete doc's embedded figure already routed, then a container re-recognition,
            // #372/#377) deliberately numbers from Max(Ordinal)+1 and skips already-persisted keys, so it neither
            // relies on nor trips an Ordinal-0 collision.
            b.HasIndex(x => new { x.SourceDocumentId, x.Ordinal })
                .IsUnique();
        });

        builder.Entity<DocumentType>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "DocumentTypes", VaultExtractDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.TypeCode).IsRequired().HasMaxLength(DocumentTypeConsts.MaxTypeCodeLength);
            b.Property(x => x.DisplayName).IsRequired().HasMaxLength(DocumentTypeConsts.MaxDisplayNameLength);
            // Nullable: classification helper description (#262), NULL = no description.
            b.Property(x => x.Description).HasMaxLength(DocumentTypeConsts.MaxDescriptionLength);
            b.Property(x => x.ConfidenceThreshold).IsRequired();
            b.Property(x => x.Priority).IsRequired();

            // #651: what counts as a duplicate for this type. Stored as the enum's int, whose values are a frozen
            // persisted contract; required with a 0 (= Layer) default so existing rows keep today's layer-wide
            // behaviour and the upgrade changes no document's state. No index: it is read one row at a time, by
            // the type the document already resolved.
            b.Property(x => x.DuplicateScope).IsRequired().HasDefaultValue(DuplicateDetectionScope.Layer);

            // Layer-scoped uniqueness on (TenantId, TypeCode) is enforced by DocumentTypeManager in the application/domain
            // layer (#304), not by a DB index. The previous soft-delete-filtered unique index relied on SQL Server's
            // "unique index treats NULL as equal" semantics for Host rows (TenantId IS NULL) plus a HasFilter("IsDeleted = 0")
            // literal — neither portable across providers (PostgreSQL defaults to NULLS DISTINCT, which would silently drop the
            // Host-layer guarantee). Dropping it makes the schema cross-DB by construction; the accepted tradeoff is a TOCTOU
            // race on these low-frequency admin-config entities. The same applies to Field / Cabinet below.
        });

        // === Field architecture v3 (#558): the FlexFields kernel's three shapes ===
        // Replaces the v2 FieldDefinition / DocumentExtractedField tables, which were dropped once the v3 data
        // migration ran and was verified (#561 expand-then-contract, #593 the final drop).

        builder.Entity<Field>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "Fields", VaultExtractDbProperties.DbSchema);
            b.ConfigureByConvention();

            // Name / DisplayName / Description / FieldTypeName / Configuration and their column lengths are
            // mapped by the kernel, so the contract's storage shape stays identical across every downstream.
            // Description is mapped with no length limit, which is what keeps #447's uncapped extraction
            // instruction uncapped after the move off FieldDefinition.Prompt.
            b.ConfigureFlexField<Field>();

            // Clear the kernel's max length on Description. Field.Description carries what v2 called
            // Prompt - the LLM extraction instruction - which #447 deliberately left uncapped as
            // admin-authored configuration that may be long structured Markdown. The published
            // 10.0.0-rc.5 package still maps Description as nvarchar(256), so without this the column
            // would silently narrow a decision this project already made, and the entity (which enforces
            // no length) would happily accept values the database then rejects.
            //
            // Drop this line once flex-fields ships a release without that limit - not before, and not on
            // the strength of the source repo showing it removed: what matters is the version in
            // Directory.Packages.props.
            b.Property(x => x.Description).Metadata.SetMaxLength(null);

            // Vault Extract's own columns, beside the contract's - same shape as the site repository's GroupName.
            b.Property(x => x.DisplayOrder).IsRequired();
            b.Property(x => x.IsRequired).IsRequired();
            b.Property(x => x.IsSearchable).IsRequired();
            b.Property(x => x.IsUniqueKey).IsRequired();

            // Internal association to parent document type (#207): FK -> DocumentType.Id, OnDelete Restrict.
            b.HasOne<DocumentType>()
                .WithMany()
                .HasForeignKey(x => x.DocumentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // Layer-scoped uniqueness on (TenantId, DocumentTypeId, Name) stays an application-layer check,
            // not a DB index - same cross-DB rationale as DocumentType above.
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
        });

        builder.Entity<DocumentFlexFieldIndex>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "DocumentFlexFieldIndexes", VaultExtractDbProperties.DbSchema);
            b.ConfigureByConvention();

            // The five typed value slots and their lengths, from the kernel.
            b.ConfigureFlexFieldIndex<DocumentFlexFieldIndex>();

            // Same precision as the v2 field-value column this index is rebuilt from. The kernel maps
            // NumberValue with no precision, so EF falls back to decimal(18,2) - which, as the v2 block
            // above records, "silently rounds values with more than 2 decimals and loses precision".
            // Since these rows are what the query executor compares against, that default would make a
            // filter on 0.0825 miss the document whose bag holds exactly 0.0825, and would throw
            // arithmetic overflow on any value above 16 integer digits during a rebuild.
            b.Property(x => x.NumberValue).HasPrecision(38, 6);

            // Derived rows die with their document. No soft delete: these are hidden through the parent
            // Document filter while it is soft-deleted, exactly like the v2 DocumentExtractedField rows.
            b.HasOne<Document>()
                .WithMany()
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Rebuild and per-document delete both walk by document.
            b.HasIndex(x => x.DocumentId);

            // Seek shape for "documents where field X matches Y": narrow by tenant and field, then by the
            // typed slot, ending on DocumentId so the match is index-only. One per slot that can actually be
            // compared - Boolean gets none, the same call the v2 mapping made: two distinct values is too
            // little selectivity to earn an index, and the (TenantId, FieldId) prefix plus AND-narrowing with
            // other fields already does the work.
            b.HasIndex(x => new { x.TenantId, x.FieldId, x.StringValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldId, x.NumberValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldId, x.DateTimeValue, x.DocumentId });
            b.HasIndex(x => new { x.TenantId, x.FieldId, x.GuidValue, x.DocumentId });
        });

        builder.Entity<Cabinet>(b =>
        {
            b.ToTable(VaultExtractDbProperties.DbTablePrefix + "Cabinets", VaultExtractDbProperties.DbSchema);
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
