```json
//[doc-seo]
{
    "Description": "Add Dignite Vault Extract to an ABP application: NuGet packages and module dependencies, database integration, text-extraction providers, LLM clients and the Angular UI."
}
```

# Installation

Dignite Vault Extract is distributed as an ABP application module: a set of NuGet packages for the server and the `@dignite/ng.vault-extract` npm package for the Angular operator UI. This repository's `host/` is one host built from them; this page describes how to add the modules to an ABP application of your own.

> To run this repository itself, see [Local Development](local-development.md).

## Requirements

| Requirement | Version |
| --- | --- |
| ABP Framework | 10.7.0 or later (every package depends on `Volo.Abp.* >= 10.7.0`) |
| .NET | 10.0 |
| Angular (for the UI) | 22, with `@abp/ng.*` `~10.7.0` |
| Database | A relational database through EF Core. The shipped host uses SQL Server; the schema is provider-agnostic ([Database Portability](../deployment/deployment.md#database-portability)) |
| LLM provider | Mandatory. Any provider with a `Microsoft.Extensions.AI` `IChatClient`; see [AI Provider](../configuration/ai-provider.md) |

The packages are licensed under LGPL-3.0-only.

## Packages

Every package ID is the project name. Install each package into the project of the matching layer, and add its module class to that project's module `[DependsOn]`.

### Application Stack

| Package | Module class | Layer |
| --- | --- | --- |
| `Dignite.Vault.Extract.Domain.Shared` | `VaultExtractDomainSharedModule` | Domain.Shared |
| `Dignite.Vault.Extract.Domain` | `VaultExtractDomainModule` | Domain |
| `Dignite.Vault.Extract.Application.Contracts` | `VaultExtractApplicationContractsModule` | Application.Contracts |
| `Dignite.Vault.Extract.Application` | `VaultExtractApplicationModule` | Application |
| `Dignite.Vault.Extract.EntityFrameworkCore` | `VaultExtractEntityFrameworkCoreModule` | EntityFrameworkCore |
| `Dignite.Vault.Extract.HttpApi` | `VaultExtractHttpApiModule` | HttpApi: the [REST API](../egress/rest-api.md) |
| `Dignite.Vault.Extract.HttpApi.Client` | `VaultExtractHttpApiClientModule` | HttpApi.Client: C# client proxies for a remote caller |
| `Dignite.Vault.Extract.Mcp` | `VaultExtractMcpModule` | Web host (optional): the [MCP server](../egress/mcp-server.md) |

These packages come along as dependencies and need no direct reference:

- `Dignite.Vault.Extract.Abstractions`: the [integration event](../egress/integration-events.md) ETOs and the `ITextExtractor` contract. A downstream consumer references this package alone.
- `Dignite.Vault.Extract.FlexFields`: Vault Extract's `Tags` field type for the `Dignite.Abp.FlexFields` kernel.
- `Dignite.Abp.FlexFields.*`, `Dignite.Abp.Notifications.Abstractions` and `Volo.Abp.Gdpr.Abstractions`, from the [abp-modules](https://github.com/dignite-projects/abp-modules) repository and ABP.

### Text Extraction

The Application layer turns an upload into Markdown through `ITextExtractor`, but does **not** depend on an implementation: the host chooses one. Add the orchestrator, the digital-document providers you need, and **exactly one** OCR provider:

| Package | Module class | Role |
| --- | --- | --- |
| `Dignite.Vault.Extract.Parse` | `VaultExtractParseModule` | Required. The `ITextExtractor` implementation that dispatches by file type |
| `Dignite.Vault.Extract.Parse.Pdf` | `VaultExtractParsePdfModule` | Digital PDFs |
| `Dignite.Vault.Extract.Parse.OpenXml` | `VaultExtractParseOpenXmlModule` | Word, Excel and PowerPoint files, including their embedded images |
| `Dignite.Vault.Extract.Parse.ElBrunoMarkItDown` | `VaultExtractParseElBrunoMarkItDownModule` | Catch-all for other digital formats |
| `Dignite.Vault.Extract.Ocr.VisionLlm` | `VaultExtractVisionLlmOcrModule` | OCR through a vision-capable chat model (the shipped default) |
| `Dignite.Vault.Extract.Ocr.PaddleOcr` | `VaultExtractPaddleOcrModule` | OCR through a local PaddleOCR sidecar |
| `Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence` | `VaultExtractAzureDocumentIntelligenceModule` | OCR through Azure Document Intelligence |

The OCR providers are mutually exclusive. See [Text Extraction](../text-extraction/text-extraction.md) for how to choose and configure them.

### Module Dependencies

For a single-project host, the dependencies look like this (from `host/src/VaultExtractHostModule.cs`):

```csharp
[DependsOn(
    // ...ABP framework modules...
    typeof(VaultExtractHttpApiModule),
    typeof(VaultExtractMcpModule),
    typeof(VaultExtractApplicationModule),
    typeof(VaultExtractEntityFrameworkCoreModule),
    typeof(VaultExtractParseModule),
    typeof(VaultExtractParsePdfModule),
    typeof(VaultExtractParseOpenXmlModule),
    typeof(VaultExtractParseElBrunoMarkItDownModule),
    typeof(VaultExtractVisionLlmOcrModule)
)]
public class MyHostModule : AbpModule
{
}
```

In a layered solution, put each module in the `[DependsOn]` of the project for its layer, as listed above, and the parse and OCR modules in the web host.

## Database

Vault Extract's `VaultExtractDbContext` uses the connection string named **`VaultExtract`** and falls back to `Default` when that is not set. Its entities are multi-tenant (`IMultiTenant`); none ignores multi-tenancy.

Call `ConfigureVaultExtract()` in the `OnModelCreating` of the `DbContext` your solution migrates from, then add and apply a migration:

```csharp
protected override void OnModelCreating(ModelBuilder builder)
{
    base.OnModelCreating(builder);

    // ...other modules...
    builder.ConfigureVaultExtract();
}
```

```bash
dotnet ef migrations add Added_VaultExtract
```

Table names use the prefix `VaultExtractDbProperties.DbTablePrefix` (default `Vault`) and the schema `VaultExtractDbProperties.DbSchema` (default none). Both are static properties; set them before the model is built if you need different names.

| Table | Holds |
| --- | --- |
| `VaultDocuments` | Documents: Markdown, system fields, the field-value bag (`FlexFields`, a JSON column) and the upload metadata (`FileOrigin`, an owned type) |
| `VaultDocumentTypes` | Document types |
| `VaultFields` | Field definitions (`Field`, implementing the FlexFields kernel's `IFlexField`) |
| `VaultDocumentFlexFieldIndexes` | The derived, rebuildable query index of field values |
| `VaultDocumentFieldValidationWarnings` | Field values flagged for review |
| `VaultDocumentPipelineRuns` | Pipeline run history |
| `VaultDocumentSegments` | The internal ledger of detected sub-documents |
| `VaultCabinets` | Cabinets |

Per-document-type grants are stored by ABP's Permission Management in `AbpResourcePermissionGrants`. The pipeline also needs ABP's background job tables and, for at-least-once events, the event outbox (and inbox) on the same `DbContext`; see [Host Configuration](#host-configuration).

> Upgrading a 0.3.x deployment that holds field values needs a data migration before the 0.5 schema; see [Field Architecture v2 → v3 Migration](../deployment/field-architecture-v3-migration.md).

## Host Configuration

The modules leave these decisions to the host:

| Concern | What to configure | See |
| --- | --- | --- |
| **LLM clients** | Register the keyed `IChatClient` instances `VaultExtractConsts.TitleGeneratorChatClientKey` and `VaultExtractConsts.StructuredChatClientKey`, plus `VisionLlmOcrConsts.VisionChatClientKey` with Vision-LLM OCR. No default `IChatClient` is used | [AI Provider](../configuration/ai-provider.md) |
| **Original files** | A BLOB provider for the `extract-documents` container (`VaultExtractDocumentContainer`). The shipped host uses the file system under `App_Data/blobs` | [Deployment](../deployment/deployment.md#original-file-storage) |
| **Background jobs** | The pipeline runs as ABP background jobs. Use a persistent job store (the shipped host adds `AbpBackgroundJobsEntityFrameworkCoreModule` and `builder.ConfigureBackgroundJobs()`) | [Pipeline Runs](../pipeline/pipeline-runs.md) |
| **Distributed events** | Configure the event outbox (`IHasEventOutbox`) on your `DbContext` for at-least-once delivery, and an ABP event bus provider to reach external consumers | [Integration Events](../egress/integration-events.md) |
| **Operator notifications** | Optional. Install the Notification Center, or the notifications client for a split deployment; without either, notifications are logged and dropped | [Operator Notifications](../egress/operator-notifications.md) |
| **MCP** | Optional. `VaultExtractMcpModule` brings the shared MCP server; add OAuth discovery (`AddAbpMcpAuthenticationDiscovery`) and, if wanted, the rate limiter | [MCP Server](../egress/mcp-server.md) |

The application services are gated by the `VaultExtract.Enable` feature, which is on by default; see [Options and Features](../configuration/options.md#features). Users need permissions before they see anything; see [Permissions](../configuration/options.md#permissions).

Vault Extract ships **no document types**. After installation an administrator creates the types and their fields; see [Document Types and Fields](../documents/document-types.md).

## Angular UI

Install the package into your Angular application:

```bash
npm install @dignite/ng.vault-extract
```

It depends on `@abp/ng.*` `~10.7.0`, `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor`, and peers on Angular 22, `@ng-bootstrap/ng-bootstrap`, `ng-zorro-antd` and `rxjs`. The package has three entry points: `@dignite/ng.vault-extract` (generated proxies, shared tokens and services), `/config` and `/documents`.

Register the menu in `app.config.ts`:

```ts
import { provideExtract } from '@dignite/ng.vault-extract/config';

export const appConfig: ApplicationConfig = {
  providers: [
    // ...ABP core, theme, etc.
    provideExtract(),
  ],
};
```

Mount the routes at **`vault-extract`** in `app.routes.ts`; the menu entries are registered under that prefix:

```ts
{
  path: 'vault-extract',
  loadChildren: () => import('@dignite/ng.vault-extract/documents').then(m => m.DOCUMENTS_ROUTES),
}
```

Add the two stylesheets the field editor loads at runtime to the `styles` array of your build target in `angular.json`. `inject: false` is required: the files are fetched by these fixed bundle names, and an injected entry would be emitted under a content hash and fail to load.

```jsonc
{ "input": "node_modules/ng-zorro-antd/select/style/index.min.css", "inject": false, "bundleName": "ng-zorro-antd-select" },
{ "input": "node_modules/ckeditor5/dist/ckeditor5.css", "inject": false, "bundleName": "ckeditor5" }
```

The generated proxies use the `Default` API name, so `environment.apis.default.url` must point at the host that serves the Vault Extract HTTP API.

### What It Adds

`provideExtract()` adds a **Vault Extract** menu group (localization key `VaultExtract::Menu:VaultExtract`) and hides it while the current tenant has `VaultExtract.Enable` explicitly set to `false`. The routes are not feature-guarded: a direct URL still opens the page, and the server refuses its calls.

| Menu item | Route | Required policy |
| --- | --- | --- |
| Overview | `/vault-extract/overview` | `VaultExtract.Documents` |
| Document List | `/vault-extract/documents` (detail: `/vault-extract/documents/:id`, file preview: `/vault-extract/documents/:id/file`) | `VaultExtract.Documents` |
| Document Types | `/vault-extract/document-types` (fields: `/vault-extract/document-types/:typeId/fields`, which requires `VaultExtract.FieldDefinitions`) | `VaultExtract.DocumentTypes` |
| Cabinets | `/vault-extract/cabinets` | `VaultExtract.Cabinets` |
| Recycle Bin | `/vault-extract/documents-recycle-bin` | `VaultExtract.Documents` |

`DOCUMENTS_ROUTES` registers every field type its pages need (the flex-fields built-ins, CKEditor and the Tags type) on its own lazy route, so the host calls neither `provideFlexFields()` nor any `provide…FieldType()`. `EXTRACT_PERMISSIONS`, `EXTRACT_FEATURES` and `isExtractFeatureDisabled` are exported for hosts that build their own navigation.

The notification bell and inbox are not part of this package; the shipped Angular host adds them from `@dignite/ng.notification-center`. See [Operator Notifications](../egress/operator-notifications.md).

## Localization

The module's localization resource is `VaultExtractResource` (resource name `VaultExtract`), with `en`, `ja`, `zh-Hans` and `zh-Hant`; the Tags field type has its own `VaultExtractFlexFields` resource in the same languages. Error codes use the `Extract:` namespace. To change a text, override the key in the `VaultExtract` resource itself: the UI writes `VaultExtract::Key`, so an override in your application's default resource does not apply.

## See Also

- [Local Development](local-development.md)
- [AI Provider](../configuration/ai-provider.md)
- [Options and Features](../configuration/options.md)
- [Deployment](../deployment/deployment.md)
- [ABP Modularity](https://abp.io/docs/latest/framework/architecture/modularity/basics)
