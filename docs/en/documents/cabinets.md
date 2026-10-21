```json
//[doc-seo]
{
    "Description": "Organize Dignite Vault Extract documents into cabinets: a manual filing dimension, independent of document types, with an optional AI suggestion at upload."
}
```

# Cabinets

A **cabinet** is a folder-like filing dimension for documents, maintained by people: "2026 accounting", "Customer A", "Pending legal review". A document is in at most one cabinet. Cabinets are independent of [document types](document-types.md): the pipeline never reads a document's cabinet, classification and field extraction ignore it, and it plays no part in the Ready gate.

## Managing Cabinets

Cabinets are managed on **Vault Extract → Cabinets** (`ICabinetAppService`). Each layer (the Host and each tenant) has its own cabinets.

| Property | Constraints |
| --- | --- |
| `Name` | Required, at most 128 characters, no control characters, unique among the layer's active cabinets |
| `Description` | Optional, at most 512 characters, no control characters |

**Deleting** a cabinet takes every document out of it first (recycle-bin documents included, in one update, with no events), then deletes the cabinet. The documents themselves are not touched otherwise. A deleted cabinet cannot be restored.

## Filing Documents

A document's cabinet is set in one of three ways:

1. **At upload**, with `CabinetId`.
2. **By an operator**, from the document's detail page (`POST /api/vault-extract/documents/{id}/cabinet`; a `null` cabinet takes the document out).
3. **By the AI suggestion**, once, after the first text extraction, only when the document has no cabinet yet. The LLM chooses among the layer's cabinets (at most `MaxCabinetsInSuggestionPrompt`, default 50) using the opening of the Markdown, and abstains below `MinCabinetSuggestionConfidence` (default 0.6), leaving the document unfiled. An operator's choice always wins, and the suggestion never retries. See [AI Provider](../configuration/ai-provider.md#cross-cutting-llm-behavior-extractbehavior).

Sub-documents are not filed automatically.

## Using Cabinets

- The document list, [Data Download](../egress/data-download.md) and the REST list endpoint filter by `CabinetId`.
- The [MCP server](../egress/mcp-server.md) exposes cabinets as resources (`vault-extract://cabinets/{id}`) and through the `vault_extract_list_cabinets` tool, and `vault_extract_search_documents` accepts `cabinetId` as a scope. A cabinet-scoped search may span document types.

## Permissions

| Permission | Allows |
| --- | --- |
| `VaultExtract.Cabinets` | Viewing cabinets, and choosing a cabinet when uploading or filing a document |
| `VaultExtract.Cabinets.Create` / `.Update` / `.Delete` | Creating, renaming and deleting cabinets |

Filing a document also needs the right to edit that document. The MCP surface lists cabinets to any caller with `VaultExtract.Documents`.

## See Also

- [Documents and Review](documents.md)
- [MCP Server](../egress/mcp-server.md)
