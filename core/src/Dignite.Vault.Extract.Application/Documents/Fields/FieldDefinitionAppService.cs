using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.FlexFields;
using Dignite.Vault.Extract.Documents.Fields.Cleanup;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.Fields;

// Authorization is declared per method (#223): reading field schema (active GetListAsync) is decoupled from schema management.
// Therefore there is no class-level [Authorize]; each method explicitly declares its own permission gate,
// using the same programmatic pattern as DocumentAppService.
public class FieldDefinitionAppService : VaultExtractAppService, IFieldDefinitionAppService
{
    /// <summary>
    /// Same allow-list <see cref="Field.SetName"/> enforces on a top-level field's <c>Name</c> (#625
    /// follow-up), re-declared here for a composite type's own columns: a column's <c>Name</c> reaches the
    /// LLM's JSON schema message exactly like <see cref="Field.Name"/> does
    /// (<c>TableFieldTypeExtension.BuildExtractionSchema</c> uses it verbatim as a property key), so it is
    /// the same prompt-injection boundary, not a formatting preference the kernel would validate for us.
    /// </summary>
    private static readonly Regex ColumnNameRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFieldRepository _repository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly FieldDefinitionManager _fieldDefinitionManager;
    private readonly IFieldTypeResolver _fieldTypeResolver;

    /// <summary>
    /// A field's <c>Name</c> is the key its values are stored under in every document's bag, so a rename
    /// has to move that key on every document — the one thing <c>RebuildAsync</c> explicitly cannot repair,
    /// because re-deriving reads the bag under the same key it is trying to move.
    /// </summary>
    private readonly DocumentFieldValueMigrator _valueMigrator;

    /// <summary>
    /// Switching <c>IsSearchable</c> changes which of a field's values belong in the index, and
    /// re-deriving from the bag is a complete migration for that — see
    /// <c>IFlexFieldIndexManager.RebuildAsync</c>'s XML doc. Rebuilding on every toggle rather than
    /// only when it turns on: turning it off has to drop the now-stale rows just as much as turning
    /// it on has to backfill the missing ones.
    /// </summary>
    private readonly IFlexFieldIndexManager<Document> _indexManager;

    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly FieldSchemaPromptBudgetGuard _schemaPromptBudget;
    private readonly IVaultExtractFieldTypeRegistry _fieldTypeExtensionRegistry;

    public FieldDefinitionAppService(
        IFieldRepository repository,
        IDocumentTypeRepository documentTypeRepository,
        IDocumentRepository documentRepository,
        FieldDefinitionManager fieldDefinitionManager,
        IFieldTypeResolver fieldTypeResolver,
        DocumentFieldValueMigrator valueMigrator,
        IFlexFieldIndexManager<Document> indexManager,
        IBackgroundJobManager backgroundJobManager,
        FieldSchemaPromptBudgetGuard schemaPromptBudget,
        IVaultExtractFieldTypeRegistry fieldTypeExtensionRegistry)
    {
        _repository = repository;
        _documentTypeRepository = documentTypeRepository;
        _documentRepository = documentRepository;
        _fieldDefinitionManager = fieldDefinitionManager;
        _fieldTypeResolver = fieldTypeResolver;
        _valueMigrator = valueMigrator;
        _indexManager = indexManager;
        _backgroundJobManager = backgroundJobManager;
        _schemaPromptBudget = schemaPromptBudget;
        _fieldTypeExtensionRegistry = fieldTypeExtensionRegistry;
    }

