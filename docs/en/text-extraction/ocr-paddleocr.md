```json
//[doc-seo]
{
    "Description": "Use the PaddleOCR sidecar as Dignite Vault Extract's OCR provider: enabling the module, PP-StructureV3 and other models, configuration and resource footprint."
}
```

# PaddleOCR

> One of Dignite Vault Extract's OCR providers. Overview and comparison: [Text Extraction](text-extraction.md).

PaddleOCR runs as a local Docker sidecar. `PP-StructureV3` runs on CPU and emits native Markdown; data never leaves the network.

## Enabling It

The OCR providers are mutually exclusive, and the shipped host enables [Vision-LLM](ocr-vision-llm.md). To switch to PaddleOCR:

1. Reference `Dignite.Vault.Extract.Ocr.PaddleOcr` instead of the current provider (in this repository, swap the commented `<ProjectReference>` lines in `host/src/Dignite.Vault.Extract.Host.csproj`).
2. In the host module's `[DependsOn]`, replace `VaultExtractVisionLlmOcrModule` with `VaultExtractPaddleOcrModule`.
3. Remove the keyed vision `IChatClient` registration from `ConfigureAI`; it fails at startup without `Vault:Extract:VisionOcrModelId`. See [AI Provider](../configuration/ai-provider.md).
4. Start the sidecar and configure its endpoint (below).

## Configuration (`PaddleOcr`)

```json
"PaddleOcr": {
  "Endpoint": "http://localhost:8866",
  "ModelName": "PP-StructureV3",
  "Languages": [ "ja", "en" ]
}
```

| Key | Default | Description |
| --- | --- | --- |
| `Endpoint` | `http://localhost:8866` | PaddleOCR sidecar REST endpoint |
| `ModelName` | `PP-StructureV3` | One of: `PP-StructureV3` (CPU, native Markdown), `PP-OCRv4` (lightest, no Markdown structure), `PaddleOCR-VL-1.5` (highest quality; needs a GPU and a ~2 GB model download; native Markdown) |
| `Languages` | `["ja", "en"]` | Default recognition languages (BCP 47); a call's `OcrOptions.LanguageHints` overrides them |
| `TimeoutSeconds` | `600` | Timeout of the HTTP client that calls the sidecar |

## Running the Sidecar

```bash
cd host
docker compose up -d paddleocr
```

The first run downloads about 600 MB of model weights and takes 30–60 seconds; later starts are instant. The sidecar answers `{"status":"success"}` on `/ping` once ready.

**Resource footprint** (PP-StructureV3, CPU): about 3.7 s per page on a modern Intel CPU and about 2 GB of working memory.

> **Known limitation.** Phone-photographed or thermal-printer receipts (curved, low-contrast, borderless) are an OCR *capability* problem that `PP-StructureV3` fails on outright. Use the [Vision-LLM provider](ocr-vision-llm.md) for those inputs (#259).

## See Also

- [Text Extraction](text-extraction.md) — the Markdown-first contract, the two extraction paths, provider comparison
- [Deployment](../deployment/deployment.md) — running the sidecar in production
- [Local Development](../get-started/local-development.md) — the development sidecars
