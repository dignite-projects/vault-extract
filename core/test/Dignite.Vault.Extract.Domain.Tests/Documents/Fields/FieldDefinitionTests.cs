using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// FieldDefinition entity invariant tests. Focus:
/// <list type="bullet">
///   <item>Name allowlist validation: FieldDefinition.Name enters the JSON schema description in the LLM
///   prompt (FieldExtractionWorkflow), so newline / quote / control-character prompt-injection carriers
///   must be blocked.</item>
///   <item>DisplayName control-character filtering: DisplayName does not enter the prompt, unlike
///   DocumentType.DisplayName, but UI rendering and log output still should not carry \n \t \0 control
///   characters as defense-in-depth hygiene.</item>
/// </list>
/// </summary>
public class FieldDefinitionTests
{
    [Theory]
    [InlineData("amount")]
    [InlineData("Contract_Number")]
    [InlineData("party-name")]
    [InlineData("a")]
    [InlineData("A1_b-2")]
    public void Should_Accept_Valid_Name(string name)
    {
        var def = CreateDefinition(name);
        def.Name.ShouldBe(name);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("name\"quoted")]
    [InlineData("with\nnewline")]
    [InlineData("中文")]
    [InlineData("name.dot")]
    [InlineData("name/slash")]
    [InlineData("name;sql")]
    public void Should_Reject_Name_With_Invalid_Chars(string name)
    {
        var ex = Should.Throw<BusinessException>(() => CreateDefinition(name));
        ex.Code.ShouldBe(ExtractErrorCodes.FieldDefinition.InvalidName);
    }

    [Fact]
    public void Should_Reject_Name_Exceeding_Max_Length()
    {
        var tooLong = new string('a', FieldDefinitionConsts.MaxNameLength + 1);
        // Overlength values go through Check.NotNullOrWhiteSpace maxLength validation and throw
        // ArgumentException rather than BusinessException.
        Should.Throw<ArgumentException>(() => CreateDefinition(tooLong));
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("合同金额")]              // Chinese OK
    [InlineData("契約金額")]              // Japanese OK
    [InlineData("Contract Amount")]       // space OK
    [InlineData("Party A / Party B")]     // slash / space combination OK
    [InlineData("Amount (CNY)")]          // parentheses OK
    public void Should_Accept_Valid_DisplayName(string displayName)
    {
        var def = CreateDefinition("amount", displayName);
        def.DisplayName.ShouldBe(displayName);
    }

