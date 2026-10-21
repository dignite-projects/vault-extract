```json
//[doc-seo]
{
    "Description": "Reference of Dignite Vault Extract's configuration sections and options classes, the VaultExtract.Enable feature, the permission tree and the per-document-type permissions."
}
```

# Options and Features

This page lists every configuration point of Dignite Vault Extract in one place: the configuration sections and options classes, the feature, and the permissions. Each entry links to the page that explains it.

## Configuration Sections

Sections bound from `appsettings.json` (or any other configuration source; environment variables replace `:` with `__`):

| Section | Options class | Package | Description |
| --- | --- | --- | --- |
| `Vault:Extract` | none (read by the host) | Host | LLM endpoint, key and model ids. [AI Provider](ai-provider.md#provider-wiring-extract) |
| `Vault:ExtractBehavior` | `VaultExtractBehaviorOptions` | `Dignite.Vault.Extract.Application` | Prompt language and the size ceilings of every LLM call. [AI Provider](ai-provider.md#cross-cutting-llm-behavior-extractbehavior) |
| `VisionLlmOcr` | `VisionLlmOcrOptions` | `Dignite.Vault.Extract.Ocr.VisionLlm` | Output cap, image downscaling, PDF page cap, repetition guards. [Vision-LLM OCR](../text-extraction/ocr-vision-llm.md#configuration-visionllmocr) |
| `PaddleOcr` | `PaddleOcrOptions` | `Dignite.Vault.Extract.Ocr.PaddleOcr` | Sidecar endpoint, model, languages, timeout. [PaddleOCR](../text-extraction/ocr-paddleocr.md#configuration-paddleocr) |
| `AzureDocumentIntelligence` | `AzureDocumentIntelligenceOptions` | `Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence` | Endpoint and key. [Azure Document Intelligence](../text-extraction/ocr-azure-document-intelligence.md) |
| `PdfExtractor` | `PdfExtractorOptions` | `Dignite.Vault.Extract.Parse.Pdf` | Embedded-image limits, scan-layer detection, table reconstruction. [Text Extraction](../text-extraction/text-extraction.md#extractor-options) |
| `OpenXmlExtractor` | `OpenXmlExtractorOptions` | `Dignite.Vault.Extract.Parse.OpenXml` | Embedded-image limits, speaker notes. [Text Extraction](../text-extraction/text-extraction.md#extractor-options) |
| `Mcp:RateLimit` | `McpRateLimiterOptions` (bound by the host) | `Dignite.Vault.Extract.Mcp` | Per-IP rate limit of `/mcp`. [MCP Server](../egress/mcp-server.md#rate-limiting) |
| `Mcp:ForwardedClientIp` | `McpForwardedClientIpOptions` (bound by the host) | `Dignite.Vault.Extract.Mcp` | Trusted reverse proxies. [MCP Server](../egress/mcp-server.md#reverse-proxy-client-ips) |
| `OpenIddict:Applications:VaultExtract_Mcp` | none (read by the host's seeder) | Host | The preset MCP OAuth client and its redirect URIs. [MCP Server](../egress/mcp-server.md#registered-callbacks) |
| `OpenTelemetry` | none (read by the host) | Host | Telemetry export. [Observability](../deployment/observability.md) |

## Options Set in Code

Some settings are not bound from configuration; set them in your host module.

| Setting | Default | Description |
| --- | --- | --- |
| `VaultExtractMcpOptions.AllowExplicitTenantScope` | `false` | Allows MCP callers to target another tenant explicitly (also needs a custom `IMcpTenantAccessValidator`). [MCP Server](../egress/mcp-server.md#tenant-scope) |
| `VaultExtractMcpOptions.ResourceListContributors` | Document types, cabinets | Categories listed by MCP `resources/list`. [MCP Server](../egress/mcp-server.md#extending-the-mcp-surface) |
| `DocumentConsts.MaxUploadFileBytes` | 20 MiB | Upload size limit; raise the server's request limits with it. [Documents and Review](../documents/documents.md#accepted-files) |
| `DocumentConsts.AllowedUploadContentTypesByExtension` | See the page | Accepted extension and content-type pairs. [Documents and Review](../documents/documents.md#accepted-files) |
| `DocumentExportConsts.MaxExportDocumentCount` | 10,000 | Rows per download. [Data Download](../egress/data-download.md#limits-and-safety) |
| `DocumentExportConsts.MaxColumnCount` | 100 | Field columns per download. [Data Download](../egress/data-download.md#limits-and-safety) |
| `VaultExtractDbProperties.DbTablePrefix` / `DbSchema` | `Vault` / none | Database table names. [Installation](../get-started/installation.md#database) |
| `NotificationRoutingOptions` | `SignalR` for every Vault Extract notification | Notification channels. [Operator Notifications](../egress/operator-notifications.md) |

The MCP result caps (`VaultExtractMcpConsts`) are compile-time constants on purpose, so that configuration cannot widen an LLM-facing boundary. See [MCP Server: Limits](../egress/mcp-server.md#limits).

Vault Extract defines **no ABP settings**: nothing in it is configured through Setting Management.

## Features

| Feature | Default | Description |
| --- | --- | --- |
| `VaultExtract.Enable` | `true` | Whether a tenant may use Vault Extract. A toggle, visible to clients |

The feature gates every Vault Extract application service (`[RequiresFeature]` on `VaultExtractAppService`), and with them the REST API and the MCP tools and resources, which only call those services. The Angular menu is hidden when the feature is explicitly `false`.

To make Vault Extract opt-in (for example per edition in a SaaS host), override the default to `false` in your own `FeatureDefinitionProvider` and grant the feature per edition or tenant. A host without Feature Management changes nothing.

The document pipeline is not gated: its background jobs and event handlers run without a signed-in user and never call an application service, so documents already uploaded finish processing.

## Permissions

Every permission belongs to the `VaultExtract` group and can be granted on both the Host and tenant sides.

| Permission | Display name | Grants |
| --- | --- | --- |
| `VaultExtract.Documents` | Access Documents | **Entry** to the documents domain. Admits the caller; returns no document by itself |
| `VaultExtract.Documents.ReadAll` | Read Documents of All Types | Reading every document of the layer, including untyped ones; statistics |
| `VaultExtract.Documents.Upload` | Upload into All Document Types | Uploading into any type, or untyped |
| `VaultExtract.Documents.Delete` | Delete Documents of All Types | Deleting and restoring any document |
| `VaultExtract.Documents.PermanentDelete` | Permanently Delete Documents | Permanent deletion |
| `VaultExtract.Documents.Export` | Download Data | CSV / XLSX download (with read access to the type) |
| `VaultExtract.Documents.ConfirmClassification` | Edit Documents of All Types | Editing and reviewing any document, assigning types |
| `VaultExtract.Documents.Pipelines.Retry` | Retry Failed Pipelines | Retrying failed stages of any document |
| `VaultExtract.Documents.Reprocessing.FieldExtraction` | Batch Re-extract Fields | Batch field re-extraction |
| `VaultExtract.Documents.Reprocessing.Reclassification` | Batch Reclassify | Batch reclassification |
| `VaultExtract.DocumentTypes` (`.Create`, `.Update`, `.Delete`) | Document Types | Managing document types |
| `VaultExtract.DocumentTypes.ManagePermissions` | Manage Document Type Permissions | Granting the per-type permissions below |
| `VaultExtract.FieldDefinitions` (`.Create`, `.Update`, `.Delete`) | Field Definitions | Managing fields |
| `VaultExtract.Cabinets` (`.Create`, `.Update`, `.Delete`) | Cabinets | Viewing and managing cabinets |

`VaultExtract.Documents.Pipelines` and `VaultExtract.Documents.Reprocessing` are grouping parents in the permission tree.

### Per-Document-Type Permissions

Each document type also carries four [resource permissions](https://abp.io/docs/latest/framework/fundamentals/authorization/resource-based-authorization), granted to a user or role for that one type (resource name `Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType`):

| Permission | Display name |
| --- | --- |
| `Upload` | Upload into this Document Type |
| `Read` | Read All Documents of this Type |
| `Edit` | Edit All Documents of this Type |
| `Delete` | Delete All Documents of this Type |

How the module-wide permissions, the per-type grants and document ownership combine is described in [Per-Document-Type Permissions](document-type-permissions.md).

### Seeded Roles

The shipped host seeds two operator roles in the Host and in every tenant (the modules seed none):

| Role | Permissions |
| --- | --- |
| `DocumentManager` | `Documents`, `Documents.ReadAll`, `Documents.Upload`, `Documents.Export`, `Documents.ConfirmClassification` |
| `Viewer` | `Documents`, `Documents.ReadAll` |

## See Also

- [Installation](../get-started/installation.md)
- [AI Provider](ai-provider.md)
- [Per-Document-Type Permissions](document-type-permissions.md)
