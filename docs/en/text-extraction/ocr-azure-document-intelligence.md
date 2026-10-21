```json
//[doc-seo]
{
    "Description": "Use Azure AI Document Intelligence (prebuilt-layout) as Dignite Vault Extract's OCR provider: enabling the module, configuration and F0 tier limits."
}
```

# Azure Document Intelligence

> One of Dignite Vault Extract's OCR providers. Overview and comparison: [Text Extraction](text-extraction.md).

> 🧪 **Status: awaiting real-world validation.** The provider is implemented and unit-tested (at the mocked SDK boundary), but has not yet been run against a live Azure Document Intelligence resource. If you use Azure DI, testing and feedback are very welcome — see [#327](https://github.com/dignite-projects/vault-extract/issues/327).

Recommended for production workloads where data is allowed to leave the network and the team prefers not to operate a sidecar.

1. Create an Azure AI Document Intelligence resource (F0 for trial, S0 for production).
2. Copy the **Endpoint** and **API Key**.
3. In the host module's `[DependsOn]`, replace `VaultExtractVisionLlmOcrModule` (the shipped default) with `VaultExtractAzureDocumentIntelligenceModule`, and reference `Dignite.Vault.Extract.Ocr.AzureDocumentIntelligence` instead (in this repository, swap the commented `<ProjectReference>` lines in `host/src/Dignite.Vault.Extract.Host.csproj`). Remove the keyed vision `IChatClient` registration from `ConfigureAI` too: it fails at startup without `Vault:Extract:VisionOcrModelId`.
4. Add the endpoint and key to `host/src/appsettings.secrets.json`, `appsettings.Production.json` or environment variables:

```json
"AzureDocumentIntelligence": {
  "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
  "ApiKey": "YOUR_KEY"
}
```

`VaultExtractAzureDocumentIntelligenceModule` binds the `AzureDocumentIntelligence` section.

Dignite Vault Extract fixes the Azure model to `prebuilt-layout` and does not expose it as a config option — it emits the structured Markdown that Markdown-first requires. `prebuilt-read` (plain text only) and business prebuilts (invoice / contract) are intentionally not channel-layer OCR options.

> ⚠️ **F0 limitations** — each request only processes the **first 2 pages**, only one F0 resource per subscription per region, ~1–2 TPS throughput. Suitable only for demos and short documents (≤ 2 pages). Switch to S0 for sustained development or any larger document.

## See Also

- [Text Extraction](text-extraction.md) — the Markdown-first contract, the two extraction paths, provider comparison
- [Deployment](../deployment/deployment.md)
