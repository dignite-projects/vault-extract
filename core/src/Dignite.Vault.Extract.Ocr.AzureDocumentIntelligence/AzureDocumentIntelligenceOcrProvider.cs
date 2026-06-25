using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence;

public class AzureDocumentIntelligenceOcrProvider : IOcrProvider, ITransientDependency
{
    // The channel layer always uses prebuilt-layout because it outputs structured Markdown with
    // headings / tables and matches Markdown-first. This is intentionally not exposed as host
    // configuration: prebuilt-read outputs only plain text and would break Markdown-first, while
    // business prebuilts such as invoice / contract belong to downstream business modules. Neither is
    // a channel-layer OCR option.
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
        // #210: archive Azure DI's raw AnalyzeResult JSON response as native payload, including
        // out-of-band spatial signals such as bbox / polygon / spans / table cells.
        var bytes = rawResponse?.ToArray();
        if (bytes is null || bytes.Length == 0) return;

        ocrResult.NativePayloadContent = bytes;
        ocrResult.NativePayloadContentType = "application/json";
        ocrResult.NativePayloadSchemaName = "AzureDocumentIntelligence.AnalyzeResult";
    }

    // Test seam: lets a stubbed DocumentIntelligenceClient (e.g. one configured with a fake
    // HttpPipelineTransport) be substituted so the provider can be exercised without contacting Azure.
    // Production builds the real client from the configured endpoint + key. Kept protected virtual per the
    // module-extensibility convention; it does not change the IOcrProvider contract or any egress boundary.
    protected virtual DocumentIntelligenceClient CreateClient()
    {
        return new DocumentIntelligenceClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));
    }

    private async Task<(AnalyzeResult Result, BinaryData RawResponse)> AnalyzeAsync(
        Stream fileStream,
        string modelId,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();

        BinaryData binaryData;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms, cancellationToken);
            binaryData = BinaryData.FromBytes(ms.ToArray());
        }

        var analyzeOptions = new AnalyzeDocumentOptions(modelId, binaryData)
        {
            // Markdown-first execution point: Azure DI OutputContentFormat defaults to Text, so
            // Markdown must be explicitly requested to get structured Content with headings / tables /
            // lists. Requires api-version 2024-11-30+ and SDK 1.0+. Do not remove this; removing it
            // makes prebuilt-layout degrade to a plain text stream and breaks Markdown-first.
            OutputContentFormat = DocumentContentFormat.Markdown
        };

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken);
        // operation.GetRawResponse().Content is the raw JSON response body from the completed polling
        // response, including the full analyzeResult (bbox / polygon / spans / cell). It is more
        // faithful than reflection-serializing the model. The LRO completion response is buffered by
        // default, so .Content is safe to read.
        return (operation.Value, operation.GetRawResponse().Content);
    }

    private static string BuildMarkdown(AnalyzeResult analyzeResult)
    {
        // analyzeResult.Content is already Markdown. If Azure returns empty content, fall back to line
        // text joined into flat Markdown paragraphs. The Provider fills this itself and does not leak
        // plain-text-to-Markdown translation responsibility to the upstream orchestrator.
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
