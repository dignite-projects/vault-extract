using System;
using Dignite.Paperbase.Chat;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Timing;
using Xunit;

namespace Dignite.Paperbase.Chat;

public class ChatConversationTests
{
    private static IClock CreateClock(DateTime? at = null)
    {
        var clock = Substitute.For<IClock>();
        clock.Now.Returns(at ?? new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));
        return clock;
    }

    private static ChatConversation CreateConversation(Guid? documentId = null)
        => new(
            Guid.NewGuid(),
            tenantId: null,
            title: "Test Conversation",
            documentId: documentId);

    // ────────────────────────────────────────────────────────────────────────────
    // 1. DocumentId is now an optional anchor only — Issue #100 dropped the old
    //    DocumentTypeCode / TopK / MinScore fields, so there is no scope conflict
    //    to test anymore. The "model is locked to a single document" path is gone;
    //    DocumentSearchAdapter no longer enforces it.
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Accept_DocumentId_Only()
    {
        var conv = CreateConversation(documentId: Guid.NewGuid());
        conv.DocumentId.ShouldNotBeNull();
    }

    [Fact]
    public void Constructor_Should_Accept_No_DocumentId()
    {
        var conv = CreateConversation();
        conv.DocumentId.ShouldBeNull();
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 2. Title 长度超限
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_Should_Throw_When_Title_Exceeds_MaxLength()
    {
        Should.Throw<Exception>(() =>
        {
            _ = new ChatConversation(
                Guid.NewGuid(),
                tenantId: null,
                title: new string('x', ChatConsts.MaxTitleLength + 1),
                documentId: null);
        });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 3. TenantId 不可变（无 public setter，无 Update 方法）
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TenantId_Should_Be_Immutable_After_Construction()
    {
        var tenantId = Guid.NewGuid();
        var conv = new ChatConversation(
            Guid.NewGuid(),
            tenantId: tenantId,
            title: "My Conversation",
            documentId: null);

        conv.TenantId.ShouldBe(tenantId);

        var clock = CreateClock();
        conv.Rename("New Title");
        conv.AppendUserMessage(clock, Guid.NewGuid(), "Hello", Guid.NewGuid());

        conv.TenantId.ShouldBe(tenantId);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 4. ConcurrencyStamp 由 ABP AbpDbContext 在保存时自动轮换；domain 不再手动轮换。
    // ────────────────────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────────────────────
    // 5. AppendUserMessage 重复 ClientTurnId → BusinessException
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendUserMessage_Should_Throw_On_Duplicate_ClientTurnId()
    {
        var conv = CreateConversation();
        var clock = CreateClock();
        var clientTurnId = Guid.NewGuid();

        conv.AppendUserMessage(clock, Guid.NewGuid(), "First message", clientTurnId);

        var ex = Should.Throw<BusinessException>(() =>
        {
            conv.AppendUserMessage(clock, Guid.NewGuid(), "Duplicate", clientTurnId);
        });

        ex.Code.ShouldBe(PaperbaseErrorCodes.DuplicateClientTurnId);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 6. AppendAssistantMessage 不需要 ClientTurnId（null），多条不冲突
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendAssistantMessage_Should_Not_Require_ClientTurnId()
    {
        var conv = CreateConversation();
        var clock = CreateClock();

        var msg1 = conv.AppendAssistantMessage(clock, Guid.NewGuid(), "Answer 1", citationsJson: null, isDegraded: false);
        var msg2 = conv.AppendAssistantMessage(clock, Guid.NewGuid(), "Answer 2", citationsJson: "[]", isDegraded: true);

        msg1.ClientTurnId.ShouldBeNull();
        msg2.ClientTurnId.ShouldBeNull();
        msg1.IsDegraded.ShouldBeFalse();
        msg2.IsDegraded.ShouldBeTrue();
        conv.Messages.Count.ShouldBe(2);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 7. Rename 仅修改标题；ConcurrencyStamp 的轮换由 ABP 在 SaveChanges 阶段自动完成
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_Should_Update_Title()
    {
        var conv = CreateConversation();
        conv.Rename("New Title");
        conv.Title.ShouldBe("New Title");
    }
}
