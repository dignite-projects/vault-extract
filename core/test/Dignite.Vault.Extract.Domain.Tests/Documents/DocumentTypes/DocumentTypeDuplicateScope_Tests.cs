using System;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// #651: <see cref="DocumentType.DuplicateScope"/> — the type-level setting that decides what counts as a
/// duplicate. The default matters as much as the value: <see cref="DuplicateDetectionScope.Layer"/> is the zero
/// value and the pre-#651 behaviour, which is what makes the upgrade change no existing type and no existing
/// document.
/// <para>
/// There is no "set just the scope" method to test. The scope moves only through the constructor or
/// <see cref="DocumentType.Update"/>, both of which take it as a <b>required</b> parameter, so a save path
/// cannot pick a duplicate rule by omission — which is exactly how the pack import came to reset it.
/// </para>
/// </summary>
public class DocumentTypeDuplicateScope_Tests
{
    [Fact]
    public void Layer_Is_The_Zero_Value_So_Existing_Rows_Keep_Todays_Behaviour()
    {
        CreateDocumentType(DuplicateDetectionScope.Layer).DuplicateScope.ShouldBe(DuplicateDetectionScope.Layer);

        // The persisted integers are a frozen contract, and the column's default is 0 — so Layer must be 0 or
        // every pre-#651 row would come back meaning something else.
        ((int)DuplicateDetectionScope.Layer).ShouldBe(0);
        ((int)DuplicateDetectionScope.Uploader).ShouldBe(1);
    }

    [Fact]
    public void Constructor_Carries_The_Scope()
    {
        CreateDocumentType(DuplicateDetectionScope.Uploader)
            .DuplicateScope.ShouldBe(DuplicateDetectionScope.Uploader);
    }

    [Fact]
    public void Update_Carries_The_Scope_Like_Every_Other_Property_And_Both_Ways()
    {
        var type = CreateDocumentType(DuplicateDetectionScope.Layer);

        type.Update("host.test", "Contract", null, 0.7, 0, DuplicateDetectionScope.Uploader);
        type.DuplicateScope.ShouldBe(DuplicateDetectionScope.Uploader);

        // And back again — the setting is not one-way.
        type.Update("host.test", "Contract", null, 0.7, 0, DuplicateDetectionScope.Layer);
        type.DuplicateScope.ShouldBe(DuplicateDetectionScope.Layer);
    }

    [Fact]
    public void Update_Leaves_The_Rest_Of_The_Type_Alone_When_Only_The_Scope_Moves()
    {
        var type = CreateDocumentType(DuplicateDetectionScope.Layer);

        type.Update("host.test", "Contract", null, 0.7, 0, DuplicateDetectionScope.Uploader);

        type.TypeCode.ShouldBe("host.test");
        type.DisplayName.ShouldBe("Contract");
        type.ConfidenceThreshold.ShouldBe(0.7);
        type.Priority.ShouldBe(0);
    }

    [Fact]
    public void Rejects_An_Undefined_Scope_Value_On_Both_Write_Paths()
    {
        // JSON deserialization turns an arbitrary integer into an enum without complaint, and the value is
        // persisted, so a row holding an undefined member would be read back as a scope no detection path knows
        // how to apply. Same exception family as ConfidenceThreshold's Check.Range: a malformed-input error,
        // not a business rule.
        Should.Throw<ArgumentException>(() => CreateDocumentType((DuplicateDetectionScope)42));

        var type = CreateDocumentType(DuplicateDetectionScope.Layer);
        Should.Throw<ArgumentException>(() =>
            type.Update("host.test", "Contract", null, 0.7, 0, (DuplicateDetectionScope)42));

        type.DuplicateScope.ShouldBe(DuplicateDetectionScope.Layer);
    }

    private static DocumentType CreateDocumentType(DuplicateDetectionScope duplicateScope) =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            typeCode: "host.test",
            displayName: "Contract",
            duplicateScope: duplicateScope);
}
