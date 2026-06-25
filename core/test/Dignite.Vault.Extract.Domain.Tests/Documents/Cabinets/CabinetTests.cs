using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Cabinet entity invariant tests. Name / Description reject control characters because both enter the
/// LLM through the #265 cabinet-selection prompt wrapped by WrapField. Rejecting control characters is
/// also injection defense in depth, mirroring <see cref="DocumentTypes.DocumentType"/>. Unicode letters,
/// numbers, spaces, and punctuation are allowed, including Chinese and Japanese text.
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
        ex.Code.ShouldBe(ExtractErrorCodes.Cabinet.InvalidName);
    }

    [Fact]
    public void Update_Should_Also_Reject_Control_Chars()
    {
        // Valid at construction time, but the Update path must revalidate so admins cannot bypass entity
        // invariants through the Update API.
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        Should.Throw<BusinessException>(() => cabinet.Update("Bad\nName"))
            .Code.ShouldBe(ExtractErrorCodes.Cabinet.InvalidName);
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
        ex.Code.ShouldBe(ExtractErrorCodes.Cabinet.InvalidDescription);
    }

    [Fact]
    public void Update_Should_Also_Reject_Description_Control_Chars()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "Legal");
        Should.Throw<BusinessException>(() => cabinet.Update("Legal", "Bad\nDescription"))
            .Code.ShouldBe(ExtractErrorCodes.Cabinet.InvalidDescription);
    }
}
