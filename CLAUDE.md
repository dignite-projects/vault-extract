# Dignite Paperbase

> **定位（一句话）**：Paperbase = 物理纸质文档 → 可信数字化数据的**通道层**。
> **不消费、不占有、不深入业务**——出口给下游 RAG 平台 / 业务系统 / AI 客户端等消费方。

这是一个 ABP 框架的项目，遵循 `.claude/rules/` 中的 ABP 核心约定。

## 数据流

```
物理纸张 / 扫描件 / 照片 / PDF 影像 / Office 文件
    ↓
[Paperbase 通道]：OCR + Markdown + 通用元数据 + 可选自定义字段抽取
    ↓ （REST / EventBus / MCP server / Webhook）
    ├─→ 下游 RAG 平台（做 RAG 问答）
    ├─→ 财务系统 / CLM / HR / ERP 等业务系统
    ├─→ Claude Desktop / Cursor / 任意 MCP 客户端
    └─→ 任何消费方（按需自建 consumer）
```

## 项目组织

- **core/** - Paperbase 通道实现（ABP 应用程序栈），遵循 `.claude/rules/abp-core.md`
- **host/** - 宿主应用：配置 OCR / Markdown / LLM provider；唯一可配置中间件的位置
- **docs/** - 面向运维 / 配置 / API 的文档；设计决策走 GitHub Issues，不在 docs/ 下落地
- **modules/** - **不在 Paperbase 范畴**。下游业务消费方（合同管理 / 发票管理等）在自己的仓库实现，通过订阅 EventBus / 调用 MCP server / REST 接入

## 架构设计

Paperbase 采用**两层架构**（业务层不在 Paperbase 范畴）：

### 第一层：Core（通道实现）

`core/` 包含通道的全部能力：

1. **Dignite.Paperbase.Abstractions（扩展契约层）**
   - **位置**：依赖拓扑的最底层（无其他 Paperbase 项目依赖）
   - **职责**：提供 Host 扩展和下游消费方接入所必需的契约
   - **内容**：
     - 多阶段集成事件 ETO：`DocumentUploadedEto` / `OCRCompletedEto` / `DocumentClassifiedEto` / `FieldsExtractedEto` / `DocumentReadyEto`
     - 文本提取契约：`ITextExtractor`、`TextExtractionContext`、`TextExtractionResult`
   - **约束**：不依赖任何其他 Paperbase 项目，仅依赖 ABP 基础模块

2. **Dignite.Paperbase 核心模块栈**（标准 ABP 分层：`Domain.Shared` / `Domain` / `Application` / `EntityFrameworkCore` / `HttpApi` / `Mcp`）
   - **通道核心能力都在 Application 层**：文档存储、OCR pipeline 编排、文档分类（LLM 自动分类至 Host / 租户已定义类型）、系统通用字段抽取、类型绑定字段抽取（B 机制：Host 字段 + 租户字段）、出口事件发布
   - **出口适配器各占独立项目**：`HttpApi`（REST 出口）与 `Mcp`（MCP server 出口，`Dignite.Paperbase.Mcp`）平行——协议/传输关注点隔离在出口项目、不渗入 Application。`Mcp` 把文档暴露为 MCP 资源（`paperbase://documents/{id}`）+ 检索 tool（keyword + 元数据 + ExtractedFields 字段值，**不做向量检索**），依赖 `ModelContextProtocol.AspNetCore`；`MapMcp` 端点仍只在 host 映射。认证复用 host OpenIddict Bearer；订阅 + lifecycle 通知是后续增量（#197）

3. **文本提取能力栈（三层契约 + 多 Provider）**——核心可插拔点
   - **`Dignite.Paperbase.TextExtraction`** —— orchestrator + 默认 `ITextExtractor` 实现（`DefaultTextExtractor`：按文件扩展名 dispatch，图片走 OCR；其他走 Markdown Provider，PDF 无文本层时 fallback OCR）。同项目内声明 `IMarkdownTextProvider` 副契约
   - **`Dignite.Paperbase.Ocr`** —— OCR Provider 实现侧的最小契约层（`IOcrProvider` / `OcrOptions` / `OcrResult`，Markdown-first 强约束）。第三方 OCR 接入只引用此项目，看不到 orchestrator 或 `IMarkdownTextProvider` 副契约
   - **OCR Provider 实现**：`Dignite.Paperbase.Ocr.PaddleOcr`（Host 当前默认，本地 sidecar，PP-StructureV3 走 CPU 即可，输出 Markdown）与 `Dignite.Paperbase.Ocr.AzureDocumentIntelligence`（云方案，高精度）；Host 二选一启用，切换时 `[DependsOn]` + `.csproj ProjectReference` 两处同步
   - **Markdown Provider 实现**：`Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown`（基于 ElBruno.MarkItDotNet，覆盖 PDF/Word/HTML/纯文本/CSV/RTF/EPUB 等数字版文档）
   - **与 OCR Provider 的不对称是故意的**——Markdown Provider 与 orchestrator 耦合度高，契约 + 实现都靠近 TextExtraction；OCR Provider 第三方实现概率更高（云服务 / 本地 sidecar），独立薄契约层给它稳定边界

### 第二层：Modules（下游消费方 / 参考实现，不在 Paperbase 范畴）

**业务模块（合同管理 / 发票管理 / HR 档案管理等）不在 Paperbase 范畴。** 下游消费方在自己的仓库 / 部署中实现，通过 Paperbase 出口契约接入：

| 接入方式 | 适合场景 |
|---------|---------|
| **EventBus 订阅** | 业务系统响应 Paperbase 多阶段事件（如 `DocumentReadyEto`），在自己的聚合根里持久化业务记录 |
| **MCP server 调用** | AI 客户端（Claude Desktop / Cursor / 自建 agent）通过 MCP 协议读取 Paperbase 文档资源、订阅 lifecycle 通知 |
| **REST API** | 通用程序化访问 |
| **Webhook** | 传统系统的事件回调消费方式 |

**典型参考实现模式**（下游业务消费方）：

1. 订阅 `DocumentClassifiedEto` 或 `DocumentReadyEto`
2. 用租户字段抽取（B 机制）拿到结构化业务字段
3. 在自己的聚合根（如下游 `Contract` / `Invoice`）持久化 + 提供业务 API / UI
4. 业务记录是 Paperbase Document 的"派生投影"——`Document` 仍是 truth source

**Paperbase 不亲自做下游的事**——不预置业务 schema（合同金额 / 发票号 / 税额等）、不写业务系统专属连接器、不实现业务工作流（审批 / 续签）。

### 第三层：Host（宿主应用）

`host/` 仅作为容器：

- 在 `[DependsOn(...)]` 中声明依赖的核心模块和 provider 实现
- 在 `ConfigureServices()` 中：
  - 配置 OCR Provider（默认 PaddleOCR，可切 Azure Document Intelligence）+ Markdown Provider（ElBruno MarkItDown）
  - 注册 `IChatClient` + `IEmbeddingGenerator<string, Embedding<float>>`（如 Azure OpenAI / Anthropic / Ollama 等，按 `Microsoft.Extensions.AI` 生态选择）—— 供内置 LLM 分类、类型绑定字段抽取（B 机制）使用
  - 配置出口事件订阅、Webhook 端点、MCP server 端点
- **仅在此处配置中间件**（`OnApplicationInitialization`）
- 不实现任何业务逻辑

> **关键约束**：**LLM / OCR provider 与 API key 配置在 host 部署层，不开放给终端客户**。这是与"客户在管理后台配置 LLM"路径的核心区别。客户是业务用户不是技术用户，让客户填 API key 是产品哲学错误

### 依赖流向

```
Host Application
    ├── 注册 IChatClient + IEmbeddingGenerator
    └── DependsOn:
        ├── Dignite.Paperbase.Application（通道核心 + 编排 + LLM 分类 / 字段抽取）
        ├── Dignite.Paperbase.HttpApi（REST 出口）
        ├── Dignite.Paperbase.Mcp（MCP 出口：文档资源 + 检索 tool；MapMcp 端点在 host 映射）
        ├── Dignite.Paperbase.TextExtraction（orchestrator + IMarkdownTextProvider 契约）
        ├── Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown（Markdown Provider 实现）
        └── Dignite.Paperbase.Ocr.PaddleOcr（OCR Provider，当前默认；可切换 Ocr.AzureDocumentIntelligence）

Dignite.Paperbase.Abstractions（扩展契约层，无其他 Paperbase 项目依赖）
    ├── 多阶段集成事件 ETO（DocumentUploaded / OCRCompleted / DocumentClassified / FieldsExtracted / DocumentReady）
    └── ITextExtractor + TextExtractionContext / TextExtractionResult

Dignite.Paperbase.Ocr（OCR Provider 最小契约层，无其他 Paperbase 项目依赖）
    ├── IOcrProvider + OcrOptions + OcrResult（Markdown-first）
    └── 被 Ocr.PaddleOcr / Ocr.AzureDocumentIntelligence 等 Provider 实现项目引用
```

**核心约束**：

- **单向依赖**：Abstractions 处于最底层，被所有上层引用
- **编排在 Application**：BackgroundJob、Workflow、PipelineRun 生命周期、Document 读写均在 Paperbase.Application
- **业务模块不在 Paperbase**：下游业务消费方通过 EventBus / MCP / REST 解耦消费，**不允许**在 Paperbase Core 内部新增业务模块依赖

## 两层独立单层模型（文档类型体系）

**文档类型（document type）是类型绑定字段的容器**——所有类型绑定字段必须挂在某个文档类型下。

| 层级 | 谁定义 | 作用域 | 例子 / 说明 |
|------|-------|------|------------|
| **Host 部署层** | Host admin | 仅 Host 自身文档使用 | 例：Host 自己运维内部文档需要的"合同 / 发票 / 报告 / 通用文档" |
| **租户层** | 租户 admin（per-tenant）| 仅该租户文档使用 | 例：某律所私有"案件卷宗"类型；某医院私有"会诊记录"类型 |

**核心约束（严格单层、无跨层混合）**：

- **Host 与 tenant 是两个独立宇宙**——Host 类型**不**自动对租户可见，租户类型对其他租户不可见
- **每个 Document 只匹配自己 TenantId 的类型层**——分类候选集 / 字段抽取均按 `Document.TenantId` 精确匹配单层，**不存在跨层 union**
- **跨层同 TypeCode 是合法的两行**——租户可以创建 `"host.contract"`（与 Host 的 `"host.contract"` 是两个独立实体，由 TenantId 区分）。下游消费方必须按 `(TenantId, DocumentTypeCode)` 元组消费，TypeCode 不是全局唯一身份
- **Paperbase 不内置任何文档类型**——所有类型由 Host admin 或租户 admin **通过 `IDocumentTypeAppService` CRUD 自管**；不存在 Module 启动注册路径，不存在 seed contributor

**分类执行机制**：

- 上传时 Paperbase 自动用 LLM 跑分类 prompt → 在该 Document 所属层（按 `Document.TenantId` 精确匹配，后台路径用 `ICurrentTenant.Change` 切层后走通用 `GetListAsync`）内归类
- 置信度低或操作员不同意 → 操作员 UI 可手动修正
- 修正后重新触发后续 pipeline（如对应类型的字段抽取）

## 字段架构

字段分两类组织：**系统通用字段**（Paperbase pipeline 自动计算，顶层 typed columns，与文档类型无关）+ **类型绑定字段**（按 schema 抽取，必须挂在某个类型下）。

### 系统通用字段（Paperbase pipeline 自动产生，顶层 typed columns）

由 Paperbase pipeline 自动产生 + LLM 内置抽取，适用于所有文档，**无需任何 schema 配置**。落到 `Document` 顶层 typed columns（强类型 LINQ + 一等公民索引）：

| 字段 | 来源 | 说明 |
|------|------|------|
| `Title` | 文本提取 pipeline | 由 `MarkdownTitleExtractor` 从 Markdown 提取 |
| `Markdown` | 文本提取 pipeline | Document 唯一文本载荷（Markdown-first） |
| `DocumentTypeCode` | 分类 pipeline | 分类结果 |
| `ClassificationConfidence` / `ClassificationReason` / `ReviewStatus` | 分类 pipeline | 分类元数据 |
| `LifecycleStatus` | 流水线编排 | 宏观生命周期状态 |
| `Language` | OCR / 抽取阶段 | ISO 639-1 / IETF tag |

`FileOrigin`（Owned Entity）含 `BlobName`（BlobStore Key）/ `OriginalFileName` / `FileSize` / `ContentType` / `ContentHash` 等上传时元数据。无独立"Filename / Size / Format"系统字段——读路径直接 `d.FileOrigin.OriginalFileName` 等。

无独立 `PageCount` / `Summary` 字段——前者是 leaky abstraction（很多文档无页概念），未来 page-aware citation 走具名扩展 `PageBlocks` 而非单 int；后者由 `Title` 替代（一个好 Title 即足够 UI 列表展示）。

### 文本提取 provenance + 原生 payload 归档（#210）

文本提取 provider 的**原生输出**（bbox / 表格 cell / text-span 锚点 / 置信度 / 区域类型等 out-of-band 空间信号）是**非可靠事后派生**（provider 版本漂移 → 重跑 bbox 未必对齐当初已存/已切块的 Markdown），唯一拿对齐原料的方式是**提取时与 Markdown 同步捕获**。Paperbase 据此在通道层捕获并归档，严守边界：

- **原生 payload → blob，不进 DB**：`NativePayload`（`Content` + `ContentType` + `SchemaName`）挂 transport `TextExtractionResult`；OCR provider 通过 `OcrResult` 扁平字段（`NativePayloadContent` / `NativePayloadContentType` / `NativePayloadSchemaName`）传递，由 `DefaultTextExtractor` 映射（Ocr 项目不引用 Abstractions）；纯 text→Markdown 的 provider（ElBruno/MarkItDown）无空间模型留 `null`。文本提取 job 把它归档进 `IBlobContainer<PaperbaseDocumentContainer>` 的**稳定 per-document key** `extraction-native/{documentId}`（重提取覆盖，一文档一归档 blob，无孤儿）。**`Document` 文本载荷仍只有 `Markdown`**——生 bbox **绝不**塞回 Markdown 字符串，也**不**关系化进 DB。
- **`Document.ExtractionMetadata`**（`DocumentTextExtractionMetadata?`，Domain typed 值对象 → JSON 列，同 `ExportTemplate.Columns` 走 `AbpJsonValueConverter`）——只存**最小 provenance**：`ProviderName?`（胜出 provider 家族名）+ `NativePayloadManifest?`（`BlobName` / `ContentType` / `SizeBytes` / `Sha256` / `SchemaName`）。**禁止** `Dictionary<string,object>` 袋（#206 原则）。无一等 `ExtractionProviderName` 列、无 path / step 链（零消费，被 review 砍掉）。
- **归档 fail-open**：无 payload / 超限（`DocumentConsts.MaxNativePayloadArchiveBytes`，默认 16 MiB）/ blob 写失败 → 记 warning、manifest 置 null、**文本提取照常成功**——辅助审计 blob 绝不打挂主 Markdown pipeline。
- **出口不透出 provenance**：REST `DocumentDto` 和 MCP 出口**完全不暴露** ExtractionMetadata / BlobName / ProviderName 等内部字段（无下载端点时 payload 摘要不可行动；BlobName 是内部存储 key）。永久删除文档时按 manifest 的 `BlobName` 一并删归档 blob。
- **`Document.Language` 落库**：把 `TextExtractionResult.DetectedLanguage` 经 `Document.SetLanguage(...)` 写入，终结此前 write-never 死字段。
- **本期不做 Layer 3**（规范化 `PageBlocks` / 锚点 / citation）——生 bbox 留 blob 即止。

### 类型绑定字段（B 机制）

类型绑定字段必须挂在某个文档类型下，按谁定义分两层。**两层独立单层——按 `Document.TenantId` 决定本文档跑哪层字段定义，绝不跨层混合**：

| 层级 | 谁定义 | 作用域（仅对……生效） | 例子 |
|------|-------|------------------|------|
| **Host 字段** | Host admin | **Host 文档**（Host 自己上传的文档）| 例：Host 在自管的"合同" type 下加"科室 / 内部合同号"字段 |
| **租户字段** | 租户 admin（per-tenant）| **该租户文档** | 例：律所租户在自管的"案件卷宗" type 下加"当事人 / 案由"字段 |

**关键约束**：

- **Host 与 tenant 是两个独立宇宙**——Host 字段只作用于 Host 文档，租户字段只作用于该租户文档；**不存在"租户字段挂在 Host 类型上"或"Host 字段渗透到租户文档"的关系**
- **业务字段全部自配**——多租户 SaaS 场景下，租户在自己的类型层下挂自己的字段；Host 部署层只服务于 Host 自身的运维场景
- **同名字段跨层允许**（Host 的 `"amount"` 字段与租户 A 的 `"amount"` 字段是两个独立行，作用在各自 TenantId 的文档上）
- **同 TypeCode 跨层允许**——租户可以创建 TypeCode 与 Host 同名的类型，二者是两个独立实体（由 TenantId 区分）

**(B) 机制本质**：Paperbase 提供"按 schema 抽取"的通用引擎——Host 或租户配 schema，引擎按所属层执行抽取。Paperbase Core **不预置任何业务字段定义**（合同金额 / 发票号 / 税额等不写死）。

**实现形态（字段架构 v2）**：

- 单一 `FieldDefinition` 实体承载两层（`TenantId IS NULL` = Host 字段定义；`TenantId != null` = 租户字段定义），唯一索引 `(TenantId, DocumentTypeId, Name)`（#207：内部按不可变 `DocumentTypeId` 关联，`DocumentType.TypeCode` / `FieldDefinition.Name` 可由 admin 重命名而不级联数据行）
- **没有继承关系，也不在 Module 中初始注册**：Host 字段与租户字段都通过 `IFieldDefinitionAppService` CRUD 创建（Host admin 操作 Host 行，租户 admin 操作自己租户行）
- 跨层无 union：admin 视图、LLM 分类候选集、字段抽取，三处都按 `Document.TenantId` 精确匹配一层（admin 视图 / 分类候选集走通用 `GetListAsync`；字段抽取走 `GetForExtractionAsync`）
- 单一 `FieldExtractionEventHandler` 订阅 `DocumentClassifiedEto`，按 `Document.TenantId` 精确匹配一层字段定义 → 一次 LLM 调用 → 经 `Document.SetFields(...)` 整组写入 `DocumentExtractedField` 一等字段值集合（`Document` 聚合的 child entity，一行一个字段值、类型化列；复合键 `(DocumentId, FieldDefinitionId)`，#207）。出口 DTO / MCP / REST 的 `ExtractedFields`（`Dictionary<string, JsonElement>`）由 App / Mapper 层从这些行即时组装，字典 key（字段名）穿透 soft-delete join 当前 `FieldDefinition` 解析（#206 / #207）。字段值查询走普通列 EF Core LINQ（Documents-anchored EXISTS，按 `FieldDefinitionId` 匹配 child），跨任意关系型数据库可移植，不再绑 SQL Server native `json` / `JSON_VALUE`
- 抽取完成统一发布 `FieldsExtractedEto`（薄载荷含 `FieldCount`；下游可按事件载荷 `TenantId` 区分场景）

## 出口契约

Paperbase 通过四种出口给下游消费方：

| 出口 | 协议 | 主要受众 |
|------|------|---------|
| **REST API** | HTTP | 通用程序化访问 |
| **MCP server** | MCP（含 `notifications/resources/updated`）| Claude Desktop / Cursor / 任意 MCP 客户端 |
| **EventBus** | ABP DistributedEventBus | 业务系统 / 自建 consumer |
| **Webhook** | HTTP POST | 传统系统消费方式 |

### 出口事件契约（多阶段 + 薄载荷）

| 阶段事件 | 触发时机 | 受 Ready 闸门约束 |
|---------|---------|----------------|
| `DocumentUploadedEto` | 文档上传完成 | 否 |
| `OCRCompletedEto` | OCR 完成（载荷含 `UsedOcr` 路径标记） | 否 |
| `DocumentClassifiedEto` | 文档分类完成 | 否 |
| `FieldsExtractedEto` | 字段抽取完成（载荷单段 `FieldCount`；按 `Document.TenantId` 决定本文档跑哪层 schema，单桶持久化） | 否 |
| `DocumentReadyEto` | **全流水线完成 + 拿到已确认类型（分类置信度达标或人工确认）** | **是** |

**回收站 / 生命周期事件**（与多阶段管线正交，不受 Ready 闸门约束；下游据此把派生数据归档或物理删除）：

| 生命周期事件 | 触发时机 |
|------------|---------|
| `DocumentDeletedEto` | 文档软删除（进回收站）——下游应把派生数据置为可恢复的归档状态 |
| `DocumentRestoredEto` | 文档从回收站恢复——下游应解除归档 |
| `DocumentPermanentlyDeletedEto` | 文档永久删除（含原文件 / 归档 blob）——下游应物理删除派生数据 |

**载荷设计**：事件载荷一律薄（ID + 关键元数据），下游通过 REST/MCP 回拉详细数据。

**Ready 闸门（分类置信度 + 人工审核）**：

- **设计意图**：只有"类型已确认"的文档才是下游可信信号——分类拿不准的不自动放行
- **闸门执行点**：**仅 `DocumentReadyEto` 受约束**——早期阶段事件正常发，但下游主要消费方默认订阅 `DocumentReadyEto`
- **闸门判据**：文档必须拿到已确认 `DocumentTypeCode`（自动分类置信度 ≥ 该类型 `ConfidenceThreshold`，或操作员人工确认）；`DeriveLifecycle` 在类型为空时不跃迁 Ready
- **不达标的文档**：仍然存（不丢失，不删除）；早期阶段事件正常发布；`DocumentReadyEto` 暂不发；文档进入操作员 UI 的"待人工审核队列"；操作员 Reclassify 指派类型 / Reject 拒绝——通过则触发 `DocumentReadyEto`
- **OCR 置信度门槛 + 信号字段均已移除**（#196）：OCR 平均置信度预测不了真实质量（整页扫歪 / 模糊 / 版式乱未必反映在平均分）。门槛配置（host 默认 + per-tenant 覆盖）已移除；`OcrConfidence` 信号本身（聚合根字段 + `OCRCompletedEto` / `DocumentReadyEto` 透出 + OCR Provider 计算）亦一并移除——既然平均分预测不了质量，透传给下游做次级 gating 同样无效，留着只是死信号。未来若需 OCR 质量运维监控，由 OCR Provider 遥测 / `PipelineRun` 诊断按群体统计诉求重新设计，不在通道出口契约预留；`OCRCompletedEto.UsedOcr`（路径事实，非质量预测）保留

**投递语义：at-least-once + 单调时间戳幂等**

- **可靠性承诺**：Paperbase 通过 **ABP 内置 transactional outbox** 投递事件——业务变更与事件入队在同一 UoW 内原子持久化（写入 `AbpEventOutbox`），后台 worker 扫表真正发布。**事件不丢**（at-least-once）
- **去重 / 替换由下游消费方负责**：通道层**不**维护事件状态表，**不**做"in-flight 替换"，**不**等下游 ack 回执。Paperbase 替下游做幂等会断下游的审计链 + 增加通道复杂度，违反通道哲学
- **下游消费方幂等规则**：每个 ETO 都带 `EventTime: DateTime`（Paperbase publish 时填 `Clock.Now`）。下游消费方按 `(DocumentId, EventType, EventTime)` 自行幂等：
  - 已处理过的 `EventTime <= incoming.EventTime` → 丢弃 incoming（at-least-once 重投）
  - 已处理过的 `EventTime > incoming.EventTime` → 丢弃 incoming（乱序到达的 stale 事件）
  - 否则应用 incoming → 持久化 `EventTime` 作为该 key 的高水位
- **设计权衡**：放弃"in-flight 替换"换取通道层简化。同一文档反复触发（如 OCR retry、操作员 reclassify）下游会收到 N 条事件，按 EventTime 自然丢弃旧版——与下游业务消费方既有的 idempotency 实现路径（如消息队列消费者通用模式）契合，下游 1 行 `WHERE Version >` 即可解决
- **若下游也是 ABP 项目**：可启用 ABP 内置 inbox（`builder.ConfigureEventInbox()`），按 `MessageId` 自动 exactly-once 消费，连 `EventTime` 比较都省了

## OUT of scope（明确不做）

通道定位下，以下能力**不在** Paperbase 范畴。改动若试图触碰这些边界，先停下开 Issue 讨论：

**RAG 应用层**：

- ❌ 向量化（embedding model 选择是下游 RAG 的事）
- ❌ 向量存储（vector DB 是下游 RAG 基础设施）
- ❌ 检索引擎
- ❌ Chat / RAG 问答 / NL search
- ❌ Agent / Workflow 编排（不做 Agent Canvas 类似物）
- ❌ MCP **client**（只做 MCP server，不调外部 MCP 工具）
- ❌ 标准化 chunking（chunking 策略让下游 RAG 决定）

**业务层**：

- ❌ 业务字段抽取的预置 schema（合同金额 / 发票号 / 税额等不预置；客户用 (B) 机制自配）
- ❌ 行业 vertical 导入模板的预置（各类 ERP / 财务 / HR 系统等）——租户可用"租户字段（B 机制）+ 自定义导出模板配置"组合出，但 Paperbase 不沉淀
- ❌ 业务工作流（审批 / 状态机 / 续签）
- ❌ 业务系统专属连接器
- ❌ 业务模块（合同管理 / 发票管理 / HR 档案管理等）——由下游消费方在自己仓库实现

**配置层**：

- ❌ 让终端客户配置 LLM provider / API key（host 部署层配好）

## Markdown-first 数据流（强制）

Paperbase 是通道，Markdown 是出口的**唯一文本载荷**。遇到取舍时优先保持 Markdown-first：

- **OCR / 数字版抽取**：`ITextExtractor` / `IMarkdownTextProvider` / `IOcrProvider` 实现方**必须**输出 Markdown，**不得**退回 plain text 路径
  - **对结构化文档而言**（合同 / 政策 / 报告 / CSV / 有标题的 DOCX / PP-StructureV3 / Azure DI prebuilt-layout）——标题、表格、列表是下游切块和 LLM 理解的**真信号**，全力利用
  - **对无结构内容而言**（OCR 散段落 / 纯 txt / PP-OCRv4 行级输出 / 单句便签）——Markdown 是**容器命名**，**不是**信号增益；保留 Markdown 路径只是为了下游 chunker / 内置 LLM 分类 / 自定义字段抽取消费同一种格式。诚实承认这一点，不要把扁平段落包装成"也是 Markdown 信号"
  - **翻译职责在 Provider 内部完成**——`OcrResult` / `TextExtractionResult` 不暴露 RawText 字段，Provider 拿到底层服务的纯文本输出后**自己**负责包成扁平 Markdown（例如 `string.Join("\n\n", paragraphs)`），不允许把 plain-text-to-markdown 的兜底逻辑泄漏给上游 orchestrator
- **持久化**：`Document.Markdown` 是 Document 聚合根上唯一的文本字段，**禁止**在 `Document` 或事件载荷上引入并行的 plain-text 字段
- **下游消费**：下游 RAG 向量化 / 内置 LLM 分类 / 类型绑定字段抽取，统一消费 Markdown
- **纯文本投影**：仅在消费侧（如关键字兜底分类器）按需通过 `Dignite.Paperbase.Documents.MarkdownStripper.Strip(...)` 即时计算，**不持久化**也**不在契约上并列暴露**
- **Prompt 表达**：内部 LLM 系统提示词显式告知"输入是 Markdown"，让模型把结构标记当作语义信号利用

**Markdown-first 是工程默认，不是哲学原则。** Markdown 是文本载荷，但 **out-of-band 信号**（坐标 / 置信度 / page metadata / 表单 key-value 结构 / 印章位置）与 Markdown **正交**。未来若需 page-aware citations、签章定位、表单 key-value 抽取，应作为 `TextExtractionResult` 上**具名可选独立扩展字段**（例如 `IReadOnlyList<PageBlock>? PageBlocks`，可空、与 Markdown 不耦合），或独立 extractor 接口（与 `ITextExtractor` 正交）——不被"Markdown 是唯一文本载荷"的字面理解挡掉。

- **禁用模式**：在 `TextExtractionResult` 上加 `Dictionary<string, object>` / `Dictionary<string, string>` 类型的**通用"扩展槽"**——这是 code smell，未来类型不清、消费侧 cast 满天飞、对 LLM-facing schema 不友好
- **正确做法**：每加一种 out-of-band 信号**单独开 Issue 讨论**（属架构决策），按需加**具名、强类型、可空**的字段；如果该信号与 OCR 强相关而与 Markdown Provider 无关，考虑加在 `OcrResult` 而非 `TextExtractionResult` 上以避免责任错位

**Document 字段扩展判定**：上述原则在 transient transport（`TextExtractionResult` / `OcrResult`）层级，到 `Document` 聚合根（持久化层、跨下游消费方共享的 truth source）规则更严。两轴判定：

1. **文本类型字段：永远只有 `Markdown` 一个。** 这是 Markdown-first 在持久化层的硬约束（已被 `Document.SetMarkdown` 的 immutability 强保护在代码层面执行）。任何派生文本（Summary / Outline / SectionsJson）走 `MarkdownStripper.Strip` 或切块器在消费侧投影，**不持久化**。`Title` 是 Markdown 派生的展示快照（不可变），不是新文本载荷；`ClassificationReason` 是 AI 决策解释（不是文档内容）
2. **非文本类型字段：按"通用 truth source vs 业务专属"判定**：
   - **跨下游消费方共享的通用 truth source**（如 `PageBlocks` 用于任何业务的 citation 高亮、OCR Provider name/version 用于调试）→ 可加到 `Document`，仍需开 Issue 讨论形状
   - **业务专属**（合同金额 / 发票号 / 身份证姓名 / 收据条目）→ 由下游业务消费方在自己的聚合根（下游 `Contract` / `Invoice` / `IdCardRecord`）里存储，**`Document` 不污染**

这条规则同时回答"OCR out-of-band 信号该放哪里"——它既不属于下游业务（与具体业务无关）、也不能塞回 Markdown 字符串（破坏 Markdown-first）。它该在 `Document` 层面承载，但每加一种**单独开 Issue**，按需加具名强类型可选字段，**禁止** `Dictionary<string, object>` 通用扩展槽。

## 安全约定（适用于所有内部 LLM 调用路径）

以下安全约定适用于 Paperbase 内部 LLM 调用路径（内置 LLM 分类、Host 字段抽取、租户字段抽取（B 机制）等）：

- **Fail-closed 安全断言**：任何由 LLM 触发或参数受 LLM 输出影响的查询路径，必须显式做**权限断言**（`IAuthorizationService.CheckAsync(...)`——AppService 上的 `[Authorize]` 在 MCP / 反射 / tool-dispatch 路径不触发）+ 结果集硬上限（`Take(N)`），不得裸跑 raw SQL
- **PromptBoundary**：用户派生的自由文本字段（title / partyName / summary / 文档内容等）进入 LLM prompt 或 LLM-facing 输出前，必须经 `PromptBoundary.WrapField(...)` 包裹，防止 prompt injection 注入向量
- **Description / Instructions 编译期常量**：任何 LLM-facing description / instructions 都必须是**编译期常量**或纯静态字符串字面量，**禁止**运行时拼接用户控制的字符串
- **多租户隔离**：租户边界由 ABP `IMultiTenant` 全局过滤器施加（框架默认行为，不手写 `CurrentTenant.Id` 谓词）；**唯一纪律——LLM 触发路径上不得 `Disable<IMultiTenant>()` / `IgnoreQueryFilters()` 击穿它**（细节见 `.claude/rules/llm-call-anti-patterns.md`）

## 处理规则

1. 在 core 中开发时，严格遵循 `.claude/rules/` 中的规则
   - 修改 ABP BackgroundJob / JobArgs 时必须读取 `.claude/rules/background-jobs.md`
2. 模块中不要配置中间件，仅在 host 中配置
3. **改动前先判断是否需要 Issue**：涉及通道边界（OCR 流水线 / 出口契约 / 字段架构 / 文档类型 Tier 体系 / Markdown-first / 安全约定）、影响模块边界、或属于 Slice 任务的改动，**先停下，告知用户开 GitHub Issue 后再动手**；纯实现细节的 fix（如 bug fix、措辞修正）直接用 commit message 记录即可
4. **下游消费方相关问题**：业务模块（合同 / 发票管理等）不属于 Paperbase 范畴。涉及下游消费方实现的讨论，明确指出属于 out-of-scope，Paperbase 只保证出口契约稳定
