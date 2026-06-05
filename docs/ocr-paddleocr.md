# PaddleOCR — local sidecar

> One of Paperbase's OCR providers. Overview & comparison: [text-extraction.md](text-extraction.md). **This is the default.**

Default for development. `PP-StructureV3` runs on CPU and emits native Markdown out of the box. Data never leaves the network.

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
| `ModelName` | `PP-StructureV3` | One of: `PP-StructureV3` (CPU + native Markdown, default), `PP-OCRv4` (lightest, no Markdown structure), `PaddleOCR-VL-1.5` (highest quality; requires GPU + ~2 GB model download; native Markdown) |
| `Languages` | `["ja", "en"]` | Default recognition languages (BCP 47); overridden per call by `OcrOptions.LanguageHints` |

**Bring up the sidecar:**

```bash
docker compose up paddleocr
```

The first run downloads ~600 MB of model weights and takes 30–60 seconds. Subsequent starts are instant.

**Resource footprint** (PP-StructureV3, CPU): ~3.7 s/page on a modern Intel CPU, ~2 GB RAM working set.

> **Known limitation** — phone-photographed or thermal-printer receipts (curved, low-contrast, borderless) are an OCR *capability* problem that `PP-StructureV3` fails on outright. For those inputs use the [vision-LLM OCR provider](ocr-vision-llm.md) instead (#259).

## See also

- [Text extraction overview](text-extraction.md) — Markdown-first contract, the two extraction paths, OCR provider comparison
- [Deployment](deployment.md) — running the sidecar in production
- [Local development](local-development.md) — the dev sidecar loop
