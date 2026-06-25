# Azure Document Intelligence — cloud

> One of Dignite Vault Extract's OCR providers. Overview & comparison: [text-extraction.md](text-extraction.md).

> 🧪 **Status: awaiting real-world validation.** The provider is implemented and unit-tested (at the mocked SDK boundary), but has not yet been run against a live Azure Document Intelligence resource. If you use Azure DI, testing and feedback are very welcome — see [#327](https://github.com/dignite-projects/vault-extract/issues/327).

Recommended for production workloads where data is allowed to leave the network and the team prefers not to operate a sidecar.

1. Create an Azure AI Document Intelligence resource (F0 for trial, S0 for production).
2. Copy the **Endpoint** and **API Key**.
3. In `host/src/ExtractHostModule.cs`, swap `ExtractVisionLlmOcrModule` (the current default) for `ExtractAzureDocumentIntelligenceModule`, and re-enable the matching `ProjectReference` in `host/src/Dignite.Vault.Extract.Host.csproj`. You can also drop the Vision-LLM vision `IChatClient` block in `ConfigureAI` — it fail-fasts on `Extract:VisionOcrModelId`.
4. Add to `host/src/appsettings.Development.json` (or `appsettings.Production.json`):

```json
"AzureDocumentIntelligence": {
  "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
  "ApiKey": "YOUR_KEY"
}
```

`ExtractAzureDocumentIntelligenceModule` binds this section automatically.

Dignite Vault Extract fixes the Azure model to `prebuilt-layout` and does not expose it as a config option — it emits the structured Markdown that Markdown-first requires. `prebuilt-read` (plain text only) and business prebuilts (invoice / contract) are intentionally not channel-layer OCR options.

> ⚠️ **F0 limitations** — each request only processes the **first 2 pages**, only one F0 resource per subscription per region, ~1–2 TPS throughput. Suitable only for demos and short documents (≤ 2 pages). Switch to S0 for sustained development or any larger document.

## See also

- [Text extraction overview](text-extraction.md) — Markdown-first contract, the two extraction paths, OCR provider comparison
- [Deployment](../deployment/deployment.md)
