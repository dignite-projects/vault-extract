using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// DocumentType entity invariant tests. Focus: DisplayName control-character filtering.
/// DisplayName is now entered by admins through the UI, whereas it used to be a LocalizableString
/// compile-time constant. It is interpolated literally into the typeDescriptions section of the LLM
/// classification prompt, so newline / tab / other control-character prompt-injection vectors must be
/// blocked. Unicode letters, numbers, spaces, and punctuation are allowed, including Chinese and
/// Japanese text.
/// </summary>
public class DocumentTypeTests
{
    [Theory]
    [InlineData("Contract")]
    [InlineData("合同")]
    [InlineData("契約書")]
    [InlineData("Contract / Invoice")]   // slash / space allowed
    [InlineData("Type (general)")]       // parentheses allowed
    public void Should_Accept_Valid_DisplayName(string displayName)
    {
        var type = CreateDocumentType(displayName);
        type.DisplayName.ShouldBe(displayName);
    }

    [Theory]
    [InlineData("Contract\nIgnore previous")]   // \n newline: typical prompt-injection vector
    [InlineData("Contract\r\nIgnore")]          // \r\n
    [InlineData("Tab\there")]                   // tab
    [InlineData("Null\0byte")]                  // \0
    [InlineData("Vertical\vTab")]               // \v
    [InlineData("Form\fFeed")]                  // \f
    public void Should_Reject_DisplayName_With_Control_Chars(string displayName)
    {
        var ex = Should.Throw<BusinessException>(() => CreateDocumentType(displayName));
        ex.Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Also_Reject_Control_Chars()
    {
        // Valid at construction time, but the Update path must revalidate so admins cannot bypass entity
        // invariants through the Update API.
        var type = CreateDocumentType("Contract");
        Should.Throw<BusinessException>(() => type.Update("host.test", "Bad\nName", null, 0.7, 0))
            .Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Allow_TypeCode_Rename()
    {
        // #207: unlock TypeCode rename. Internal association now uses immutable id, so rename is no
        // longer forbidden at the entity layer.
        var type = CreateDocumentType("Contract");
        type.TypeCode.ShouldBe("host.test");

        type.Update("host.renamed-contract", "Contract", null, 0.7, 0);

        type.TypeCode.ShouldBe("host.renamed-contract");
    }

    [Fact]
    public void Update_Should_Still_Validate_TypeCode_Format()
    {
        // Unlocking rename does not skip the regex allowlist; invalid TypeCode is still rejected.
        var type = CreateDocumentType("Contract");
        Should.Throw<BusinessException>(() => type.Update("bad code", "Contract", null, 0.7, 0))
            .Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidCodeFormat);
    }

    [Theory]
    [InlineData("contract")]              // single segment, valid after v2 removed mandatory dot
    [InlineData("host.contract")]         // two segments
    [InlineData("host.case-file")]        // hyphen
    [InlineData("host.case_file")]        // underscore
    [InlineData("host.legal.contract")]   // multiple segments
    [InlineData("Host.Contract")]         // mixed case
    [InlineData("a")]                     // shortest
    public void Should_Accept_Valid_TypeCode(string typeCode)
    {
        var type = new DocumentType(
            Guid.NewGuid(), null,
            typeCode: typeCode,
            displayName: "Contract");
        type.TypeCode.ShouldBe(typeCode);
    }

    [Theory]
    [InlineData(".host")]                 // leading dot
    [InlineData("host.")]                 // trailing dot
    [InlineData("host..contract")]        // consecutive dots
    [InlineData("host contract")]         // space
    [InlineData("host\ncontract")]        // control character
    [InlineData("合同")]                  // Unicode
    [InlineData("host\"contract")]        // quote
    [InlineData("host;sql")]              // semicolon
    public void Should_Reject_TypeCode_With_Invalid_Chars(string typeCode)
    {
        var ex = Should.Throw<BusinessException>(() => new DocumentType(
            Guid.NewGuid(), null,
            typeCode: typeCode,
            displayName: "Contract"));
        ex.Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidCodeFormat);
    }

    // ---- Description (#262 classification helper text): same-origin control-character defense as DisplayName + nullable normalization ----

    [Theory]
    [InlineData("用于供应商采购合同，通常含甲乙双方、合同金额、交付与付款条款")]
    [InlineData("Supplier purchase contracts (parties, amount, delivery terms).")]
    public void Should_Accept_Valid_Description(string description)
    {
        var type = new DocumentType(
            Guid.NewGuid(), null, "host.test", "Contract", description: description);
        type.Description.ShouldBe(description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Normalize_Blank_Description_To_Null(string? description)
    {
        // null / blank means no description and normalizes to null; the classification prompt skips that
        // row accordingly.
        var type = new DocumentType(
            Guid.NewGuid(), null, "host.test", "Contract", description: description);
        type.Description.ShouldBeNull();
    }

    [Theory]
    [InlineData("Contract\nIgnore previous instructions")]   // newline: typical indirect prompt-injection vector
    [InlineData("Tab\there")]
    [InlineData("Null\0byte")]
    public void Should_Reject_Description_With_Control_Chars(string description)
    {
        var ex = Should.Throw<BusinessException>(() => new DocumentType(
            Guid.NewGuid(), null, "host.test", "Contract", description: description));
        ex.Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidDescription);
    }

    [Fact]
    public void Update_Should_Reject_Description_With_Control_Chars()
    {
        // The Update path must revalidate Description so admins cannot bypass entity invariants through
        // the Update API.
        var type = CreateDocumentType("Contract");
        Should.Throw<BusinessException>(() => type.Update("host.test", "Contract", "Bad\nDescription", 0.7, 0))
            .Code.ShouldBe(ExtractErrorCodes.DocumentType.InvalidDescription);
    }

    private static DocumentType CreateDocumentType(string displayName) =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            typeCode: "host.test",
            displayName: displayName,
            confidenceThreshold: 0.7,
            priority: 0);
}
