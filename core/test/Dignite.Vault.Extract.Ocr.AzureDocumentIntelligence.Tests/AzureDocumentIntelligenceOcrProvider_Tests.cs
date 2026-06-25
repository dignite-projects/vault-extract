using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Pipeline;
using Dignite.Vault.Extract.Ocr;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence;

/// <summary>
/// Unit tests for <see cref="AzureDocumentIntelligenceOcrProvider"/> at the Azure SDK boundary. A test
/// subclass overrides the <c>CreateClient()</c> seam to return a <see cref="DocumentIntelligenceClient"/>
/// wired to an in-process <see cref="MockTransport"/>, which short-circuits the HTTP pipeline and plays back
/// the Document Intelligence long-running-operation protocol (POST 202 with Operation-Location, then a GET
/// that reports <c>succeeded</c>). No Azure resource is contacted.
/// </summary>
public class AzureDocumentIntelligenceOcrProvider_Tests
{
    private const string Endpoint = "https://fake.cognitiveservices.azure.com";

    private static MemoryStream FakeImage() => new(new byte[] { 0xFF, 0xD8, 0xFF });

    private static string SucceededBody(string content, string? locale = "en")
    {
        var languages = locale is null
            ? ""
            : $$"""
                ,
                "languages": [ { "spans": [ { "offset": 0, "length": 10 } ], "locale": "{{locale}}", "confidence": 0.95 } ]
                """;
        return $$"""
            {
              "status": "succeeded",
              "createdDateTime": "2024-01-01T00:00:00Z",
              "lastUpdatedDateTime": "2024-01-01T00:00:01Z",
              "analyzeResult": {
                "apiVersion": "2024-11-30",
                "modelId": "prebuilt-layout",
                "stringIndexType": "textElements",
                "content": {{System.Text.Json.JsonSerializer.Serialize(content)}},
                "pages": []{{languages}}
              }
            }
            """;
    }

    private static AzureDocumentIntelligenceOcrProvider Provider(MockTransport transport)
        => new TestProvider(new AzureDocumentIntelligenceOptions { Endpoint = Endpoint, ApiKey = "fake-key" }, transport);

    [Fact]
    public async Task Should_Request_The_Prebuilt_Layout_Model_With_Markdown_Output()
    {
        var transport = new MockTransport(SucceededBody("# Heading"));
        var result = await Provider(transport).RecognizeAsync(FakeImage(), new OcrOptions());

        transport.AnalyzeRequestUri.ShouldContain("prebuilt-layout");
        // Markdown-first execution point: the layout model must be asked for Markdown output, not plain text.
        transport.AnalyzeRequestUri.ShouldContain("outputContentFormat=markdown");
        result.ProviderName.ShouldBe("AzureDocumentIntelligence");
    }

