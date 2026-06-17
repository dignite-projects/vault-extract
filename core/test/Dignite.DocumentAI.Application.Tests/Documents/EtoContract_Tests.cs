using System;
using System.Text.Json;
using Dignite.DocumentAI.Abstractions.Documents;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.Documents;

/// <summary>
/// Exit ETO contract tests (issue #188).
/// <para>
/// Verifies that the <c>init</c>-only / <c>required</c> changes introduced by #188 did not break the
/// System.Text.Json round-trip. ABP's built-in transactional outbox serializes ETOs to JSON and writes them
/// to <c>AbpEventOutbox.EventData</c>; a background worker reads and deserializes them before dispatching to
/// handlers. If round-trip fails, the whole exit contract is invalid.
/// </para>
/// <para>
/// This does not test ABP outbox itself, which is framework behavior. It tests compatibility between our
/// ETO shape and System.Text.Json. <see cref="System.Text.Json"/> supports <c>init</c>-only setters on .NET 5+;
/// the <c>required</c> keyword only affects compile-time object-initializer checks and does not block
/// deserialization, because reflection can set it.
/// </para>
/// </summary>
public class EtoContract_Tests
{
    private static readonly DateTime SampleEventTime =
        new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DocumentUploadedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentUploadedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            FileName = "x.pdf",
            FileSize = 1024,
            ContentType = "application/pdf"
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.TenantId.ShouldBe(eto.TenantId);
        roundTrip.EventTime.ShouldBe(eto.EventTime);
        roundTrip.FileName.ShouldBe(eto.FileName);
        roundTrip.FileSize.ShouldBe(eto.FileSize);
        roundTrip.ContentType.ShouldBe(eto.ContentType);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void OCRCompletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new OCRCompletedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = SampleEventTime,
            UsedOcr = true,
            // Non-zero so a serialization regression (getter-only / [JsonIgnore] / rename) is actually caught;
            // at the default 0 the round-trip would survive a broken contract (#306 review).
            FigureOcrCount = 3
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.EventTime.ShouldBe(eto.EventTime);
        roundTrip.UsedOcr.ShouldBeTrue();
        roundTrip.FigureOcrCount.ShouldBe(3);
    }

    [Fact]
    public void DocumentClassifiedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentClassifiedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.93
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
        roundTrip.ClassificationConfidence.ShouldBe(0.93);
        roundTrip.EventTime.ShouldBe(eto.EventTime);
    }

    [Fact]
    public void FieldsExtractedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new FieldsExtractedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general",
            FieldCount = 3
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.FieldCount.ShouldBe(3);
        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
    }

    [Fact]
    public void DocumentReadyEto_RoundTrips_Through_SystemTextJson()
    {
        var originDocumentId = Guid.NewGuid();
        var eto = new DocumentReadyEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = SampleEventTime,
            DocumentTypeCode = "contract.general",
            // #346: container marker. Non-default so a serialization regression (getter-only / rename) is caught.
            IsContainer = true,
            // #306: provenance link for a Scenario B sub-document. Non-null so a serialization regression is caught.
            OriginDocumentId = originDocumentId
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentTypeCode.ShouldBe("contract.general");
        roundTrip.EventTime.ShouldBe(eto.EventTime);
        roundTrip.IsContainer.ShouldBeTrue();
        roundTrip.OriginDocumentId.ShouldBe(originDocumentId);
    }

    [Fact]
    public void DocumentDeletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentDeletedEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void DocumentPermanentlyDeletedEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentPermanentlyDeletedEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
        roundTrip.Version.ShouldBe("1.0");
    }

    [Fact]
    public void DocumentRestoredEto_RoundTrips_Through_SystemTextJson()
    {
        var eto = new DocumentRestoredEto
        {
            DocumentId = Guid.NewGuid(),
            EventTime = SampleEventTime
        };

        var roundTrip = RoundTrip(eto);

        roundTrip.DocumentId.ShouldBe(eto.DocumentId);
    }

    [Fact]
    public void EventTime_Missing_From_Json_Throws_On_Deserialize()
    {
        // Verify fail-fast behavior for the `required` keyword during System.Text.Json (.NET 7+) deserialization:
        // JSON without EventTime throws JsonException, so downstream workers do not receive a default(DateTime)
        // event. This triggers outbox retry or inbox dead-letter instead of silently swallowing bad data.
        var jsonWithoutEventTime = """
            {
              "DocumentId": "00000000-0000-0000-0000-000000000001",
              "FileSize": 1024
            }
            """;

        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<DocumentUploadedEto>(jsonWithoutEventTime));
    }

    private static T RoundTrip<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value);
        var roundTrip = JsonSerializer.Deserialize<T>(json);
        roundTrip.ShouldNotBeNull();
        return roundTrip;
    }
}
