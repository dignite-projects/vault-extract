using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UtfUnknown;

namespace Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;

/// <summary>
/// Normalizes raw byte-stream text uploads to UTF-8 before ElBruno converts them (#493).
/// <para>
/// ElBruno's <c>CsvConverter</c> and <c>PlainTextConverter</c> read through
/// <c>new StreamReader(stream, null, detectEncodingFromByteOrderMarks: true, ...)</c>, which is UTF-8 plus
/// BOM sniffing and nothing else. Excel on Japanese Windows exports CSV as CP932 with no BOM, so every
/// double-byte character decoded to U+FFFD and the garbage landed in the write-once
/// <c>Document.Markdown</c>.
/// </para>
/// <para>
/// Only formats whose bytes carry no in-band encoding declaration need this. OpenXML (.docx / .pptx /
/// .xlsx) declares its encoding in the package XML, and .pdf carries per-font encodings, so both are
/// excluded.
/// </para>
/// </summary>
internal static class TextEncodingNormalizer
{
    /// <summary>
    /// Codepage used when detection cannot reach <see cref="MinDetectionConfidence"/>. CP932 (Shift-JIS):
    /// this product's documents are Japanese-first, and it is what Excel on Japanese Windows writes.
    /// Deliberately a <c>const</c> rather than host configuration — it only fires when detection is
    /// inconclusive, and the detector already recognizes GBK / Big5 / EUC-JP on its own, so a host serving
    /// another locale is served by detection, not by this fallback.
    /// </summary>
    private const int FallbackCodePage = 932;

    /// <summary>
    /// Below this confidence the detector's guess is not worth taking over <see cref="FallbackCodePage"/>.
    /// Short files (a two-line CSV) are exactly where universalchardet is least sure.
    /// </summary>
    private const float MinDetectionConfidence = 0.5f;

    private static readonly HashSet<string> ByteStreamTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt" };

    static TextEncodingNormalizer()
    {
        // .NET registers only the Unicode encodings by default; CP932 and the other legacy codepages need
        // this provider before Encoding.GetEncoding can resolve them. Registered from a static constructor
        // rather than the ABP module so that a directly-constructed provider still decodes correctly,
        // independent of module initialization order.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool AppliesTo(string? fileExtension)
        => fileExtension is not null && ByteStreamTextExtensions.Contains(fileExtension);

    /// <summary>
    /// Reads <paramref name="source"/> and returns a fresh UTF-8 stream for ElBruno. Bytes that carry a
    /// Unicode BOM, or that decode as strict UTF-8, are handed back verbatim. Anything else is decoded
    /// through a detected — or, failing that, the fallback — legacy codepage and re-encoded as UTF-8 with a
    /// BOM, so ElBruno's own BOM sniffing reads it back correctly. The caller owns the returned stream.
    /// </summary>
    public static async Task<Stream> ToUtf8Async(
        Stream source,
        string? fileExtension,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bytes = await ReadAllBytesAsync(source, cancellationToken);
        if (bytes.Length == 0 || HasUnicodeBom(bytes) || IsStrictUtf8(bytes))
        {
            return new MemoryStream(bytes, writable: false);
        }

        var encoding = Detect(bytes, logger);
        if (encoding is null)
        {
            encoding = Encoding.GetEncoding(FallbackCodePage);
            logger.LogWarning(
                "Could not determine the text encoding of a '{Extension}' upload; decoding it as {Fallback}. " +
                "Characters outside that codepage will be lost.",
                fileExtension, encoding.WebName);
        }

        var text = encoding.GetString(bytes);

        var normalized = new MemoryStream();
        await using (var writer = new StreamWriter(
            normalized,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            leaveOpen: true))
        {
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
        }

        normalized.Position = 0;
        return normalized;
    }

    private static Encoding? Detect(byte[] bytes, ILogger logger)
    {
        var detected = CharsetDetector.DetectFromBytes(bytes).Detected;
        if (detected?.Encoding is null || detected.Confidence < MinDetectionConfidence)
        {
            return null;
        }

        logger.LogDebug(
            "Detected legacy text encoding {Encoding} (confidence {Confidence:0.00}); transcoding to UTF-8.",
            detected.EncodingName, detected.Confidence);
        return detected.Encoding;
    }

    /// <summary>
    /// A deterministic UTF-8 test: valid multi-byte UTF-8 sequences do not occur by accident in Shift-JIS or
    /// the other double-byte codepages, so a clean strict decode means the bytes really are UTF-8 (or ASCII).
    /// </summary>
    private static bool IsStrictUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Checked before the UTF-8 test: UTF-16 / UTF-32 bytes would fail a strict UTF-8 decode.</summary>
    private static bool HasUnicodeBom(byte[] b)
        => (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ||
           (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) ||
           (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) ||
           (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) ||
           (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF);

    private static async Task<byte[]> ReadAllBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
