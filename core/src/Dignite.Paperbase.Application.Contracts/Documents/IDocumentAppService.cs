using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Paperbase.Documents;

public interface IDocumentAppService : IApplicationService
{
    Task<DocumentDto> GetAsync(Guid id);

    Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input);

    Task<DocumentDto> UploadAsync(UploadDocumentInput input);

    Task<IRemoteStreamContent> GetBlobAsync(Guid id);

    Task DeleteAsync(Guid id);

    Task PermanentDeleteAsync(Guid id);

    Task RestoreAsync(Guid id);

    Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input);

    /// <summary>
    /// 操作员主动修正分类——任意状态下都允许覆写到新类型。
    /// 行为：写入 DocumentTypeCode/ReviewStatus=Reviewed/Confidence=1.0，发布
    /// <see cref="Abstractions.Documents.DocumentClassifiedEto"/>（经 ABP transactional outbox 投递）。
    /// 下游业务消费方可订阅 DocumentClassifiedEto 来重跑各自的字段抽取——按
    /// <c>(DocumentId, EventType, EventTime)</c> 自行幂等以处理 at-least-once 重投。
    /// </summary>
    Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input);

    /// <summary>
    /// 操作员拒绝待审核文档——文档落到 Failed 生命周期。
    /// <para>
    /// 拒绝是"当前数字化结果不可用 / 无法归类"的终态结论：保留原始文件、已提取 Markdown、OCR confidence 与拒绝原因用于审计；
    /// 不在本路径提供重跑或替换源文件能力，需要重试由操作员重新上传。
    /// </para>
    /// </summary>
    Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input);

    Task RetryPipelineAsync(Guid id, RetryPipelineInput input);

    /// <summary>
    /// 「重新识别」（#263）——让 AI 在**现有 Markdown** 上重跑「自动分类」workflow → 级联重抽字段，**不重新 OCR**。
    /// <para>
    /// 区别于 <see cref="ReclassifyAsync"/>（操作员**人工指定**类型、同步落库、不跑 LLM）与
    /// <see cref="RetryPipelineAsync"/>（仅 <c>Failed</c> 的 run 可重试）：本路径对任意**已完成文本提取**的文档
    /// 重排 classification job（LLM 依最新类型/字段说明自动重判）。高置信度发布
    /// <see cref="Abstractions.Documents.DocumentClassifiedEto"/>（经 transactional outbox），
    /// 由 <c>FieldExtractionEventHandler</c> 级联重抽字段；低置信度落「待人工审核」队列。
    /// </para>
    /// <para>
    /// ⚠️ 会**覆盖**既有分类结果（含操作员人工确认过的类型）与级联重抽时操作员手改过的字段值——
    /// 调用方 UI 须先确认。文档在回收站、或尚未产出 Markdown、或分类正在进行时拒绝。
    /// </para>
    /// </summary>
    Task RerecognizeAsync(Guid id);

    /// <summary>
    /// 操作员手改类型绑定字段抽取结果（个别纠错）。整体替换该文档的字段值集合；
    /// 每个 key 必须是该文档所属层、该 DocumentType 下已定义的 <see cref="FieldDefinition.Name"/>。
    /// 完成后复用 <see cref="Abstractions.Documents.FieldsExtractedEto"/> 重发，下游按
    /// <c>(DocumentId, EventType, EventTime)</c> 幂等吸收、回拉最新字段值。
    /// 大面积错误应走重跑 text-extraction / 重新上传，而非本路径批量修补。
    /// </summary>
    Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input);

    /// <summary>
    /// 改派文档所属文件柜（#257）——人工组织维度，正交于 pipeline，不触发后续 Run、不发出口事件。
    /// <paramref name="input"/>.CabinetId 为 null 表示移出文件柜（未归类）；非 null 须为当前层已存在的柜。
    /// </summary>
    Task<DocumentDto> UpdateCabinetAsync(Guid id, UpdateDocumentCabinetInput input);
}
