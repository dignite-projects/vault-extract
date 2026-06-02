using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Dignite.Paperbase.Ocr;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Ocr.AzureDocumentIntelligence;

public class AzureDocumentIntelligenceOcrProvider : IOcrProvider, ITransientDependency
{
    // 通道层固定使用 prebuilt-layout：它输出带标题/表格的结构化 Markdown，契合 Markdown-first。
    // 故意不暴露为 host 配置——prebuilt-read 只产纯文本会破坏 Markdown-first，业务 prebuilt
    // （invoice / contract 等）属下游业务范畴，二者都不是通道层应有的 OCR 选项。
    private const string ModelId = "prebuilt-layout";

    private readonly AzureDocumentIntelligenceOptions _options;

    public AzureDocumentIntelligenceOcrProvider(IOptions<AzureDocumentIntelligenceOptions> options)
    {
        _options = options.Value;
    }

    public virtual async Task<OcrResult> RecognizeAsync(
        Stream fileStream,
        OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        var (analyzeResult, rawResponse) = await AnalyzeAsync(fileStream, ModelId, cancellationToken);

        var markdown = BuildMarkdown(analyzeResult);

        var ocrResult = new OcrResult
        {
            Markdown = markdown,
            DetectedLanguage = analyzeResult.Languages?.FirstOrDefault()?.Locale,
            ProviderName = "AzureDocumentIntelligence"
        };
        FillNativePayload(ocrResult, rawResponse);
        return ocrResult;
    }

    private static void FillNativePayload(OcrResult ocrResult, BinaryData? rawResponse)
    {
        // #210：归档 Azure DI 的原始 AnalyzeResult JSON 响应（含 bbox / polygon / spans / 表格 cell 等
        // out-of-band 空间信号）作为原生 payload。
        var bytes = rawResponse?.ToArray();
        if (bytes is null || bytes.Length == 0) return;

        ocrResult.NativePayloadContent = bytes;
        ocrResult.NativePayloadContentType = "application/json";
        ocrResult.NativePayloadSchemaName = "AzureDocumentIntelligence.AnalyzeResult";
    }

    private async Task<(AnalyzeResult Result, BinaryData RawResponse)> AnalyzeAsync(
        Stream fileStream,
        string modelId,
        CancellationToken cancellationToken)
    {
        var client = new DocumentIntelligenceClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));

        BinaryData binaryData;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms, cancellationToken);
            binaryData = BinaryData.FromBytes(ms.ToArray());
        }

        var analyzeOptions = new AnalyzeDocumentOptions(modelId, binaryData)
        {
            // Markdown-first 执行点：Azure DI 的 OutputContentFormat 默认是 Text，必须显式请求 Markdown
            // 才能拿到带标题/表格/列表的结构化 Content（需 api-version 2024-11-30+、SDK 1.0+）。
            // 不可移除——移除会让 prebuilt-layout 退化成纯文本流，破坏 Markdown-first。
            OutputContentFormat = DocumentContentFormat.Markdown
        };

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken);
        // operation.GetRawResponse().Content 是完成轮询的原始 JSON 响应体（含完整 analyzeResult：bbox / polygon /
        // spans / cell）——比反射序列化 model 更忠实。LRO 完成响应默认缓冲，.Content 安全可读。
        return (operation.Value, operation.GetRawResponse().Content);
    }

    private static string BuildMarkdown(AnalyzeResult analyzeResult)
    {
        // analyzeResult.Content 已是 Markdown；若 Azure 返回空，回退到行级文本拼接成扁平 Markdown 段落。
        // Provider 负责自填，不把 plain-text-to-markdown 翻译职责泄漏给上游 orchestrator。
        var markdown = analyzeResult.Content;
        if (string.IsNullOrEmpty(markdown))
        {
            var paragraphs = (analyzeResult.Pages ?? [])
                .SelectMany(p => p.Lines ?? [])
                .Select(l => l.Content)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            markdown = string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
        }

        return markdown ?? string.Empty;
    }
}
