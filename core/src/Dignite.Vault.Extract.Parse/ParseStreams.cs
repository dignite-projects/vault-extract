using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Shared stream helpers for <see cref="IMarkdownTextProvider"/> implementations. Lives in the
/// orchestrator project (referenced by every provider module) so the buffering logic is defined once
/// rather than copied per provider (PdfExtractor / PptxExtractor / future DOCX).
/// </summary>
public static class ParseStreams
{
    /// <summary>
    /// Materializes the whole stream into a byte array. The orchestrator (<c>DefaultTextExtractor</c>)
    /// already buffers the upload into a seekable <see cref="MemoryStream"/>, so a single
    /// <see cref="MemoryStream.ToArray"/> copy suffices on the production path; only a non-MemoryStream is
    /// re-buffered.
    /// </summary>
    public static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    /// <summary>
    /// Reads the stream into <paramref name="bytes"/>, aborting (returning <c>false</c>) the moment the
    /// running total exceeds <paramref name="maxBytes"/> — used to bound an untrusted, potentially
    /// ZIP-inflating embedded-image part so it can never be fully materialized (a decompression-bomb
    /// guard). Shared so every OpenXML container provider (PPTX now, DOCX in #308) reuses one hardened
    /// implementation rather than copying it.
    /// <para>
    /// Memory: the copy buffer chunk is pooled (no per-call allocation). On success the bytes are returned
    /// via <see cref="MemoryStream.ToArray"/>, so peak transient memory is up to ~2× the final size for a
    /// near-cap stream (the growing <see cref="MemoryStream"/> buffer plus the ToArray copy) — still
    /// bounded by the cap, never unbounded.
    /// </para>
    /// </summary>
    public static bool TryReadAllBytesBounded(Stream stream, long maxBytes, out byte[] bytes)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var buffer = new MemoryStream();
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    bytes = Array.Empty<byte>();
                    return false;
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