    [Fact]
    public async Task Should_Pass_Through_Markdown_Content_And_Detected_Language()
    {
        var transport = new MockTransport(SucceededBody("# Invoice\n\n| Item | Price |", locale: "ja"));

        var result = await Provider(transport).RecognizeAsync(FakeImage(), new OcrOptions());

        result.Markdown.ShouldBe("# Invoice\n\n| Item | Price |");
        result.DetectedLanguage.ShouldBe("ja");
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Archive_The_Raw_Response_As_Native_Payload()
    {
        var transport = new MockTransport(SucceededBody("# Heading"));

        var result = await Provider(transport).RecognizeAsync(FakeImage(), new OcrOptions());

        // #210: the raw AnalyzeResult JSON (including out-of-band bbox/span signals) is archived verbatim.
        result.NativePayloadContentType.ShouldBe("application/json");
        result.NativePayloadSchemaName.ShouldBe("AzureDocumentIntelligence.AnalyzeResult");
        result.NativePayloadContent.ShouldNotBeNull();
        Encoding.UTF8.GetString(result.NativePayloadContent!).ShouldContain("analyzeResult");
    }

    [Fact]
    public async Task Should_Fall_Back_To_Joined_Line_Text_When_Content_Is_Empty()
    {
        // Azure returned no top-level Markdown content; the provider rebuilds flat Markdown paragraphs from
        // the page lines itself rather than leaking that responsibility to the orchestrator.
        var body = """
            {
              "status": "succeeded",
              "analyzeResult": {
                "apiVersion": "2024-11-30",
                "modelId": "prebuilt-layout",
                "stringIndexType": "textElements",
                "content": "",
                "pages": [
                  {
                    "pageNumber": 1,
                    "angle": 0,
                    "width": 8.5,
                    "height": 11,
                    "unit": "inch",
                    "spans": [],
                    "words": [],
                    "lines": [
                      { "content": "line A", "polygon": [], "spans": [] },
                      { "content": "line B", "polygon": [], "spans": [] }
                    ]
                  }
                ]
              }
            }
            """;
        var transport = new MockTransport(body);

        var result = await Provider(transport).RecognizeAsync(FakeImage(), new OcrOptions());

        result.Markdown.ShouldBe(string.Join(Environment.NewLine + Environment.NewLine, "line A", "line B"));
    }

    [Fact]
    public async Task Should_Surface_Service_Errors_As_Exceptions()
    {
        // A 400 from the analyze call is non-retryable; the SDK raises RequestFailedException, which the
        // provider propagates (it does not swallow the failure into a blank success).
        var transport = new MockTransport(getBody: "", postStatus: 400);

        await Should.ThrowAsync<RequestFailedException>(() =>
            Provider(transport).RecognizeAsync(FakeImage(), new OcrOptions()));
    }

    // Injects the in-process transport through the provider's CreateClient() seam.
    private sealed class TestProvider : AzureDocumentIntelligenceOcrProvider
    {
        private readonly MockTransport _transport;

        public TestProvider(AzureDocumentIntelligenceOptions options, MockTransport transport)
            : base(Options.Create(options))
        {
            _transport = transport;
        }

        protected override DocumentIntelligenceClient CreateClient()
            => new(
                new Uri(Endpoint),
                new AzureKeyCredential("fake-key"),
                new DocumentIntelligenceClientOptions { Transport = _transport });
    }

    // Short-circuits HttpClientTransport (reusing its real Request building) to return canned responses,
    // playing back the LRO without any socket. POST -> 202 + Operation-Location, GET -> 200 succeeded.
    private sealed class MockTransport : HttpClientTransport
    {
        private readonly string _getBody;
        private readonly int _postStatus;

        public MockTransport(string getBody, int postStatus = 202)
        {
            _getBody = getBody;
            _postStatus = postStatus;
        }

        public string AnalyzeRequestUri { get; private set; } = string.Empty;

        public override void Process(HttpMessage message) => SetResponse(message);

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            SetResponse(message);
            return default;
        }

        private void SetResponse(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Post)
            {
                AnalyzeRequestUri = message.Request.Uri.ToString();

                if (_postStatus != 202)
                {
                    var error = new MockResponse(_postStatus,
                        Encoding.UTF8.GetBytes("""{ "error": { "code": "InvalidRequest", "message": "bad input" } }"""));
                    error.AddHeader("Content-Type", "application/json");
                    message.Response = error;
                    return;
                }

                var accepted = new MockResponse(202, Array.Empty<byte>());
                accepted.AddHeader(
                    "Operation-Location",
                    $"{Endpoint}/documentintelligence/documentModels/prebuilt-layout/analyzeResults/test-id?api-version=2024-11-30");
                accepted.AddHeader("Retry-After", "0");
                message.Response = accepted;
                return;
            }

            var ok = new MockResponse(200, Encoding.UTF8.GetBytes(_getBody));
            ok.AddHeader("Content-Type", "application/json");
            message.Response = ok;
        }
    }

    // A self-owned Response whose content stream is never disposed by the pipeline, so the LRO status check
    // and the provider's later GetRawResponse().Content read both succeed.
    private sealed class MockResponse : Response
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public MockResponse(int status, byte[] content)
        {
            Status = status;
            ContentStream = new MemoryStream(content, writable: false);
        }

        public override int Status { get; }
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public void AddHeader(string name, string value) => _headers[name] = value;

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            foreach (var header in _headers)
            {
                yield return new HttpHeader(header.Key, header.Value);
            }
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
            => _headers.TryGetValue(name, out value);

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            if (_headers.TryGetValue(name, out var value))
            {
                values = new[] { value };
                return true;
            }

            values = null;
            return false;
        }

        // Intentionally does not dispose ContentStream: the SDK reads it more than once (status poll, then
        // the archived raw payload), and the pipeline would otherwise close it between reads.
        public override void Dispose()
        {
        }
    }
}
