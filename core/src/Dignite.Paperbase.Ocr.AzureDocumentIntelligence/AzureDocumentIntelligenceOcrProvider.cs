using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Dignite.Paperbase.Ocr;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Ocr.AzureDocumentIntelligence;

public class AzureDocumentIntelligenceOcrProvider : IOcrProvider, ITransientDependency
{
    private readonly AzureDocumentIntelligenceOptions _options;

    public AzureDocumentIntelligenceOcrProvider(IOptions<AzureDocumentIntelligenceOptions> options)
    {
        _options = options.Value;
    }

    public virtual async Task<OcrResult> RecognizeAsync(Stream fileStream, OcrOptions options)
    {
        var client = new DocumentIntelligenceClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));

        BinaryData binaryData;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms);
            binaryData = BinaryData.FromBytes(ms.ToArray());
        }

        var analyzeOptions = new AnalyzeDocumentOptions(_options.ModelId, binaryData)
        {
            // 启用 Markdown 输出（需 api-version 2024-11-30+，SDK 1.0+）。
            // analyzeResult.Content 直接是带标题/表格/列表的 Markdown。
            OutputContentFormat = DocumentContentFormat.Markdown
        };
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions);
        var analyzeResult = operation.Value;

        // Confidence: page.Lines 命中 Spans 视为高置信，否则给 0.9 兜底；整体取均值。
        double totalConfidence = 0;
        int lineCount = 0;
        foreach (var page in analyzeResult.Pages ?? [])
        {
            foreach (var line in page.Lines ?? [])
            {
                totalConfidence += line.Spans?.Any() == true ? 1.0 : 0.9;
                lineCount++;
            }
        }

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

        return new OcrResult
        {
            Markdown = markdown ?? string.Empty,
            Confidence = lineCount > 0 ? totalConfidence / lineCount : 0,
            DetectedLanguage = analyzeResult.Languages?.FirstOrDefault()?.Locale,
            PageCount = analyzeResult.Pages?.Count ?? 0
        };
    }
}
