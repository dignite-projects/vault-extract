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
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Ocr.PaddleOcr;

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
        // SchemaName includes the model identifier so downstream consumers can choose how to parse
        // bbox / block structure.
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
        // #210: archive the raw sidecar JSON, including blocks with per-page / per-line text + page.
        // Coordinate / confidence truthfulness depends on the model. PP-StructureV3 / PaddleOCR-VL,
        // including the host current default, only reach page-level output: bbox is always the
        // [0,0,0,0] placeholder and confidence is always 1.0; see sidecar server.py
        // _process_structure / _process_vl. Real line-level bbox exists only with PP-OCRv4 when the
        // request has include_bboxes=true, and this provider does not currently send that parameter.
        // Under default configuration, the archived payload therefore has no usable coordinates /
        // confidence; solve data capture first before implementing future Layer 3 parsing.
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

        var client = _httpClientFactory.CreateClient(ExtractPaddleOcrModule.HttpClientName);
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
        // Markdown-first: PP-StructureV3 / PaddleOCR-VL modes return structured Markdown. Modes such
        // as PP-OCRv4 that return only raw_text are wrapped by the Provider into flat Markdown
        // paragraphs, keeping plain-text-to-Markdown translation out of the upstream orchestrator.
        return !string.IsNullOrEmpty(result.Markdown)
            ? result.Markdown
            : WrapParagraphs(result.RawText);
    }

    private static string WrapParagraphs(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        // Convert newline-separated plain-text paragraphs into flat Markdown paragraphs separated by
        // blank lines. This is the Provider-side minimum implementation of the Markdown-first
        // contract.
        var paragraphs = rawText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private sealed class PaddleOcrResponse
    {
        /// <summary>Plain-text output. When the sidecar mode provides only raw_text, the Provider wraps it into flat Markdown.</summary>
        [JsonPropertyName("raw_text")]
        public string RawText { get; set; } = string.Empty;

        /// <summary>Populated by PP-StructureV3 / PaddleOCR-VL models; null in PP-OCRv4 mode.</summary>
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
