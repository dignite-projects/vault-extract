# Vision-LLM OCR — photos, receipts, image PDFs

> One of Dignite Vault Extract's OCR providers. Overview & comparison: [text-extraction.md](text-extraction.md). **Opt-in** (the default is [PaddleOCR](ocr-paddleocr.md)).

## When to use it

Phone-photographed and thermal-printer receipts (curved, low-contrast, borderless, two-column) are an OCR **capability** problem — layout OCR (`PP-StructureV3` on CPU) fails on them outright, and no downstream prompt can recover garbage input. A vision-capable LLM reads them reliably.

A/B on one Japanese drugstore thermal receipt (#259): `PP-StructureV3 @ CPU` recovered almost nothing; **Qwen3-VL-8B (cloud, chat mode)** returned all 10 line items + every amount + the discount notes + the total — structure and numbers 100% correct — in ~6 s.

This provider is **vendor-agnostic**: it consumes a multimodal `IChatClient` (`Microsoft.Extensions.AI`), so any vision model on any OpenAI-compatible (or other `IChatClient`) provider works. The host points it at a vision model; Dignite Vault Extract Core hardcodes none.

## What it handles

| Input | Behaviour |
|---|---|
| Images (`jpg/png/gif/bmp/webp/tiff`) | One vision-LLM call → Markdown transcription |
| Scanned / image-only PDFs (no text layer) | Each page rasterized to PNG (PDFium) → one vision-LLM call per page → joined |
| Digital PDFs **with** a text layer | Never reach OCR — the [MarkItDown digital path](text-extraction.md) handles them |
| Anything else | Fail-open: empty Markdown + a logged warning (document still persists → review queue) |

Output is Markdown, exactly like the other OCR providers — the Markdown-first channel contract is preserved. There is no out-of-band spatial payload (a chat LLM has no bbox/cell model), so `OcrResult` native-payload fields stay null.

## Enabling it

`IOcrProvider` has a single registration, so VisionLlm and PaddleOCR/Azure are **mutually exclusive** — enabling VisionLlm means disabling the current provider.

**1. Reference the project** — uncomment in `host/src/Dignite.Vault.Extract.Host.csproj`:

```xml
<ProjectReference Include="..\..\core\src\Dignite.Vault.Extract.Ocr.VisionLlm\Dignite.Vault.Extract.Ocr.VisionLlm.csproj" />
```

**2. Swap the module** — in `host/src/ExtractHostModule.cs` `[DependsOn(...)]`, comment out `ExtractPaddleOcrModule` and enable:

```csharp
typeof(ExtractVisionLlmOcrModule),
```

**3. Register the keyed vision `IChatClient`** — in `ExtractHostModule.ConfigureAI`, after the existing keyed clients, reusing the same `OpenAIClient` (same endpoint/key) but a **vision-capable** model id:

```csharp
// The vision model CANNOT fall back to ChatModelId — the main chat model (e.g. DeepSeek-V3)
// may not be vision-capable. Require an explicit id and fail fast if missing.
var visionModelId = configuration["Vault:Extract:VisionOcrModelId"];
if (string.IsNullOrWhiteSpace(visionModelId))
{
    throw new AbpException(
        "VisionLlm OCR is enabled but Vault:Extract:VisionOcrModelId is not set.");
}
context.Services.AddKeyedChatClient(
    VisionLlmOcrConsts.VisionChatClientKey,
    _ => openAIClient.GetChatClient(visionModelId).AsIChatClient())
    .UseOpenTelemetry()
    .UseLogging();
```

**4. Configure the model** — in `appsettings.Development.json` / env vars (reuses the existing `Vault:Extract:Endpoint` + `ApiKey`; Qwen3-VL and the chat model can share one SiliconFlow endpoint/key):

```json
"Vault": {
  "Extract": {
    "VisionOcrModelId": "Qwen/Qwen3-VL-8B-Instruct"
  }
}
```

> Use your provider's exact model id. **Recommended v1 default: the 8B tier** — the validated 6 s / structurally-100%-correct result, cheapest of the vision tiers (occasional katakana glyph errors). Bump to a larger tier (e.g. 32B) when character precision matters more than latency/cost.

## Configuration (`VisionLlmOcr`)

Provider behaviour knobs (the model id / endpoint / key are host `ConfigureAI` concerns, above):

```json
"VisionLlmOcr": {
  "MaxOutputTokens": 4096,
  "MaxPdfPages": 30,
  "MaxImagePixels": 1500000
}
```

| Key | Default | Description |
| --- | --- | --- |
| `MaxOutputTokens` | `4096` | Hard cap on tokens generated per image/page. Bounds worst-case cost **and** the length of a runaway loop |
| `Temperature` | `0` | Deterministic transcription; reduces hallucination/looping |
| `MaxPdfPages` | `30` | Max pages rasterized per PDF. Exceeding it throws (fails loudly) rather than silently dropping pages |
| `MaxImagePixels` | `1500000` | Most pixels (width × height) of an image sent to the model; a larger one is [scaled down](#large-images-are-shrunk-first) first. `0` or less sends images as they are |
| `MaxConsecutiveRepeatedLines` | `24` | Guard heuristic 1: trip on this many identical consecutive lines. Values below 2 are treated as 2 (strictest); set a very large value to disable |
| `MinDistinctLineRatio` | `0.3` | Guard heuristic 2: trip if distinct/total line ratio drops below this over a large body |
| `MinLinesForRatioCheck` | `40` | Guard heuristic 2: minimum lines before the distinct-ratio check applies |
| `MinLengthForSegmentCheck` | `200` | Guard heuristic 3: minimum single-line length before the short-period check inspects it |
| `MaxRepeatedSegmentLength` | `120` | Guard heuristic 3: largest repeating-unit (period) length treated as a loop |
| `MinRepeatedSegmentRepeats` | `8` | Guard heuristic 3: minimum times the unit must tile a line to trip |
| `MaxNoContentRefusalLength` | `160` | No-content-refusal guard: only a response no longer than this is checked against refusal phrasings, so a genuine (longer) transcription is never mistaken for one |

## Large images are shrunk first

A vision model reads an image as a number of tokens that grows with its pixel count, and past a point it stops transcribing and writes the same empty table rows until it hits `MaxOutputTokens`. The [repetition guard](#hallucination--repetition-loop-guard) then (correctly) discards the page, and the document is left with no text, no type and no fields ([#692](https://github.com/dignite-projects/vault-extract/issues/692)). In the case that surfaced it, a 4.6-megapixel phone photo of a payslip looped at `temperature` 0; a 2.2-megapixel copy still looped, even at temperature 0.7 with a presence penalty; a 1.25-megapixel copy read correctly in 10–16 s. The cause is the pixel count, not the sampling settings.

So the provider scales an image down before the call when it is over `MaxImagePixels` (default 1.5 megapixels):

- **Within the budget** the original bytes are sent untouched, with no re-encoding.
- **Over the budget** the image is scaled to at most that many pixels, keeping its aspect ratio. A PNG (a screenshot, a rasterized PDF page) stays a PNG so the text stays sharp; anything else becomes a JPEG (quality 90).
- The EXIF orientation is applied, because the smaller copy carries no EXIF and a photo taken sideways would otherwise reach the model on its side.
- An image SkiaSharp cannot read (HEIC, corrupt data), or any failure while shrinking, **sends the original** and logs a warning; shrinking never fails the OCR call.
- Only the copy sent to the model is smaller; the stored original is untouched. One information line logs each shrink (original and new size).

This applies to uploaded images, to the pages of a scanned PDF (PDFtoImage renders at 300 dpi, an A4 page is about 8.7 megapixels), and to images embedded in a PDF that the Markdown providers send here.

The budget was measured on one image and one model, so treat 1.5 megapixels as usable rather than generous: a phone photo of a bank passbook and card statement at that size took 94 s. Lower it if your model loops on dense forms.

## Hallucination / repetition-loop guard

A vision LLM driven in chat mode can fall into a repetition loop that fills the token budget with the same line over and over (the PaddleOCR-VL chat-mode death loop in #259). As a *trusted digitization channel*, Dignite Vault Extract must never persist such output. Defences, layered:

- **`MaxOutputTokens`** caps generation length (a loop can't run unbounded).
- **`Temperature = 0`** + an explicit "transcribe each piece of text exactly once, never repeat a line" instruction.
- **Post-response repetition guard** ([`VisionLlmOutputGuard`](../core/src/Dignite.Vault.Extract.Ocr.VisionLlm/VisionLlmOutputGuard.cs)), three heuristics with conservative thresholds that never flag a legitimate receipt/table: (1) too many identical consecutive lines; (2) low distinct-line ratio over a large body (interleaved loops); (3) a single content-heavy line that is a short unit repeated many times — catching the no-newline char-/phrase-level loop (e.g. one giant line of `ありがとう…`) the line heuristics miss. Punctuation-only lines (Markdown table separators, horizontal rules) are excluded from heuristic 3 so wide tables are not flagged.
- On a guard trip the output is **discarded** (empty Markdown + warning) — never persisted. For PDFs the guard runs **per page**, so one looping page is dropped without failing the whole document.
- A PDF page that **fails to rasterize** (corrupt PDFium page object) is likewise skipped with a warning, preserving the pages already transcribed. A failure in the per-page LLM call is *not* swallowed — it is almost always systemic (auth / network / quota) and propagates to fail the run loudly (visible, retryable), distinct from "the model produced garbage".
- **Incompleteness is surfaced, not silent (#268).** Whenever output is truncated at the token cap, discarded by the guard, or a PDF page is dropped, the result is flagged incomplete and the REST `DocumentDto` exposes `ExtractionIsComplete = false` + `ExtractionIncompleteReason`. Downstream consumers decide whether to accept / downgrade / route to review — the channel does not gate Ready on it. See [text-extraction.md](text-extraction.md) and [#268](https://github.com/dignite-projects/vault-extract/issues/268).

## No-content-refusal guard

The system prompt tells the model: "If the image contains no readable text, output nothing at all" — but a chat vision LLM sometimes explains itself instead of complying, returning a short English sentence such as *"There is no readable text in the image to transcribe."* for a purely decorative image (a letterhead banner, a logo, a stamp with no legible glyphs). Left as-is, that sentence would be persisted as if it were the image's real transcribed content.

[`VisionLlmOutputGuard.LooksLikeNoContentRefusal`](../core/src/Dignite.Vault.Extract.Ocr.VisionLlm/VisionLlmOutputGuard.cs) recognizes common refusal phrasings ("there is no ... text", "I cannot find any text", "nothing to transcribe", …) in a response no longer than `MaxNoContentRefusalLength` characters, and normalizes a match to the same outcome as a genuinely empty response (`IsComplete = true`, empty Markdown) — this is a phrasing normalization, not a discard of possibly-real content, so it is reported complete rather than tripping the `#268` incompleteness signal the way the repetition-loop guard does.

## Cost

This shifts OCR from **free local CPU** (PaddleOCR) to a **paid, per-token cloud LLM call**. Host operators must budget for it:

- **Per image** ≈ one call (input image tokens + output transcription tokens, capped by `MaxOutputTokens`).
- **Per PDF** ≈ **one call per page** — cost scales linearly with page count. `MaxPdfPages` bounds it.
- Token usage is emitted on the existing OpenTelemetry pipeline (`gen_ai.client.token.usage`, via `.UseOpenTelemetry()` on the keyed client) — monitor spend there. See [observability.md](../deployment/observability.md).

## Linux deployment

PDF rasterization uses [PDFtoImage](https://github.com/sungaila/PDFtoImage) (PDFium + SkiaSharp). Windows works out of the box. **Linux** containers additionally need:

- the `SkiaSharp.NativeAssets.Linux` package referenced in the host project, and
- the `libfontconfig1` system library installed in the image (e.g. `apt-get install -y libfontconfig1`).

(Image-only inputs need no fonts, but vector content in a PDF page does. Shrinking an over-sized image (above) uses SkiaSharp too, but only to decode and encode pixels, so it needs no fonts either.)

## Limitations (v1)

- **No language detection** — `DetectedLanguage` is left null (not this provider's job).
- **No input-type routing** — VisionLlm fully handles its input domain (images + scanned PDFs); there is no composite that sends images to VisionLlm and scanned PDFs to PaddleOCR. If you need both engines at once, that's a future enhancement — open an issue.
- **No retry on a loop** — when the repetition guard trips, the page is discarded; the provider does not retry at a smaller scale. Large images are shrunk up front (see above) so this is rare.

## See also

- [Text extraction overview](text-extraction.md) — Markdown-first contract, the two extraction paths, OCR provider comparison
- [AI provider](../configuration/ai-provider.md) — how the keyed `IChatClient` instances are wired
- [Observability](../deployment/observability.md) — the `gen_ai.*` token metrics
