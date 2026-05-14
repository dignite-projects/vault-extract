using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public virtual async Task<OcrResult> RecognizeAsync(Stream fileStream, OcrOptions options)
    {
        var languages = options.LanguageHints.Count > 0
            ? options.LanguageHints
            : _options.Languages;

        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms);
        var fileBytes = ms.ToArray();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        if (!string.IsNullOrEmpty(options.ContentType))
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(options.ContentType);
        content.Add(fileContent, "file", "document");
        content.Add(new StringContent(string.Join(",", languages)), "languages");
        content.Add(new StringContent(_options.ModelName), "model_name");

        var client = _httpClientFactory.CreateClient(PaperbasePaddleOcrModule.HttpClientName);
        var response = await client.PostAsync($"{_options.Endpoint.TrimEnd('/')}/ocr", content);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"PaddleOCR server returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<PaddleOcrResponse>(json)
            ?? throw new InvalidOperationException("PaddleOCR server returned an empty response.");

        // Markdown-first：PP-StructureV3 / PaddleOCR-VL 模式返回结构化 Markdown；
        // PP-OCRv4 等只返回 raw_text 的模式由 Provider 内部包成扁平 Markdown 段落，
        // 不把 plain-text-to-markdown 翻译职责泄漏给上游 orchestrator。
        var markdown = !string.IsNullOrEmpty(result.Markdown)
            ? result.Markdown
            : WrapParagraphs(result.RawText);

        var confidence = result.Blocks is { Count: > 0 }
            ? result.Blocks.Average(b => b.Confidence)
            : 0;

        return new OcrResult
        {
            Markdown = markdown,
            Confidence = confidence,
            DetectedLanguage = result.DetectedLanguage,
            PageCount = result.PageCount
        };
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

        /// <summary>Sidecar 行级 OCR 结果。Provider 内部只读其 <c>confidence</c> 用于整体置信度均值，
        /// 不再向外暴露 bbox/text。Sidecar 协议未来若简化 schema 可安全省略此字段（按 0 处理）。</summary>
        [JsonPropertyName("blocks")]
        public List<PaddleOcrLineConfidence>? Blocks { get; set; }

        [JsonPropertyName("detected_language")]
        public string? DetectedLanguage { get; set; }

        [JsonPropertyName("page_count")]
        public int PageCount { get; set; }
    }

    private sealed class PaddleOcrLineConfidence
    {
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }
}
