# LLM call anti-patterns

本文件由 `maf-workflow-reviewer` agent 在审查 PR 时引用，用于快速定位 Paperbase 内部 **LLM 调用点**的典型错误（反例 A / B 是安全问题，反例 C 是 MCP schema 正确性问题）。规则适用于所有 LLM 入口：

- 当前已落地：`DocumentClassificationWorkflow` / `FieldExtractionWorkflow` + `FieldExtractionEventHandler`（字段架构 v2 统一 Host + 租户 (B 机制)）/ `DocumentTextExtractionBackgroundJob.TryGenerateTitleAsync`
- 未来扩展：MCP server tool（[#170](https://github.com/dignite-projects/dignite-paperbase/issues/170)）、Webhook 触发的 LLM 路径、任何由 LLM 输出影响参数的查询路径

所有示例均为**伪代码**，不可编译，仅用于说明意图。

---

## 反例 A：字段抽取 Agent 挂 AIContextProviders

**规则来源**：`maf-workflow-reviewer.md § 2.10`

**适用范围**：所有结构化字段抽取路径——分类、Host 字段抽取、租户字段抽取（B 机制），以及未来新增的字段抽取 workflow / agent。

### ❌ 错误写法

```csharp
// 错误：给字段抽取 agent 挂 TextSearchProvider（RAG 检索）
var provider = new TextSearchProvider(
    async (query, ct) => /* fetch chunks from your vector store */,
    options: /* TextSearchProviderOptions */);
var options = new ChatClientAgentOptions
{
    AIContextProviders = [provider],          // ← 禁止
    ChatHistoryProvider = new InMemory...()   // ← 禁止
};
var agent = new ChatClientAgent(_chatClient, options);
var run = await agent.RunAsync<HostFieldExtractionResult>(markdown);
```

**危害**：

- RAG 检索会把与当前文档无关的其他文档 chunk 注入到 prompt 中，导致"合同金额"、"甲方名称"等结构化字段**从错误文档**提取，写入下游业务聚合根 / Paperbase 类型绑定字段表
- Paperbase 是**通道层**，字段抽取的输入应当**仅是当前文档的 Markdown**——挂 `AIContextProviders` 把通道哲学破坏成 RAG，违反 CLAUDE.md "OUT of scope"

### ✅ 正确写法

```csharp
// 正确：仅 IChatClient + system instructions，RunAsync<T> 结构化输出
var agent = new ChatClientAgent(
    _chatClient,
    instructions: HostFieldExtractionInstructions.SystemPrompt);
var run = await agent.RunAsync<HostFieldExtractionResult>(markdown);
return run.Result ?? new HostFieldExtractionResult();
```

或者像当前 `FieldExtractionWorkflow` 那样直接调 `IChatClient.GetResponseAsync`，绕过 agent 封装：

```csharp
// 正确：直接构造 ChatMessage 列表，无 AIContextProvider 干扰
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
    new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
};
var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
```

**参照实现**：
- `core/src/Dignite.Paperbase.Application/Documents/Pipelines/Classification/DocumentClassificationWorkflow.cs`
- `core/src/Dignite.Paperbase.Application/Documents/Pipelines/FieldExtraction/FieldExtractionWorkflow.cs`

---

## 反例 B：LLM 触发的查询路径未做 fail-closed 安全门

**规则来源**：`maf-workflow-reviewer.md § 2.9`

**背景**：当前 Paperbase Core 的 LLM 路径都是**单纯的文本输入 → 结构化输出**（分类、字段抽取、标题生成），不涉及由 LLM 输出参数化的 DB 查询。但即将到来的 #170 MCP server 会让 LLM 通过 tool 接口触发文档检索 / 元数据查询。**任何 LLM 触发或参数受 LLM 输出影响的查询路径**都必须做 fail-closed 安全门——HTTP 边界上的 `[Authorize]` 不覆盖这种反射 / tool-dispatch 路径；script / tool 方法体内的安全断言是唯一防线。

**适用范围**：

- 未来的 MCP server tool 方法（#170）
- 未来的 Webhook handler 中触发 LLM 工具调用的路径
- 任何让 LLM 拿到查询参数并落到 `IRepository.GetQueryableAsync()` / `IAsyncQueryableExecuter.ToListAsync()` 的代码点

### ❌ 错误写法 1：依赖 AppService 上的 `[Authorize]` / 不做权限断言

```csharp
[McpTool("search-documents")]
public async Task<string> SearchAsync(string documentTypeCode, string fieldName, string fieldValue, IServiceProvider sp, CancellationToken ct)
{
    // 错误：直接把 AppService 方法的结果序列化返回
    // IDocumentAppService 的 [Authorize(Documents.Default)] 在 LLM 反射调用时不生效
    var appService = sp.GetRequiredService<IDocumentAppService>();
    var result = await appService.GetListAsync(new GetDocumentListInput { DocumentTypeCode = documentTypeCode });
    return JsonSerializer.Serialize(result);
}
```

**危害**：仅持有 MCP 端点访问凭据的客户端通过自然语言（"帮我查张三的合同"）即可拿到本无权访问的文档。LLM 是无意识的"权限提升通道"。

### ❌ 错误写法 2：在 LLM 触发路径上禁用租户过滤器 / 把端点放在多租户中间件之外

```csharp
public async Task<string> SearchAsync(string keyword)
{
    await _authService.CheckAsync(DocumentPermissions.Default);
    // 错误：在 LLM 可达路径上禁用 IMultiTenant 全局过滤器
    using (DataFilter.Disable<IMultiTenant>())
    {
        var q = await _repo.GetQueryableAsync();   // ← 此时不再按租户过滤，跨租户泄漏
        return JsonSerializer.Serialize(await _executer.ToListAsync(q.Where(...).Take(20)));
    }
}
```

**澄清**：ABP 的 `IMultiTenant` 全局查询过滤器**就是**框架级租户边界——它由已认证主体（token 的 tenant 声明，经 `CurrentUserTenantResolveContributor` 解析，优先级最高、不可被 `__tenant` header 伪造）驱动，对所有查询默认生效，`FromSqlRaw` 经 EF Core 包成子查询后同样受约束。所以**正常路径依赖它即可，不需要手写 `TenantId` 谓词**——手写只是冗余，且在调用方刻意禁用过滤器时会静默把结果钳到 ambient 租户、无视其意图，还永久剥夺合法跨租户检索能力。

**真正的反模式**是反过来击穿这条边界：在 LLM 触发路径上 `DataFilter.Disable<IMultiTenant>()` / `IgnoreQueryFilters()`（如上），或把 MCP / Webhook 端点映射在 `UseMultiTenancy()` 之外（租户解析不到 → 全部当 host 数据跑）。

### ❌ 错误写法 3：结果集无上限

```csharp
var matches = await _executer.ToListAsync(q.Where(d => d.Title.Contains(keyword)));
return JsonSerializer.Serialize(matches);   // ← 命中 5000 条全返回
```

**危害**：

- 单次 tool 调用炸 LLM context window，后续 turn 退化
- 攻击者通过宽泛 keyword（如 `""`）制造内存压力或费用攻击

### ❌ 错误写法 4：把用户输入拼进 tool description / instructions

```csharp
public override McpToolDescriptor Descriptor { get; } = new(
    "search-documents",
    $"Search documents belonging to user {someUserName}. ...");   // ← 禁止：构造器参数运行时拼

// 或：
protected override string Instructions => $"You serve user {dynamicValue}. ...";
```

**危害**：description / instructions 文本是 LLM 决策上下文的一部分。如果包含用户控制的字符串（昵称、签名、文档名），可被用作 prompt injection 注入向量。Descriptor 文本和 instructions 都必须是**编译期常量**或纯静态字符串字面量。

### ❌ 错误写法 5：裸跑 raw SQL

```csharp
public async Task<string> ReportAsync(string whereClause)
{
    var sql = $"SELECT * FROM Documents WHERE {whereClause}";   // ← LLM 拼 SQL
    return await _dbContext.Database.SqlQueryRaw<...>(sql).ToListAsync();
}
```

**危害**：SQL 注入面 + 绕过 ABP 权限 / 审计 / 软删除 / 租户过滤层。即便是 LLM 生成 SQL 看似可控，也在攻击面内（prompt injection 完全可以诱导 LLM 写 `WHERE 1=1` 或 `; DROP TABLE`）。

### ✅ 正确实现要点

每个 LLM 触发的查询点（无论 MCP tool、Webhook handler、还是其他类似入口）依次满足以下条件：

```csharp
[McpTool("search-documents")]
[Description("Search Paperbase documents by structured criteria.")]   // ← 编译期常量
private static async Task<string> SearchAsync(
    string? keyword,
    [Description("ISO 4217 currency code (optional).")] string? currency,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken = default)
{
    // 1. 显式权限断言 — fail closed
    var authSvc = serviceProvider.GetRequiredService<IAuthorizationService>();
    await authSvc.CheckAsync(PaperbasePermissions.Documents.Default);

    // 2. 租户隔离 — 交给 ABP 的 IMultiTenant 全局过滤器自动施加，不手写 TenantId 谓词，
    //    更不要在此 Disable<IMultiTenant>() / IgnoreQueryFilters()
    var repo = serviceProvider.GetRequiredService<IDocumentRepository>();
    var q = await repo.GetQueryableAsync();   // ← 自动按已解析租户过滤

    // 3. 业务过滤 + 强制 Take(N)
    var executer = serviceProvider.GetRequiredService<IAsyncQueryableExecuter>();
    var rows = await executer.ToListAsync(
        q.Where(d => keyword == null || d.Title.Contains(keyword))
         .OrderByDescending(d => d.CreationTime)
         .Take(MaxResultRows),
        cancellationToken);

    // 4. 用户派生自由文本字段必须 PromptBoundary.WrapField(...) 包裹
    return JsonSerializer.Serialize(new
    {
        rows = rows.Select(r => new
        {
            r.Id,
            r.CreationTime,
            title = PromptBoundary.WrapField(r.Title),            // ← 包裹
            documentTypeCode = r.DocumentTypeCode                 // 系统字段，不需要 wrap
        })
    });
}
```

**关键要点回顾**：

1. **`[Authorize]` 不够**——必须在方法体显式 `CheckAsync(Permission)`
2. **租户隔离交给框架过滤器**——ABP `IMultiTenant` 全局过滤器即租户边界（由已认证主体的 tenant 声明驱动，含 `FromSqlRaw` 路径）；不手写 `TenantId` 谓词，只需保证不在 LLM 路径上 `Disable<IMultiTenant>()` / `IgnoreQueryFilters()`
3. **必须 `Take(N)`**——单次 tool 调用结果集硬上限，防止 prompt-injection 诱导宽泛查询
4. **description / instructions 必须是编译期常量**——禁止拼用户字符串
5. **不允许 raw SQL**——LLM 拼 SQL 在攻击面内
6. **PromptBoundary**——返回值里的用户派生自由文本字段（title、partyName、summary 等）必须经 `PromptBoundary.WrapField(...)` 包裹

---

## 同源安全约定（适用于所有 LLM 路径）

上述两个反例都是 CLAUDE.md "## 安全约定" 一节 4 条 bullet 的具体化：

- **Fail-closed 安全断言**——权限 + 租户 + 上限 + 不裸 SQL
- **PromptBoundary**——用户派生自由文本进 prompt / LLM-facing 输出前必须包裹
- **Description / Instructions 编译期常量**——禁止运行时拼用户字符串
- **多租户隔离**——依赖 ABP `IMultiTenant` 全局过滤器（框架级边界，含 `FromSqlRaw`）；不手写谓词，唯一纪律是 LLM 路径不得禁用该过滤器

新增任何 LLM 调用点时，按这 4 条做 self-review；`maf-workflow-reviewer` agent 在 PR 审查时也按此清单核对。

---

## 反例 C：MCP tool / resource 的 LLM-facing 集合参数用了集合接口 / 数组类型

**规则来源**：本仓库实证（`DocumentSearchTool.fieldFilters` 曾静默失效）。**这是 schema 正确性问题，不是安全问题**——但同样属于"LLM 调用点的典型错误"，故并入本文件。

**背景**：ABP 用 Autofac 容器。Autofac 把**所有集合关系类型**（`IEnumerable<T>` / `IReadOnlyList<T>` / `IList<T>` / `ICollection<T>` / `IReadOnlyCollection<T>` / `T[]`）视为可隐式解析的服务——`IServiceProviderIsService.IsService(typeof(IReadOnlyList<Foo>))` 返回 `true`。而 MCP SDK（ModelContextProtocol 1.3.0）的参数绑定规则是：**`IsService(参数类型) == true` 的参数会被 `ExcludeFromSchema`**，从 LLM 看到的 inputSchema 中剔除、改为运行时从 DI 注入。

**后果**：用集合接口 / 数组声明的 LLM-facing 参数**静默消失**——LLM 看不到、永不传值；tool 仍能调用（运行时被注入一个空集合），于是该参数对应的功能**永久失效且不报错**。标量参数（`string` / `int?`，`IsService=false`）不受影响。

### ❌ 错误写法

```csharp
[McpServerTool(Name = "search_documents")]
public static async Task<...> SearchAsync(
    string documentTypeCode,                          // ✅ string → IsService=false → 进 schema
    IReadOnlyList<FieldFilter>? fieldFilters = null,  // ❌ 集合接口 → Autofac IsService=true → 被剔除出 schema
    FieldFilter[]? more = null)                       // ❌ 数组同样被剔除（反直觉：T[] 也是集合关系类型）
```

### ✅ 正确写法

```csharp
[McpServerTool(Name = "search_documents")]
public static async Task<...> SearchAsync(
    string documentTypeCode,
    // 必须用具体 List<T>（IsService=false）；集合接口 / 数组都会被静默剔除。
    List<FieldFilter>? fieldFilters = null)
```

**守护**：用**真正经过 MCP schema 生成**的测试——`McpServerTool.Create(method, target, new McpServerToolCreateOptions { Services = autofacServiceProvider })`，断言 `ProtocolTool.InputSchema` 的 `properties` 含该参数。**直接调用 C# 方法的单元测试抓不到此 bug——它绕过了 schema 生成**。参照 `DocumentSearchTool_Tests.Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters`。

**注意**：MS DI 容器只对 `IEnumerable<T>` 返回 `IsService=true`，对其它集合接口返回 `false`——此坑是 Autofac 特有，复现 / 守护测试必须在 Autofac 容器下跑（测试基类已 `UseAutofac()`）。
