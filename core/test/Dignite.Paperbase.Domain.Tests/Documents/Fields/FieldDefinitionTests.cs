using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// FieldDefinition 实体层不变量测试。重点：
/// <list type="bullet">
///   <item>Name 白名单校验——FieldDefinition.Name 会进 LLM prompt 的 JSON schema 描述
///   （FieldExtractionWorkflow），必须阻断换行 / 引号 / 控制字符等 prompt injection 载体</item>
///   <item>DisplayName 控制字符过滤——DisplayName 不进 prompt（与 DocumentType.DisplayName 不同），
///   但 UI 渲染 / 日志输出仍不应承受 \n \t \0 等控制字符，作为深度防御 hygiene</item>
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
        ex.Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidName);
    }

    [Fact]
    public void Should_Reject_Name_Exceeding_Max_Length()
    {
        var tooLong = new string('a', FieldDefinitionConsts.MaxNameLength + 1);
        // 长度超限走 Check.NotNullOrWhiteSpace 的 maxLength 校验，抛 ArgumentException 而非 BusinessException
        Should.Throw<ArgumentException>(() => CreateDefinition(tooLong));
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("合同金额")]              // 中文 OK
    [InlineData("契約金額")]              // 日文 OK
    [InlineData("Contract Amount")]       // 空格 OK
    [InlineData("Party A / Party B")]     // 斜杠 / 空格组合 OK
    [InlineData("Amount (CNY)")]          // 括号 OK
    public void Should_Accept_Valid_DisplayName(string displayName)
    {
        var def = CreateDefinition("amount", displayName);
        def.DisplayName.ShouldBe(displayName);
    }

    [Theory]
    [InlineData("Amount\nIgnore")]        // \n
    [InlineData("Amount\r\nIgnore")]      // \r\n
    [InlineData("Tab\there")]             // 制表符
    [InlineData("Null\0byte")]            // \0
    [InlineData("Vertical\vTab")]
    [InlineData("Form\fFeed")]
    public void Should_Reject_DisplayName_With_Control_Chars(string displayName)
    {
        var ex = Should.Throw<BusinessException>(() => CreateDefinition("amount", displayName));
        ex.Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Also_Reject_DisplayName_Control_Chars()
    {
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("amount", "Bad\nName", "Extract", FieldDataType.Text, 0, false, false))
            .Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Allow_Name_Rename()
    {
        // #207：解锁 Name 重命名——内部关联改用不可变 FieldDefinitionId，rename 不再被实体层禁止。
        var def = CreateDefinition("amt", "Amount");
        def.Name.ShouldBe("amt");

        def.Update("total_amount", "Amount", "Extract", FieldDataType.Text, 0, false, false);

        def.Name.ShouldBe("total_amount");
    }

    [Fact]
    public void Update_Should_Still_Validate_Name_Format()
    {
        // rename 解锁不等于跳过 regex 白名单——非法 Name 仍被拒。
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("bad name", "Amount", "Extract", FieldDataType.Text, 0, false, false))
            .Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidName);
    }

    // ─── NormalizeDisplayName 契约（#264：起草助手预填值必能过 ValidateDisplayName） ─────────

    [Theory]
    [InlineData("Amount\nIgnore")]            // \n → 空格
    [InlineData("Amount\r\nIgnore")]          // \r\n → 折叠空格
    [InlineData("Tab\there")]                 // 制表符
    [InlineData("Null\0byte")]                // \0
    [InlineData("Vertical\vTab")]
    [InlineData("Form\fFeed")]
    [InlineData("  双侧空白  ")]
    [InlineData("多   连续   空白")]
    public void NormalizeDisplayName_Output_Should_Pass_ValidateDisplayName(string raw)
    {
        // 契约钉死：Normalize 的产出**必须**能过 ValidateDisplayName（经构造器触发），
        // 防止日后收紧拒绝域时两处静默漂移、起草值一保存就 loud-fail（#264 review2 #3）。
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
        // 第 MaxDisplayNameLength 个码元正好是某 astral 字符（emoji）的高代理项：截断不得残留孤立高代理项。
        var raw = new string('a', FieldDefinitionConsts.MaxDisplayNameLength - 1) + "😀"; // 😀 = U+D83D U+DE00

        var normalized = FieldDefinition.NormalizeDisplayName(raw);

        normalized.Length.ShouldBeLessThanOrEqualTo(FieldDefinitionConsts.MaxDisplayNameLength);
        // 末位不是孤立高代理项；要么完整保留 😀，要么整体丢弃。
        if (normalized.Length > 0)
        {
            char.IsHighSurrogate(normalized[^1]).ShouldBeFalse();
        }
        // 仍可安全构造（不抛、可序列化）。
        var def = CreateDefinition("amount", normalized);
        def.DisplayName.ShouldBe(normalized);
    }

    [Fact]
    public void NormalizeDisplayName_Should_Return_Empty_For_Blank()
    {
        FieldDefinition.NormalizeDisplayName(null).ShouldBe(string.Empty);
        FieldDefinition.NormalizeDisplayName("   ").ShouldBe(string.Empty);
    }

    // ─── AllowMultiple 不变量（#212：仅文本字段可多值） ───────────────────────

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

        ex.Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.MultiValueRequiresStringType);
    }

    [Fact]
    public void Update_Should_Reject_AllowMultiple_On_Non_String_Field()
    {
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("amount", "Amount", "Extract", FieldDataType.Number, 0, false, allowMultiple: true))
            .Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.MultiValueRequiresStringType);
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
