using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Xunit;

namespace Dignite.DocumentAI.Documents;

/// <summary>
/// #349 domain-level tests: the <see cref="ContainerMarkerClearedEvent"/> local event must be raised on a true→false
/// transition of <see cref="Document.IsContainer"/> (and only then), for both classification entry points — the
/// high-confidence automatic re-recognition (<c>ApplyAutomaticClassificationResult</c>) and the operator reclassify
/// (<c>ConfirmClassification</c>). A document that was never a container raises nothing.
/// </summary>
public class ContainerMarkerClearedEvent_Tests
{
    [Fact]
    public void ApplyAutomaticClassificationResult_On_A_Container_Raises_The_Event()
    {
        var doc = NewDocument();
        MarkAsContainer(doc);

        ApplyAutomaticClassificationResult(doc, TypeId("invoice.general"), 0.95);

        doc.IsContainer.ShouldBeFalse();
        LocalEvents(doc).OfType<ContainerMarkerClearedEvent>()
            .ShouldContain(e => e.DocumentId == doc.Id);
    }

    [Fact]
    public void ConfirmClassification_On_A_Container_Raises_The_Event()
    {
        var doc = NewDocument();
        MarkAsContainer(doc);

        ConfirmClassification(doc, TypeId("invoice.general"));

        doc.IsContainer.ShouldBeFalse();
        LocalEvents(doc).OfType<ContainerMarkerClearedEvent>()
            .ShouldContain(e => e.DocumentId == doc.Id);
    }

    [Fact]
    public void Classifying_A_Non_Container_Raises_No_Event()
    {
        var doc = NewDocument();

        ApplyAutomaticClassificationResult(doc, TypeId("invoice.general"), 0.95);

        doc.IsContainer.ShouldBeFalse();
        LocalEvents(doc).OfType<ContainerMarkerClearedEvent>().ShouldBeEmpty();
    }

    private static Document NewDocument()
        => new(
            Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

    private static void MarkAsContainer(Document doc)
        => typeof(Document)
            .GetMethod("MarkAsContainer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, null);

    private static void ApplyAutomaticClassificationResult(Document doc, Guid typeId, double confidence)
        => typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, [typeId, confidence]);

    private static void ConfirmClassification(Document doc, Guid typeId)
        => typeof(Document)
            .GetMethod("ConfirmClassification", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(doc, [typeId]);

    // AggregateRoot exposes its queued local events through IGeneratesDomainEvents.GetLocalEvents().
    private static object[] LocalEvents(Document doc)
        => ((IGeneratesDomainEvents)doc).GetLocalEvents().Select(r => r.EventData).ToArray();

    private static Guid TypeId(string typeCode)
        => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
