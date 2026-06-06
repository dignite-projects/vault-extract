using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// Cabinet 实体层不变量测试。Name / Description 拒控制字符——二者经 #265 选柜 prompt 进 LLM（WrapField 包裹），
/// 拒控制字符兼作注入深度防御（镜像 <see cref="DocumentTypes.DocumentType"/>）。允许 Unicode 字母数字 / 空格 / 标点（中日文 OK）。
/// </summary>
public class CabinetTests
{
    [Theory]
    [InlineData("法务部")]
    [InlineData("2024 Project")]
    [InlineData("Legal / HR")]
    public void Should_Accept_Valid_Name(string name)
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, name);
        cabinet.Name.ShouldBe(name);
    }

    [Theory]
    [InlineData("Bad\nName")]
    [InlineData("Tab\there")]
    [InlineData("Null\0byte")]
    public void Should_Reject_Name_With_Control_Chars(string name)
    {
        var ex = Should.Throw<BusinessException>(() => new Cabinet(Guid.NewGuid(), null, name));
        ex.Code.ShouldBe(PaperbaseErrorCodes.Cabinet.InvalidName);
    }

    [Fact]
    public void Update_Should_Also_Reject_Control_Chars()
    {
        // 构造时合法，但 Update 路径必须重新校验——避免 admin 通过 Update API 绕过实体不变量。
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        Should.Throw<BusinessException>(() => cabinet.Update("Bad\nName"))
            .Code.ShouldBe(PaperbaseErrorCodes.Cabinet.InvalidName);
    }

    [Theory]
    [InlineData("Contracts signed with external parties.")]
    [InlineData("存放对外签署的销售 / 采购合同")]
    public void Should_Accept_Valid_Description(string description)
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal", description);
        cabinet.Description.ShouldBe(description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Normalize_Blank_Description_To_Null(string? description)
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal", description);
        cabinet.Description.ShouldBeNull();
    }

    [Theory]
    [InlineData("Bad\nDescription")]
    [InlineData("Tab\there")]
    [InlineData("Null\0byte")]
    public void Should_Reject_Description_With_Control_Chars(string description)
    {
        var ex = Should.Throw<BusinessException>(
            () => new Cabinet(Guid.NewGuid(), null, "Legal", description));
        ex.Code.ShouldBe(PaperbaseErrorCodes.Cabinet.InvalidDescription);
    }

    [Fact]
    public void Update_Should_Also_Reject_Description_Control_Chars()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        Should.Throw<BusinessException>(() => cabinet.Update("Legal", "Bad\nDescription"))
            .Code.ShouldBe(PaperbaseErrorCodes.Cabinet.InvalidDescription);
    }
}
