using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// FieldDraftSuggestionAppService 的解析 / sanitize / 护栏 / fail-open 语义（issue #264）。
/// IChatClient 用 NSubstitute 替代，无真实 LLM 调用——权限断言（CheckDraftPermissionAsync）在测试子类里放行
/// （单元构造直接 new，无 HTTP 鉴权上下文），只验证服务自有的输出处理逻辑。
/// </summary>
public class FieldDraftSuggestionAppService_Tests
{
    // 测试子类：放行权限断言（生产路径由类级 [Authorize] + CheckDraftPermissionAsync 的 Create||Update 把关）。
    // [DisableConventionalRegistration]：阻止 ABP 把这个派生 ApplicationService 自动注册进 DI——否则 CI 跑全量
    // 测试时容器构建会对这个 private 测试双类启用 Castle DynamicProxy 类拦截器而失败（不可访问），拖垮整个测试宿主。
    [DisableConventionalRegistration]
    private sealed class TestableFieldDraftSuggestionAppService : FieldDraftSuggestionAppService
    {
        public TestableFieldDraftSuggestionAppService(IChatClient chatClient, ILogger<FieldDraftSuggestionAppService> logger)
            : base(chatClient, logger)
        {
        }

        protected override Task CheckDraftPermissionAsync() => Task.CompletedTask;
    }

    private static FieldDraftSuggestionAppService CreateService(IChatClient chatClient)
        => new TestableFieldDraftSuggestionAppService(chatClient, NullLogger<FieldDraftSuggestionAppService>.Instance);

    private static IChatClient ChatClientReturning(string responseText)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)])));
        return fake;
    }

    private static IChatClient ChatClientThrowing(Exception ex)
    {
        var fake = Substitute.For<IChatClient>();
        fake.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(ex));
        return fake;
    }

    [Fact]
    public async Task Maps_full_draft_for_new_field()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Contract Amount\",\"name\":\"contract_amount\",\"dataType\":\"number\",\"isRequired\":true,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "提取合同总金额", ForNewField = true });

        draft.DisplayName.ShouldBe("Contract Amount");
        draft.Name.ShouldBe("contract_amount");
        draft.DataType.ShouldBe(FieldDataType.Number);
        draft.IsRequired.ShouldBeTrue();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Sanitizes_name_from_llm_output()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"甲方名称\",\"name\":\"Party Name!\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "甲方名称", ForNewField = true });

        // 不信任 LLM 输出：小写、非 [a-z0-9] 折叠为下划线、合并、去首尾。
        draft.Name.ShouldBe("party_name");
    }

    [Fact]
    public async Task Never_suggests_name_when_editing_existing_field()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Amount\",\"name\":\"contract_amount\",\"dataType\":\"number\",\"isRequired\":false,\"allowMultiple\":false}"));

        // 护栏 1：编辑既有字段 —— Name 是契约级冻结身份键，恒回吐空，即使 LLM 给了建议。
        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "金额", ForNewField = false });

        draft.Name.ShouldBe(string.Empty);
        draft.DisplayName.ShouldBe("Amount");
        draft.DataType.ShouldBe(FieldDataType.Number);
    }

    [Fact]
    public async Task Forces_allow_multiple_false_for_non_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Dates\",\"name\":\"dates\",\"dataType\":\"date\",\"isRequired\":false,\"allowMultiple\":true}"));

        // 护栏 2：镜像 FieldDefinition.ValidateMultiValue —— 多值仅 Text 有效，非文本钳为 false。
        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "所有日期", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Date);
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Keeps_allow_multiple_true_for_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Tags\",\"name\":\"tags\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":true}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "标签列表", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Text);
        draft.AllowMultiple.ShouldBeTrue();
    }

    [Fact]
    public async Task Tolerates_string_boolean_from_weak_provider()
    {
        // #264 review #4：弱结构化 provider 可能回字符串布尔，不应静默降级为 false。
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Amount\",\"name\":\"amount\",\"dataType\":\"number\",\"isRequired\":\"true\",\"allowMultiple\":\"false\"}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "金额", ForNewField = true });

        draft.IsRequired.ShouldBeTrue();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Tolerates_numeric_boolean_from_weak_provider()
    {
        // #264 review #4：数字 1/0 形态的布尔同样识别（allowMultiple 仅 Text 有效，此处 text + 1 → true）。
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Tags\",\"name\":\"tags\",\"dataType\":\"text\",\"isRequired\":0,\"allowMultiple\":1}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "标签", ForNewField = true });

        draft.IsRequired.ShouldBeFalse();
        draft.AllowMultiple.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_data_type_falls_back_to_text()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"X\",\"name\":\"x\",\"dataType\":\"currency\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DataType.ShouldBe(FieldDataType.Text);
    }

    [Fact]
    public async Task Strips_control_chars_from_display_name()
    {
        var svc = CreateService(ChatClientReturning(
            "{\"displayName\":\"Line\\nBreak\\tField\",\"name\":\"x\",\"dataType\":\"text\",\"isRequired\":false,\"allowMultiple\":false}"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        // 控制字符折叠为单空格，与实体层 ValidateDisplayName 拒绝域一致（防起草值一保存即 loud fail）。
        draft.DisplayName.ShouldBe("Line Break Field");
    }

    [Fact]
    public async Task Returns_empty_draft_when_output_is_not_json()
    {
        var svc = CreateService(ChatClientReturning("sorry, I cannot help with that"));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DisplayName.ShouldBe(string.Empty);
        draft.Name.ShouldBe(string.Empty);
        draft.DataType.ShouldBe(FieldDataType.Text);
        draft.IsRequired.ShouldBeFalse();
        draft.AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Returns_empty_draft_when_llm_throws()
    {
        var svc = CreateService(ChatClientThrowing(new InvalidOperationException("LLM down")));

        var draft = await svc.DraftAsync(new DraftFieldDefinitionInput { Prompt = "x", ForNewField = true });

        draft.DisplayName.ShouldBe(string.Empty);
        draft.Name.ShouldBe(string.Empty);
    }
}
