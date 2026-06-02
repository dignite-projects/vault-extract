using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ocr;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Ocr.PaddleOcr;

public class PaddleOcrProvider : IOcrProvider, ITransientDependency
{
    private readonly PaddleOcrOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public PaddleOcrProvider(
        IOptions<PaddleOcrOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    public virtual async Task<OcrResult> RecognizeAsync(
        Stream fileStream,
        OcrOptions options,
        CancellationToken cancellationToken = default)
    {
        var modelName = _options.ModelName;
        var rawJson = await SendAsync(
            fileStream,
            options.LanguageHints,
            options.ContentType,
            modelName,
            cancellationToken);

        var result = JsonSerializer.Deserialize<PaddleOcrResponse>(rawJson)
            ?? throw new InvalidOperationException("PaddleOCR server returned an empty response.");

        var markdown = BuildMarkdown(result);
        // SchemaName 包含 model 标识，便于下游消费方判断如何解析 bbox/block 结构。
        var schemaName = $"PaddleOCR/{result.ProviderModelName ?? modelName}";
        var ocrResult = new OcrResult
        {
            Markdown = markdown,
            DetectedLanguage = result.DetectedLanguage,
            ProviderName = result.ProviderName ?? "PaddleOCR"
        };
        FillNativePayload(ocrResult, rawJson, schemaName);
        return ocrResult;
    }

    private static void FillNativePayload(OcrResult ocrResult, string rawJson, string schemaName)
    {
        // #210：归档 sidecar 原始 JSON（含 blocks：每页/每行 text + page）。
        // ⚠️ 坐标 / 置信度的真实性取决于模型：PP-StructureV3 / PaddleOCR-VL（含 host 当前默认）只到页级，
        //    bbox 恒为占位 [0,0,0,0]、confidence 恒为 1.0（见 sidecar server.py 的 _process_structure / _process_vl）；
        //    真实行级 bbox 仅 PP-OCRv4 且请求带 include_bboxes=true 时才有——而本 provider 当前未传该参数。
        //    即默认配置下归档的 payload 没有可用坐标 / 置信度，将来做 Layer 3 解析前需先解决取数。
        if (string.IsNullOrEmpty(rawJson)) return;

        ocrResult.NativePayloadContent = Encoding.UTF8.GetBytes(rawJson);
        ocrResult.NativePayloadContentType = "application/json";
        ocrResult.NativePayloadSchemaName = schemaName;
    }

    private async Task<string> SendAsync(
        Stream fileStream,
        IList<string> languageHints,
        string contentType,
        string modelName,
        CancellationToken cancellationToken)
    {
        var languages = languageHints.Count > 0
            ? languageHints
            : _options.Languages;

        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, cancellationToken);
        var fileBytes = ms.ToArray();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        if (!string.IsNullOrEmpty(contentType))
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", "document");
        content.Add(new StringContent(string.Join(",", languages)), "languages");
        content.Add(new StringContent(modelName), "model_name");

        var client = _httpClientFactory.CreateClient(PaperbasePaddleOcrModule.HttpClientName);
        var response = await client.PostAsync($"{_options.Endpoint.TrimEnd('/')}/ocr", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"PaddleOCR server returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildMarkdown(PaddleOcrResponse result)
    {
        // Markdown-first：PP-StructureV3 / PaddleOCR-VL 模式返回结构化 Markdown；
        // PP-OCRv4 等只返回 raw_text 的模式由 Provider 内部包成扁平 Markdown 段落，
        // 不把 plain-text-to-markdown 翻译职责泄漏给上游 orchestrator。
        return !string.IsNullOrEmpty(result.Markdown)
            ? result.Markdown
            : WrapParagraphs(result.RawText);
    }

    private static string WrapParagraphs(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        // 把换行分隔的纯文本段落转成 Markdown 扁平段落（空行分隔）。
        // 这是 Provider 侧履行 Markdown-first 契约的最低实现。
        var paragraphs = rawText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private sealed class PaddleOcrResponse
    {
        /// <summary>纯文本输出。当 sidecar 模式只提供 raw_text 时由 Provider 包成扁平 Markdown。</summary>
        [JsonPropertyName("raw_text")]
        public string RawText { get; set; } = string.Empty;

        /// <summary>PP-StructureV3 / PaddleOCR-VL 模型填充；PP-OCRv4 模式下为 null。</summary>
        [JsonPropertyName("markdown")]
        public string? Markdown { get; set; }

        [JsonPropertyName("detected_language")]
        public string? DetectedLanguage { get; set; }

        [JsonPropertyName("page_count")]
        public int PageCount { get; set; }

        [JsonPropertyName("provider_name")]
        public string? ProviderName { get; set; }

        [JsonPropertyName("provider_model")]
        public string? ProviderModelName { get; set; }
    }
}