    /// <summary>See <see cref="IFieldDefinitionAppService.GetFieldTypesAsync"/>.</summary>
    public virtual async Task<List<FieldTypeDto>> GetFieldTypesAsync()
    {
        // Same fail-closed OR gate as GetListAsync: both the field designer (FieldDefinitions.Default)
        // and the document filter/detail UI (Documents.Default) need this catalog, and neither widens
        // visibility by being granted it (see GetListAsync's own comment for why).
        if (!await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default) &&
            !await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.FieldDefinitions.Default))
        {
            throw new AbpAuthorizationException();
        }

        // Filtered through the same registry EnsureFieldTypeRegistered enforces, not everything the
        // kernel resolver knows about — see IVaultExtractFieldTypeRegistry for why the two lists can
        // differ (the kernel registers Tree unconditionally; Vault Extract has no extension for it).
        var fieldTypes = _fieldTypeResolver.GetAll()
            .Where(fieldType => _fieldTypeExtensionRegistry.IsSupported(fieldType.Name))
            .Select(fieldType => new FieldTypeDto
            {
                Name = fieldType.Name,
                Indexable = fieldType.IsIndexable(),
            })
            .ToList();

        return fieldTypes;
    }

    public virtual async Task<List<FieldDefinitionDto>> GetListAsync(GetFieldDefinitionListInput input)
    {
        // Current tenant layer only (CLAUDE.md "two layers are mutually exclusive, no mixing").
        // Tenant isolation is enforced by the ABP IMultiTenant global filter.
        // When DocumentTypeId is specified, match exactly one type by immutable Id (#207); missing type naturally returns an empty set.
        // Empty = all field definitions in the current layer, the batch path used by MCP vault_extract_list_document_types and similar callers to fetch once and avoid per-type N+1.
        if (input.OnlyDeleted)
        {
            // Trash view is consumed only by schema management screens, so keep the admin gate (#223).
            await CheckPolicyAsync(VaultExtractPermissions.FieldDefinitions.Default);

            // Trash view: traverse soft-delete filter, take only IsDeleted, ordered by deletion time descending.
            using (DataFilter.Disable<ISoftDelete>())
            {
                var queryable = await _repository.GetQueryableAsync();
                var deletedQuery = queryable.Where(f => f.IsDeleted);
                if (input.DocumentTypeId != null)
                {
                    deletedQuery = deletedQuery.Where(f => f.DocumentTypeId == input.DocumentTypeId);
                }
                var deleted = await AsyncExecuter.ToListAsync(
                    deletedQuery.OrderByDescending(f => f.DeletionTime));
                return ObjectMapper.Map<List<Field>, List<FieldDefinitionDto>>(deleted);
            }
        }

        // Active field schema reads are decoupled from schema management (#223): document operators (Documents.Default) need field definitions
        // to drive dynamic field columns / detail field editing / export column selection; field admins (FieldDefinitions.Default)
        // need to read their own management list. Either is enough: fail-closed OR assertion.
        // Batch queries (DocumentTypeId empty) and type-scoped queries use the same permission gate and do not widen visibility;
        // enumerating per type could already obtain the same set.
        if (!await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.Documents.Default) &&
            !await AuthorizationService.IsGrantedAsync(VaultExtractPermissions.FieldDefinitions.Default))
        {
            throw new AbpAuthorizationException();
        }

        if (input.DocumentTypeId == null)
        {
            // Batch path: query all active fields in the current layer once, with IMultiTenant + ISoftDelete filters still applied.
            // Stable-sort by DocumentTypeId then DisplayOrder; callers group in memory.
            var queryable = await _repository.GetQueryableAsync();
            var all = await AsyncExecuter.ToListAsync(
                queryable
                    .OrderBy(f => f.DocumentTypeId)
                    .ThenBy(f => f.DisplayOrder));
            return ObjectMapper.Map<List<Field>, List<FieldDefinitionDto>>(all);
        }

        var list = await _repository.GetListAsync(input.DocumentTypeId.Value);
        return ObjectMapper.Map<List<Field>, List<FieldDefinitionDto>>(list);
    }

    [Authorize(VaultExtractPermissions.FieldDefinitions.Create)]
    public virtual async Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input)
    {
        // Parent type must exist in the current layer (#207 Field.DocumentTypeId FK RESTRICT).
        // IMultiTenant + ISoftDelete filters ensure cross-layer / deleted types return null.
        var type = await _documentTypeRepository.FindAsync(input.DocumentTypeId);
        if (type == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), input.DocumentTypeId);
        }

        EnsureFieldTypeRegistered(input.FieldTypeName, input.Configuration);
        CheckSearchable(input.FieldTypeName, input.IsSearchable);
        CheckUniqueKey(input.FieldTypeName, input.IsUniqueKey);

        // Soft-delete-aware duplicate check owned by the domain service (#304): the same (TenantId, DocumentTypeId, Name)
        // counts as occupied even when soft-deleted, avoiding conflicts with new records on restore.
        await _fieldDefinitionManager.CheckNameAvailableAsync(input.DocumentTypeId, input.Name);
        await EnsureSchemaPromptBudgetAsync(type, replacingFieldId: null, projectedPrompt: input.Description);

        var entity = new Field(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.DocumentTypeId,
            input.Name,
            input.DisplayName,
            input.FieldTypeName,
            input.Description,
            input.Configuration,
            input.DisplayOrder,
            input.IsRequired,
            input.IsSearchable,
            input.IsUniqueKey);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<Field, FieldDefinitionDto>(entity);
    }

    [Authorize(VaultExtractPermissions.FieldDefinitions.Update)]
    public virtual async Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input)
    {
        var entity = await _repository.GetAsync(id);

        // Cross-layer defense: callers may modify only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Field), id);
        }

        EnsureFieldTypeRegistered(input.FieldTypeName, input.Configuration);
        CheckSearchable(input.FieldTypeName, input.IsSearchable);
        CheckUniqueKey(input.FieldTypeName, input.IsUniqueKey);

        var oldName = entity.Name;
        var wasSearchable = entity.IsSearchable;
        var renamed = !string.Equals(input.Name, oldName, StringComparison.Ordinal);
        if (renamed)
        {
            // Rename unlock (#207): run the domain duplicate check only when Name changes. Same layer + same type is unique,
            // including soft-deleted occupancy. The manager resolves the owning TypeCode for the error message only on conflict.
            await _fieldDefinitionManager.CheckNameAvailableAsync(entity.DocumentTypeId, input.Name);
        }

        // #207, carried into v3: changing the field type of a field that already holds values is refused.
        // The values were validated against the old type and stay in the bag untouched, but nothing would
        // render or index them under the new one — the same silent disappearance the v2 typed-column guard
        // existed to prevent, arrived at by a different route.
        //
        // v3 drops the separate multi-value narrowing guard: "one value or many" is a property of the type
        // now (Tags versus Text), so narrowing IS a type change and this one guard covers both.
        //
        // #625 follow-up: for a composite type (Table) whose own FieldTypeName did NOT change, a change to
        // its COLUMNS is the same kind of silent-disappearance risk — renaming, removing, adding, retyping,
        // or reordering a column orphans the historical cell data every already-extracted document holds
        // for it, the same way a top-level type change would. Blocked by the same guard, same error code:
        // "this field's shape changed under stored values" is one rule, not two.
        var fieldTypeChanged = !string.Equals(input.FieldTypeName, entity.FieldTypeName, StringComparison.Ordinal);
        var columnsChanged = !fieldTypeChanged &&
            CompositeColumnsChanged(entity.FieldTypeName, entity.Configuration, input.Configuration);
        if ((fieldTypeChanged || columnsChanged) &&
            await _documentRepository.AnyFlexFieldValueAsync(entity, IsIndexable(entity.FieldTypeName)))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.DataTypeChangeNotAllowed)
                .WithData("Name", entity.Name);
        }

        var type = await _documentTypeRepository.GetAsync(entity.DocumentTypeId);
        await EnsureSchemaPromptBudgetAsync(type, entity.Id, input.Description);

        entity.SetName(input.Name);
        entity.SetDisplayName(input.DisplayName);
        entity.SetDescription(input.Description);
        entity.SetFieldTypeName(input.FieldTypeName);
        entity.SetConfiguration(input.Configuration);
        entity.SetDisplayOrder(input.DisplayOrder);
        entity.SetIsRequired(input.IsRequired);
        entity.SetIsSearchable(input.IsSearchable);
        entity.SetIsUniqueKey(input.IsUniqueKey);
        await _repository.UpdateAsync(entity, autoSave: true);

        if (renamed)
        {
            // Order matters, and the kernel is explicit about it: the definition changes first, the bags
            // follow, and nothing may synchronize the index in between — an entity synchronized between the
            // two steps projects nothing for this field and loses the index rows it had. Nothing here
            // reindexes afterwards either, and nothing needs to: index rows key on field id and value,
            // neither of which a rename touches.
            //
            // Scoped to this field's own document type, NOT IFlexFieldValueMigrator<Document>: a field name
            // is unique per (TenantId, DocumentTypeId, Name) here, so the kernel's rename-by-name-everywhere
            // would rewrite another type's identically named field out of reach. See DocumentFieldValueMigrator.
            await _valueMigrator.RenameFieldAsync(entity.DocumentTypeId, oldName, input.Name);
        }

        if (wasSearchable != entity.IsSearchable)
        {
            // Turning IsSearchable on has to backfill the values that were never indexed; turning it off
            // has to drop the rows that now would be. Re-deriving the whole index is a complete migration
            // for either direction, and cheaper to reason about than trying to patch just this field's rows.
            await _indexManager.RebuildAsync();
        }

        return ObjectMapper.Map<Field, FieldDefinitionDto>(entity);
    }

    /// <summary>
    /// Soft-deletes a field definition and reconciles the document state derived from it (#528).
    /// <para>
    /// Deletion alone used to leave every <see cref="DocumentFieldValidationWarning"/> naming this field in place,
    /// and with it the blocking <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit — parking those
    /// documents out of <c>DocumentReadyEto</c> until an operator resolved them by hand or a re-extraction happened
    /// to run. The cleanup is deferred to <see cref="FieldValidationWarningCleanupJob"/> because the affected set is
    /// bounded only by the type's document count; enqueueing happens inside this UoW, so it cannot run for a delete
    /// that rolled back.
    /// </para>
    /// <para>
    /// The scope line is the Ready gate (<c>ReviewReasonPolicy.Blocking</c>): state that <b>withholds</b> a document
    /// is reconciled here, state that is merely stale is not. Hence the second, conditional job — and hence
    /// <c>MissingRequiredFields</c> (non-blocking) and a stale fingerprint under a narrowed-but-non-empty unique-key
    /// set (an under-detection, not a park) are #537, not this path.
    /// </para>
    /// <para>
    /// The values themselves are deliberately left in their bags, as v2 left its value rows: deletion is soft, and
    /// <see cref="RestoreAsync"/> has to be able to bring the field back to values that are still there. They are
    /// invisible while the field is gone — <c>AssembleExtractedFields</c> only emits keys that resolve to a
    /// definition — which is the same "archived field contributes no column" behaviour #499 pinned for the export.
    /// </para>
    /// </summary>
    [Authorize(VaultExtractPermissions.FieldDefinitions.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Field), id);
        }

        await _repository.DeleteAsync(entity);

        await _backgroundJobManager.EnqueueAsync(
            new FieldValidationWarningCleanupArgs
            {
                FieldDefinitionId = entity.Id,
                TenantId = entity.TenantId
            });

        if (entity.IsUniqueKey)
        {
            // Always enqueue for a deleted unique-key field; the job re-evaluates the FINAL active schema when it
            // runs and only clears the duplicate basis when no unique-key field remains. Deciding "last key" here
            // is racy: two concurrent deletes could each observe the other key and enqueue nothing, while a restore
            // or a newly-created key before job execution could make an enqueue-time "last key" decision stale.
            await _backgroundJobManager.EnqueueAsync(
                new DuplicateBasisCleanupArgs
                {
                    DocumentTypeId = entity.DocumentTypeId,
                    TenantId = entity.TenantId
                });
        }
    }

    [Authorize(VaultExtractPermissions.FieldDefinitions.Delete)]
    public virtual async Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(Field), id);
            }

            // Already inside Disable<ISoftDelete>, so the parent type TypeCode can be resolved even if soft-deleted for error messages / DTO.
            var parentType = await _documentTypeRepository.FindAsync(entity.DocumentTypeId);
            var documentTypeCode = parentType?.TypeCode;

            // Idempotent: return directly when not deleted.
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<Field, FieldDefinitionDto>(entity);
            }

            // Parent type must exist and be active, with strict single-layer matching (consistent with FieldExtractionService).
            // If the parent type is still deleted, use the cascading path in IDocumentTypeAppService.RestoreAsync instead.
            if (parentType == null || parentType.IsDeleted)
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.ParentTypeMissing)
                    .WithData("DocumentTypeCode", documentTypeCode ?? string.Empty)
                    .WithData("Name", entity.Name);
            }

            // Active field with the same name conflicts. CreateAsync duplicate checks should already prevent this; the domain
            // service keeps a defensive guard and throws RestoreConflict (#304).
            await _fieldDefinitionManager.CheckRestorableAsync(entity);

            // A deleted field is outside the active extraction schema. Restoring it is therefore another
            // configuration write and must validate the projected active set, especially when siblings were added
            // while this field was in the recycle bin or the host lowered the configured ceiling.
            var queryable = await _repository.GetQueryableAsync();
            var activePrompts = await AsyncExecuter.ToListAsync(
                queryable
                    .Where(f =>
                        f.DocumentTypeId == entity.DocumentTypeId &&
                        f.Id != entity.Id &&
                        !f.IsDeleted)
                    .Select(f => f.Description));
            activePrompts.Add(entity.Description);
            _schemaPromptBudget.EnsureCanPersist(parentType.TypeCode, activePrompts);

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            return ObjectMapper.Map<Field, FieldDefinitionDto>(entity);
        }
    }

    protected virtual async Task EnsureSchemaPromptBudgetAsync(
        DocumentType type,
        Guid? replacingFieldId,
        string? projectedPrompt)
    {
        var fields = await _repository.GetListAsync(type.Id);
        var projectedPrompts = fields
            .Where(f => f.Id != replacingFieldId)
            .Select(f => f.Description)
            .Append(projectedPrompt);

        _schemaPromptBudget.EnsureCanPersist(type.TypeCode, projectedPrompts);
    }

    /// <summary>
    /// A <c>FieldTypeName</c> is a registration key, not a class name, and it arrives from the wire. An
    /// unregistered one would persist a field that no reader, validator or indexer can act on — every one
    /// of them dispatches on this string — so it is rejected at the boundary rather than discovered later
    /// as values that silently fail to save.
    /// </summary>
    protected virtual void EnsureFieldTypeRegistered(string fieldTypeName, FieldConfigurationDictionary? configuration)
    {
        // Both halves matter: registered with the kernel (a name Vault Extract could dispatch on at all)
        // and in Vault Extract's own extension registry (a name something here actually does dispatch on
        // — see IVaultExtractFieldTypeRegistry for the built-in the kernel ships that fails this second
        // check).
        var kernelFieldType = FindFieldType(fieldTypeName);
        if (kernelFieldType == null || !_fieldTypeExtensionRegistry.IsSupported(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.UnknownFieldType)
                .WithData("FieldTypeName", fieldTypeName);
        }

        // A composite type (Table, #625) declares further fields inline, inside its own configuration,
        // rather than under a single FieldTypeName the check above already covers. Each inline column's
        // own FieldTypeName must independently clear Vault Extract's own registry gate too — the kernel's
        // own recursive validator (InlineFieldValidator, inside e.g. TableFieldType.Validate) only checks
        // kernel-level shape correctness against IFieldTypeResolver, which says nothing about whether Vault
        // Extract has ever wired up an IVaultExtractFieldTypeExtension for that column's type. Left
        // unchecked here, an unregistered column type would only surface at extraction time, as
        // FlexFieldValueSchemaBuilder's/the column extension's own last-resort NotSupportedException — by
        // which point the definition is already saved and every document of the type extracts nothing for
        // it. Generic over ICompositeFieldType rather than named to Table specifically, so a future
        // composite type (Matrix, out of scope for #625) is covered the moment it implements the same
        // kernel interface, with nothing to register here.
        if (kernelFieldType is ICompositeFieldType)
        {
            var allFieldTypes = _fieldTypeResolver.GetAll();

            // Reject a definition that nests composite field types deeper than the kernel's own cap FIRST,
            // before anything below recurses into the configuration itself — CompositeFieldNesting's own
            // doc explains why: it is the first thing to walk a configuration that has not been vetted yet,
            // and only the caller (here) can bound that walk before it runs. Order matters: the recursive
            // column walk right below is safe from runaway recursion only because this already ran.
            if (CompositeFieldNesting.ExceedsMaxDepth(fieldTypeName, configuration, allFieldTypes))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.CompositeNestingTooDeep)
                    .WithData("FieldTypeName", fieldTypeName)
                    .WithData("MaxDepth", CompositeFieldNesting.MaxDepth);
            }

            EnsureColumnsRegistered(fieldTypeName, configuration ?? new FieldConfigurationDictionary(), allFieldTypes);
        }
    }

    /// <summary>
    /// Recurses into a composite field type's own columns (#625), checking each one against the same two
    /// gates a top-level <c>FieldTypeName</c> already clears above: Vault Extract's own registry
    /// (<see cref="VaultExtractErrorCodes.FieldDefinition.UnknownColumnFieldType"/>) and the <c>Name</c>
    /// allow-list <see cref="Field.SetName"/> enforces
    /// (<see cref="VaultExtractErrorCodes.FieldDefinition.InvalidColumnName"/>). When a column's own type
    /// is itself composite (a Table column that is a Table), recurses into its columns in turn — this is
    /// only safe from unbounded recursion because <see cref="EnsureFieldTypeRegistered"/> already rejected
    /// anything deeper than <see cref="CompositeFieldNesting.MaxDepth"/> before this method is ever called.
    /// </summary>
    protected virtual void EnsureColumnsRegistered(
        string fieldTypeName, FieldConfigurationDictionary configuration, IReadOnlyList<IFieldType> allFieldTypes)
    {
        var fieldType = allFieldTypes.FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal));
        if (fieldType is not ICompositeFieldType compositeFieldType)
        {
            return;
        }

        foreach (var column in compositeFieldType.GetInlineFields(configuration))
        {
            if (!_fieldTypeExtensionRegistry.IsSupported(column.FieldTypeName))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.UnknownColumnFieldType)
                    .WithData("FieldTypeName", column.FieldTypeName)
                    .WithData("ColumnName", column.Name);
            }

            if (!ColumnNameRegex.IsMatch(column.Name))
            {
                throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidColumnName)
                    .WithData("ColumnName", column.Name)
                    .WithData("Pattern", FieldDefinitionConsts.NamePattern);
            }

            EnsureColumnsRegistered(column.FieldTypeName, column.Configuration, allFieldTypes);
        }
    }

    /// <summary>
    /// Whether values of this field type reach the query index at all. A type with no index value type
    /// (long text) produces no index rows however <c>IsSearchable</c> is set, which is what makes the
    /// index unusable as an "are there values" oracle for it.
    /// </summary>
    protected virtual bool IsIndexable(string fieldTypeName)
        => FindFieldType(fieldTypeName)?.IndexValueType != null;

    /// <summary>
    /// Whether a composite type's (<c>Table</c>) own column list differs between an old and a new
    /// configuration — not just the outer <c>FieldTypeName</c>, which the caller already compares
    /// separately (#625 follow-up: option A, block rather than migrate). Compares the ordered
    /// <c>(Name, FieldTypeName)</c> pairs <see cref="ICompositeFieldType.GetInlineFields"/> returns:
    /// adding, removing, renaming, retyping, or reordering any column counts as a change, because any of
    /// those orphans the cell data an already-extracted document holds under the old shape. A non-composite
    /// <paramref name="fieldTypeName"/> (or one the kernel no longer recognizes) never counts as changed
    /// here — the outer-type comparison at the call site already covers that case.
    /// </summary>
    protected virtual bool CompositeColumnsChanged(
        string fieldTypeName, FieldConfigurationDictionary? oldConfiguration, FieldConfigurationDictionary? newConfiguration)
    {
        if (FindFieldType(fieldTypeName) is not ICompositeFieldType compositeFieldType)
        {
            return false;
        }

        var oldColumns = compositeFieldType
            .GetInlineFields(oldConfiguration ?? new FieldConfigurationDictionary())
            .Select(c => (c.Name, c.FieldTypeName))
            .ToList();
        var newColumns = compositeFieldType
            .GetInlineFields(newConfiguration ?? new FieldConfigurationDictionary())
            .Select(c => (c.Name, c.FieldTypeName))
            .ToList();

        return !oldColumns.SequenceEqual(newColumns);
    }

    /// <summary>
    /// Rejects a field marked searchable under a field type with no query-index slot.
    /// <para>
    /// The Angular field designer already disables the setting for such a type, but that is a courtesy
    /// to the admin, not the rule: anything reaching this service another way — a direct API call, MCP,
    /// a pack import — would otherwise store a flag <see cref="Dignite.Abp.FlexFields.FlexFieldIndexManagerBase{TEntity}"/>
    /// silently ignores, and the only symptom would be a filter that never matches. Failing the save is
    /// the difference between a mistake that is reported and one that is invisible.
    /// </para>
    /// </summary>
    protected virtual void CheckSearchable(string fieldTypeName, bool isSearchable)
    {
        if (isSearchable && !IsIndexable(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotSearchable)
                .WithData("FieldTypeName", fieldTypeName);
        }
    }

    /// <summary>
    /// Rejects a field marked as a unique key under a field type with no query-index slot (#626) — the
    /// same predicate <see cref="CheckSearchable"/> already gates on, because a composite (Table) or
    /// long-text (CKEditor) value is not something a document should be identified by, and duplicate-
    /// detection fingerprinting is a deliberate product restriction rather than a mechanical limitation of
    /// how the fingerprint itself reads values.
    /// </summary>
    protected virtual void CheckUniqueKey(string fieldTypeName, bool isUniqueKey)
    {
        if (isUniqueKey && !IsIndexable(fieldTypeName))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.FieldTypeNotUniqueKeyable)
                .WithData("FieldTypeName", fieldTypeName);
        }
    }

    /// <summary>
    /// Lookup that tolerates an unknown name. <c>IFieldTypeResolver.Get</c> throws an
    /// <c>AbpException</c> — right for the internal callers that only ever pass a name a stored field
    /// already carries, wrong here, where the name arrives on the wire and "unknown" is a 400 with a
    /// localized message rather than a 500.
    /// </summary>
    protected virtual IFieldType? FindFieldType(string fieldTypeName)
        => _fieldTypeResolver.GetAll().FirstOrDefault(t => string.Equals(t.Name, fieldTypeName, StringComparison.Ordinal));
}
