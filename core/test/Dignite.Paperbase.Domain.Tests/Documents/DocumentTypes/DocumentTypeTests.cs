using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents.DocumentTypes;

/// <summary>
/// DocumentType 实体层不变量测试。重点：DisplayName 控制字符过滤——
/// DisplayName 现在由 admin 通过 UI 输入（之前是 LocalizableString 编译期常量），
/// 会被字面拼入 LLM 分类 prompt 的 typeDescriptions 段；必须阻断换行 / 制表符 / 其他
/// 控制字符等 prompt injection 注入向量。允许 Unicode 字母数字 / 空格 / 标点（中日文 OK）。
/// </summary>
public class DocumentTypeTests
{
    [Theory]
    [InlineData("Contract")]
    [InlineData("合同")]
    [InlineData("契約書")]
    [InlineData("Contract / Invoice")]   // 斜杠 / 空格合法
    [InlineData("Type (general)")]       // 括号合法
    public void Should_Accept_Valid_DisplayName(string displayName)
    {
        var type = CreateDocumentType(displayName);
        type.DisplayName.ShouldBe(displayName);
    }

    [Theory]
    [InlineData("Contract\nIgnore previous")]   // \n 换行——典型 prompt injection 注入向量
    [InlineData("Contract\r\nIgnore")]          // \r\n
    [InlineData("Tab\there")]                   // 制表符
    [InlineData("Null\0byte")]                  // \0
    [InlineData("Vertical\vTab")]               // \v
    [InlineData("Form\fFeed")]                  // \f
    public void Should_Reject_DisplayName_With_Control_Chars(string displayName)
    {
        var ex = Should.Throw<BusinessException>(() => CreateDocumentType(displayName));
        ex.Code.ShouldBe(PaperbaseErrorCodes.DocumentType.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Also_Reject_Control_Chars()
    {
        // 构造时合法，但 Update 路径必须重新校验——避免 admin 通过 Update API 绕过实体不变量
        var type = CreateDocumentType("Contract");
        Should.Throw<BusinessException>(() => type.Update("host.test", "Bad\nName", 0.7, 0))
            .Code.ShouldBe(PaperbaseErrorCodes.DocumentType.InvalidDisplayName);
    }

    [Fact]
    public void Update_Should_Allow_TypeCode_Rename()
    {
        // #207：解锁 TypeCode 重命名——内部关联改用不可变 Id，rename 不再被实体层禁止。
        var type = CreateDocumentType("Contract");
        type.TypeCode.ShouldBe("host.test");

        type.Update("host.renamed-contract", "Contract", 0.7, 0);

        type.TypeCode.ShouldBe("host.renamed-contract");
    }

    [Fact]
    public void Update_Should_Still_Validate_TypeCode_Format()
    {
        // rename 解锁不等于跳过 regex 白名单——非法 TypeCode 仍被拒。
        var type = CreateDocumentType("Contract");
        Should.Throw<BusinessException>(() => type.Update("bad code", "Contract", 0.7, 0))
            .Code.ShouldBe(PaperbaseErrorCodes.DocumentType.InvalidCodeFormat);
    }

    [Theory]
    [InlineData("contract")]              // 单段，无 . 也合法（v2 移除强制 . 后）
    [InlineData("host.contract")]         // 两段
    [InlineData("host.case-file")]        // 短横线
    [InlineData("host.case_file")]        // 下划线
    [InlineData("host.legal.contract")]   // 多段
    [InlineData("Host.Contract")]         // 大小写混合
    [InlineData("a")]                     // 最短
    public void Should_Accept_Valid_TypeCode(string typeCode)
    {
        var type = new DocumentType(
            Guid.NewGuid(), null,
            typeCode: typeCode,
            displayName: "Contract");
        type.TypeCode.ShouldBe(typeCode);
    }

    [Theory]
    [InlineData(".host")]                 // 首字符 .
    [InlineData("host.")]                 // 尾字符 .
    [InlineData("host..contract")]        // 连续 .
    [InlineData("host contract")]         // 空格
    [InlineData("host\ncontract")]        // 控制字符
    [InlineData("合同")]                  // Unicode
    [InlineData("host\"contract")]        // 引号
    [InlineData("host;sql")]              // 分号
    public void Should_Reject_TypeCode_With_Invalid_Chars(string typeCode)
    {
        var ex = Should.Throw<BusinessException>(() => new DocumentType(
            Guid.NewGuid(), null,
            typeCode: typeCode,
            displayName: "Contract"));
        ex.Code.ShouldBe(PaperbaseErrorCodes.DocumentType.InvalidCodeFormat);
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
