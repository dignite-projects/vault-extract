---
name: abp-document-boundary-check
description: 校验 Document 聚合根是否仍然是"纯基础设施聚合根"，扫描是否引入了禁止出现的业务字段（合同金额、到期日、发票号等）。在修改 Document.cs 或新增业务模块时使用，或在 Slice 收尾前手动调用 /abp-document-boundary-check 进行复核。
---

# Document 聚合根边界校验

## 背景与不变量

`CLAUDE.md` 中声明了一条**强制约束**：

> `Document` 是**纯基础设施聚合根**，职责限于：文件存储、生命周期状态机、流水线 Run 记录、文本提取结果、AI 分类结果、向量化状态。
>
> **禁止**在 `Document` 上添加任何来自业务模块的字段，例如合同金额、到期日、对方名称、发票号等。这类字段属于业务模块自己的聚合根，由业务模块在收到 `DocumentClassifiedEto` 后自行持久化和查询。

**判断依据**：如果一个字段的含义只有在特定业务场景（合同、发票、报销单…）下才成立，它就不属于 `Document`。

## 当前允许出现在 Document.cs 上的字段集合

以下字段属于"纯基础设施"范畴，是合规的：

| 字段类别 | 字段名（示例） |
| --- | --- |
| 多租户 | `TenantId` |
| 文件存储 | `OriginalFileBlobName`, `FileOrigin` |
| 生命周期 | `LifecycleStatus` |
| 文本提取 | `ExtractedText` |
| AI 分类（通用元数据） | `DocumentTypeCode`, `ClassificationConfidence`, `ClassificationReason`, `ReviewStatus` |
| 流水线编排 | `PipelineRuns` 集合, `HasEmbedding` 派生 |

> 注意：`DocumentTypeCode` 是**通用类型标识**（"contract"、"invoice"…），不是业务字段。新增的字段如果性质上类似（任何文档类型都需要），通常允许；如果只对某个特定类型有意义，禁止。

## 禁止模式（红灯）

任何字段名或属性含义只在某个业务场景下成立，都应当迁移到该业务模块自己的聚合根。常见红灯关键词：

- 合同相关：`Amount`, `Currency`, `ContractNumber`, `EffectiveDate`, `ExpirationDate`, `ExpiryDate`, `Counterparty`, `CounterpartyName`, `SignedAt`, `SignedBy`, `ContractType`
- 发票相关：`InvoiceNumber`, `InvoiceCode`, `TaxAmount`, `TaxRate`, `Buyer`, `Seller`, `IssuedAt`, `LineItems`
- 报销/财务：`ReimbursementCategory`, `Reimbursable`, `CostCenter`, `Project`
- 证照/人事：`HolderName`, `IDNumber`, `IssuingAuthority`, `LicenseNumber`, `ValidFrom`, `ValidUntil`
- 任何包含特定行业术语的字段（"PolicyNumber"、"PatientId"、"ClaimAmount" …）

## 触发场景

满足以下条件之一时，主动运行此检查：

1. 用户编辑了 `core/src/Dignite.Paperbase.Domain/Documents/Document.cs`。
2. 用户在 `core/src/Dignite.Paperbase.Application.Contracts/Documents/DocumentDto.cs` 添加了新属性（DTO 是 Document 的对外投影，污染 DTO 等价于污染聚合根）。
3. 用户提到要"把 X 字段加到 Document 上"。
4. 新增 EF Core migration 触及了 `PaperbaseDocuments` 表。
5. 用户运行 `/abp-document-boundary-check`。

## 执行步骤

1. **读取当前 Document 字段清单**

   ```bash
   # 用 Grep 抽取所有 public/protected/internal 属性
   ```

   使用 Grep 在以下路径搜索属性声明：
   - `core/src/Dignite.Paperbase.Domain/Documents/Document.cs`
   - `core/src/Dignite.Paperbase.Application.Contracts/Documents/DocumentDto.cs`
   - `core/src/Dignite.Paperbase.EntityFrameworkCore/EntityFrameworkCore/PaperbaseDbContextModelCreatingExtensions.cs`（找到 `builder.Entity<Document>` 配置块）

2. **对每个字段判定归属**

   对照上面的"允许字段类别"表与"禁止模式"关键词。对疑似违规的字段，**不要立即结论违规**，先回答下面这组问题：

   - 这个字段的语义在所有文档类型下都成立吗？（合规：是；违规：否）
   - 它是元数据/状态/编排，还是业务事实？（合规：前者；违规：后者）
   - 它的写入者是 `DocumentPipelineRunManager` / 流水线，还是某个业务模块的字段提取器？（合规：前者；违规：后者）

3. **如发现违规，给出迁移建议**

   不要尝试直接修复；而是输出：
   - 违规字段名
   - 该字段属于哪类业务领域（合同 / 发票 / 报销 / 证照 等）——下游业务消费方在自己的仓库实现
   - 明确指出："此字段属于下游业务聚合根，由下游消费方订阅 `DocumentClassifiedEto` / `DocumentReadyEto` 后在自己的聚合根持久化；Paperbase Document 是纯基础设施聚合根，不污染。"
   - 引用 `CLAUDE.md` 中相关段落让用户复核。

4. **如未发现违规，给出简短确认**

   列出当前所有字段及其归属类别，确认全部位于"允许字段类别"表内，并报告所检查的提交范围（例如最近一次 `git diff Document.cs`）。

## 不做什么

- **不要**主动修改 `Document.cs` 删除字段——这是用户的设计决策，需要用户确认后才能改。
- **不要**把 `Document.cs` 上已有的字段（如 `ClassificationReason`、`DocumentTypeCode`）误判为违规——它们是**通用元数据**，参见允许字段表。
- **不要**对 `DocumentPipelineRun`、`DocumentChunk` 等子实体应用同样的规则——它们是聚合根内部的基础设施实体，与业务边界讨论的"Document 聚合根本身"不同。

## 参考资料

- 项目根 `CLAUDE.md` → "Document 聚合根边界（强制）"小节
- `.claude/rules/ddd-patterns.md` → 聚合根设计与 DDD 不变量
- `.claude/rules/dependency-rules.md` → 跨层/跨模块的依赖方向
