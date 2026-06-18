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
/// #355 domain-level tests: the <see cref="ContainerMarkerSetEvent"/> local event must be raised on a false→true
/// transition of <see cref="Document.IsContainer"/> <b>only when the document previously had a concrete type</b>
/// (a re-recognition that turned an already-classified document into a container, whose former typed record
/// downstream must retract). A fresh upload first detected as a container — or a document already a container —
/// raises nothing. Mirror of <see cref="ContainerMarkerClearedEvent_Tests"/> (the container→type direction).
/// </summary>
public class ContainerMarkerSetEvent_Tests
{
    [Fact]
    public void Reclassifying_A_Concrete_Typed_Document_To_Container_Raises_The_Event()
    {
        var doc = NewDocument();
        // First classified to a concrete type (downstream may now hold a record), then re-recognized as a container.
        ApplyAutomaticClassificationResult(doc, TypeId("invoice.general"), 0.95);

        MarkAsContainer(doc);

        doc.IsContainer.ShouldBeTrue();
        doc.DocumentTypeId.ShouldBeNull();
        LocalEvents(doc).OfType<ContainerMarkerSetEvent>()
            .ShouldContain(e => e.DocumentId == doc.Id && e.TenantId == doc.TenantId);
    }

    [Fact]
    public void Fresh_Document_First_Detected_As_Container_Raises_No_Event()
    {
        var doc = NewDocument();

        MarkAsContainer(doc);

        doc.IsContainer.ShouldBeTrue();
        // No prior type means no downstream record to retract — a spurious retraction would confuse consumers.
        LocalEvents(doc).OfType<ContainerMarkerSetEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void Re_Detecting_An_Existing_Container_As_Container_Raises_No_Event()
    {
        var doc = NewDocument();
        MarkAsContainer(doc);

        MarkAsContainer(doc);

        LocalEvents(doc).OfType<ContainerMarkerSetEvent>().ShouldBeEmpty();
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

    // AggregateRoot exposes its queued local events through IGeneratesDomainEvents.GetLocalEvents().
    private static object[] LocalEvents(Document doc)
        => ((IGeneratesDomainEvents)doc).GetLocalEvents().Select(r => r.EventData).ToArray();

    private static Guid TypeId(string typeCode)
        => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + typeCode)));
}
