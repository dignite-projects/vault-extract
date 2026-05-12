using System.Text;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Agents;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Contracts.Contracts;
using Dignite.Paperbase.Contracts.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Contracts.EventHandlers;

public class ContractDocumentHandler :
    IDistributedEventHandler<DocumentClassifiedEto>,
    IDistributedEventHandler<DocumentDeletedEto>,
    IDistributedEventHandler<DocumentRestoredEto>,
    IDistributedEventHandler<DocumentPermanentlyDeletedEto>,
    ITransientDependency
{
    private readonly IContractRepository _contractRepository;
    private readonly ContractManager _contractManager;
    private readonly IChatClient _chatClient;
    private readonly ICurrentTenant _currentTenant;
    private readonly IContractExtractionExampleProvider _exampleProvider;
    private readonly IExtractionValidator<ContractExtractionResult> _extractionValidator;
    private readonly ContractsTelemetryRecorder _telemetry;
    private readonly ILogger<ContractDocumentHandler> _logger;

    public ContractDocumentHandler(
        IContractRepository contractRepository,
        ContractManager contractManager,
        IChatClient chatClient,
        ICurrentTenant currentTenant,
        IContractExtractionExampleProvider exampleProvider,
        IExtractionValidator<ContractExtractionResult> extractionValidator,
        ContractsTelemetryRecorder telemetry,
        ILogger<ContractDocumentHandler>? logger = null)
    {
        _contractRepository = contractRepository;
        _contractManager = contractManager;
        _chatClient = chatClient;
        _currentTenant = currentTenant;
        _exampleProvider = exampleProvider;
        _extractionValidator = extractionValidator;
        _telemetry = telemetry;
        _logger = logger ?? NullLogger<ContractDocumentHandler>.Instance;
    }

    public virtual async Task HandleEventAsync(DocumentClassifiedEto eventData)
    {
        if (string.IsNullOrEmpty(eventData.DocumentTypeCode) ||
            !eventData.DocumentTypeCode.StartsWith(ContractsDocumentTypes.Prefix))
        {
            return;
        }

        using (_currentTenant.Change(eventData.TenantId))
        {
            // 字段抽取直接吃 Markdown：结构信号（标题/表格/列表）有助于 LLM 准确定位金额、日期等字段。
            var extraction = await ExtractFieldsAsync(
                eventData.Markdown ?? string.Empty,
                eventData.DocumentTypeCode);

            // Issue #143 telemetry snapshot: re-run the validator on the FINAL result so
            // the recorder reflects what's actually being persisted (retries may have
            // either fixed or exhausted by this point). The validator is a pure function,
            // so the second call is essentially free relative to the LLM cost above.
            var finalValidation = _extractionValidator.Validate(extraction);
            _telemetry.RecordExtraction(
                eventData.DocumentTypeCode,
                finalValidation,
                extraction.ExtractionConfidence);

            var fields = extraction.ToContractFields();

            var existing = await _contractRepository.FindByDocumentIdAsync(eventData.DocumentId);

            if (existing != null)
            {
                existing.UpdateExtractedFields(fields);
                await _contractRepository.UpdateAsync(existing, autoSave: true);
            }
            else
            {
                var contract = await _contractManager.CreateAsync(
                    eventData.DocumentId,
                    eventData.DocumentTypeCode,
                    fields);
                await _contractRepository.InsertAsync(contract, autoSave: true);
            }
        }
    }

    public virtual async Task HandleEventAsync(DocumentDeletedEto eventData)
    {
        using (_currentTenant.Change(eventData.TenantId))
        {
            var contract = await _contractRepository.FindByDocumentIdAsync(eventData.DocumentId);
            if (contract == null)
            {
                return;
            }

            contract.ArchiveBecauseDocumentDeleted();
            await _contractRepository.UpdateAsync(contract, autoSave: true);
        }
    }

    public virtual async Task HandleEventAsync(DocumentRestoredEto eventData)
    {
        using (_currentTenant.Change(eventData.TenantId))
        {
            var contract = await _contractRepository.FindByDocumentIdAsync(eventData.DocumentId);
            if (contract == null)
            {
                return;
            }

            contract.RestoreBecauseDocumentRestored();
            await _contractRepository.UpdateAsync(contract, autoSave: true);
        }
    }

    public virtual async Task HandleEventAsync(DocumentPermanentlyDeletedEto eventData)
    {
        using (_currentTenant.Change(eventData.TenantId))
        {
            var contract = await _contractRepository.FindByDocumentIdAsync(eventData.DocumentId);
            if (contract == null)
            {
                return;
            }

            // Document 已不可恢复，派生 Contract 失去数据源头 → 物理删除
            // Contract 基类是 AuditedAggregateRoot（无 ISoftDelete），DeleteAsync 即真正物理删除
            await _contractRepository.DeleteAsync(contract, autoSave: true);
        }
    }

    protected virtual async Task<ContractExtractionResult> ExtractFieldsAsync(
        string extractedText,
        string documentTypeCode)
    {
        var instructions = await BuildExtractionInstructionsAsync(documentTypeCode);
        // WithValidationRetry wraps the agent with a sense-check loop: on validator failure
        // the rule-violations are appended as a user-role feedback message and the agent is
        // called again. Exhausting retries returns the last response unchanged so the
        // ExtractionConfidence-based PendingReview routing still kicks in downstream.
        var agent = new ChatClientAgent(
                _chatClient,
                instructions: instructions)
            .WithValidationRetry(_extractionValidator, _logger);

        var run = await agent.RunAsync<ContractExtractionResult>(extractedText);
        return run.Result ?? new ContractExtractionResult();
    }

    protected virtual async Task<string> BuildExtractionInstructionsAsync(string documentTypeCode)
    {
        var examples = await _exampleProvider.GetExamplesAsync(documentTypeCode);
        if (examples.Count == 0)
        {
            return ContractAgentInstructions.SystemPrompt;
        }

        var sb = new StringBuilder(ContractAgentInstructions.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("以下は人間が修正済みの抽出例です。同じ種類の誤りを避けてください。");

        foreach (var example in examples)
        {
            if (!string.IsNullOrWhiteSpace(example.SourceExcerpt))
            {
                sb.AppendLine("入力抜粋:");
                sb.AppendLine(example.SourceExcerpt);
            }

            sb.AppendLine("修正済み JSON:");
            sb.AppendLine(example.CorrectedJson);
        }

        return sb.ToString();
    }
}
