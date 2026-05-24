using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Cabinet 实体层不变量测试。DisplayName 拒绝控制字符——基础卫生（防 UI / CSV 注入），
/// 目的不同于 <see cref="DocumentType"/> 的 prompt injection 边界（Cabinet 正交于 pipeline 不进 LLM）。
/// 允许 Unicode 字母数字 / 空格 / 标点（中日文柜名 OK）。
/// </summary>
public class CabinetTests
{
    [Theory]
    [InlineData("法务部")]
    [InlineData("2024 Project")]
    [InlineData("Legal / HR")]
    public void Should_Accept_Valid_DisplayName(string displayName)
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, displayName);
        cabinet.DisplayName.ShouldBe(displayName);
    }

    [Theory]
    [InlineData("Bad\nName")]
    [InlineData("Tab\there")]
    [InlineData("Null\0byte")]
    public void Should_Reject_DisplayName_With_Control_Chars(string displayName)
    {
        var ex = Should.Throw<BusinessException>(() => new Cabinet(Guid.NewGuid(), null, displayName));
        ex.Code.ShouldBe(PaperbaseErrorCodes.InvalidCabinetDisplayName);
    }

    [Fact]
    public void Update_Should_Also_Reject_Control_Chars()
    {
        // 构造时合法，但 Update 路径必须重新校验——避免 admin 通过 Update API 绕过实体不变量。
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        Should.Throw<BusinessException>(() => cabinet.Update("Bad\nName"))
            .Code.ShouldBe(PaperbaseErrorCodes.InvalidCabinetDisplayName);
    }
}
