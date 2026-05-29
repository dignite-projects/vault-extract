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
                def.Update("amount", "Bad\nName", "Extract", FieldDataType.String, 0, false))
            .Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Allow_Name_Rename()
    {
        // #207：解锁 Name 重命名——内部关联改用不可变 FieldDefinitionId，rename 不再被实体层禁止。
        var def = CreateDefinition("amt", "Amount");
        def.Name.ShouldBe("amt");

        def.Update("total_amount", "Amount", "Extract", FieldDataType.String, 0, false);

        def.Name.ShouldBe("total_amount");
    }

    [Fact]
    public void Update_Should_Still_Validate_Name_Format()
    {
        // rename 解锁不等于跳过 regex 白名单——非法 Name 仍被拒。
        var def = CreateDefinition("amount", "Amount");
        Should.Throw<BusinessException>(() =>
                def.Update("bad name", "Amount", "Extract", FieldDataType.String, 0, false))
            .Code.ShouldBe(PaperbaseErrorCodes.FieldDefinition.InvalidName);
    }

    private static FieldDefinition CreateDefinition(string name, string displayName = "Amount") =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            documentTypeId: Guid.NewGuid(),
            name: name,
            displayName: displayName,
            prompt: "Extract the value.",
            dataType: FieldDataType.String);
}