    [Theory]
    [InlineData("Amount\nIgnore")]        // \n
    [InlineData("Amount\r\nIgnore")]      // \r\n
    [InlineData("Tab\there")]             // tab
    [InlineData("Null\0byte")]            // \0
    [InlineData("Vertical\vTab")]
    [InlineData("Form\fFeed")]
    public void Should_Reject_DisplayName_With_Control_Chars(string displayName)
    {
        var ex = Should.Throw<BusinessException>(() => CreateDefinition("amount", displayName));
        ex.Code.ShouldBe(ExtractErrorCodes.FieldDefinition.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Also_Reject_DisplayName_Control_Chars()
    {
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("amount", "Bad\nName", "Extract", FieldDataType.Text, 0, false, false, false))
            .Code.ShouldBe(ExtractErrorCodes.FieldDefinition.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Allow_Name_Rename()
    {
        // #207: unlock Name rename. Internal association now uses immutable FieldDefinitionId, so rename
        // is no longer forbidden at the entity layer.
        var def = CreateDefinition("amt", "Amount");
        def.Name.ShouldBe("amt");

        def.Update("total_amount", "Amount", "Extract", FieldDataType.Text, 0, false, false, false);

        def.Name.ShouldBe("total_amount");
    }

    [Fact]
    public void Update_Should_Still_Validate_Name_Format()
    {
        // Unlocking rename does not skip the regex allowlist; invalid Name is still rejected.
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("bad name", "Amount", "Extract", FieldDataType.Text, 0, false, false, false))
            .Code.ShouldBe(ExtractErrorCodes.FieldDefinition.InvalidName);
    }

    // ─── NormalizeDisplayName contract (#264: draft assistant prefill must pass ValidateDisplayName) ─────────

    [Theory]
    [InlineData("Amount\nIgnore")]            // \n to space
    [InlineData("Amount\r\nIgnore")]          // \r\n to collapsed space
    [InlineData("Tab\there")]                 // tab
    [InlineData("Null\0byte")]                // \0
    [InlineData("Vertical\vTab")]
    [InlineData("Form\fFeed")]
    [InlineData("  双侧空白  ")]
    [InlineData("多   连续   空白")]
    public void NormalizeDisplayName_Output_Should_Pass_ValidateDisplayName(string raw)
    {
        // Contract lock: Normalize output must pass ValidateDisplayName through the constructor. This
        // prevents future tightening of the rejection domain from silently drifting between the two paths
        // and making drafted values fail loudly when saved (#264 review2 #3).
        var normalized = FieldDefinition.NormalizeDisplayName(raw);

        var def = CreateDefinition("amount", normalized);

        def.DisplayName.ShouldBe(normalized);
        normalized.ShouldNotContain('\n');
        normalized.ShouldNotContain('\t');
        normalized.ShouldNotContain('\0');
    }

    [Fact]
    public void NormalizeDisplayName_Should_Truncate_Without_Leaving_Lone_Surrogate()
    {
        // The MaxDisplayNameLength-th code unit is exactly the high surrogate of an astral character
        // (emoji); truncation must not leave an orphan high surrogate.
        var raw = new string('a', FieldDefinitionConsts.MaxDisplayNameLength - 1) + "😀"; // 😀 = U+D83D U+DE00

        var normalized = FieldDefinition.NormalizeDisplayName(raw);

        normalized.Length.ShouldBeLessThanOrEqualTo(FieldDefinitionConsts.MaxDisplayNameLength);
        // Last char is not an orphan high surrogate; either keep 😀 intact or drop it entirely.
        if (normalized.Length > 0)
        {
            char.IsHighSurrogate(normalized[^1]).ShouldBeFalse();
        }
        // Still safe to construct: no throw and serializable.
        var def = CreateDefinition("amount", normalized);
        def.DisplayName.ShouldBe(normalized);
    }

    [Fact]
    public void NormalizeDisplayName_Should_Return_Empty_For_Blank()
    {
        FieldDefinition.NormalizeDisplayName(null).ShouldBe(string.Empty);
        FieldDefinition.NormalizeDisplayName("   ").ShouldBe(string.Empty);
    }

    // ─── AllowMultiple invariant (#212: only text fields can be multi-value) ───────────────────────

    [Fact]
    public void Should_Accept_AllowMultiple_On_String_Field()
    {
        var def = new FieldDefinition(
            Guid.NewGuid(), null, Guid.NewGuid(), "tags", "Tags", "Extract tags.",
            FieldDataType.Text, allowMultiple: true);

        def.AllowMultiple.ShouldBeTrue();
    }

    [Theory]
    [InlineData(FieldDataType.Number)]
    [InlineData(FieldDataType.Boolean)]
    [InlineData(FieldDataType.Date)]
    [InlineData(FieldDataType.DateTime)]
    public void Should_Reject_AllowMultiple_On_Non_String_Field(FieldDataType dataType)
    {
        var ex = Should.Throw<BusinessException>(() => new FieldDefinition(
            Guid.NewGuid(), null, Guid.NewGuid(), "f", "F", "Extract.",
            dataType, allowMultiple: true));

        ex.Code.ShouldBe(ExtractErrorCodes.FieldDefinition.MultiValueRequiresStringType);
    }

    [Fact]
    public void Update_Should_Reject_AllowMultiple_On_Non_String_Field()
    {
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("amount", "Amount", "Extract", FieldDataType.Number, 0, false, allowMultiple: true, isUniqueKey: false))
            .Code.ShouldBe(ExtractErrorCodes.FieldDefinition.MultiValueRequiresStringType);
    }

    private static FieldDefinition CreateDefinition(string name, string displayName = "Amount") =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            documentTypeId: Guid.NewGuid(),
            name: name,
            displayName: displayName,
            prompt: "Extract the value.",
            dataType: FieldDataType.Text);
}
