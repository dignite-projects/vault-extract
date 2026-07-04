using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;

/// <summary>
/// Fail-closed resource limits for untrusted XLSX packages (#471). The upload byte limit constrains only
/// the compressed ZIP; this preflight also bounds actual expanded bytes and worksheet XML cardinality
/// before ClosedXML materializes the workbook.
/// </summary>
internal sealed record XlsxSafetyLimits(
    long MaxCompressedBytes,
    long MaxExpandedArchiveBytes,
    int MaxArchiveEntries,
    int MaxWorksheets,
    long MaxCells,
    int MaxMarkdownCharacters)
{
    public static XlsxSafetyLimits Default { get; } = new(
        MaxCompressedBytes: 20L * 1024 * 1024,
        MaxExpandedArchiveBytes: 64L * 1024 * 1024,
        MaxArchiveEntries: 2_048,
        MaxWorksheets: 128,
        MaxCells: 500_000,
        MaxMarkdownCharacters: 2_000_000);
}

internal static class XlsxSafetyGuard
{
    public static async Task ValidatePackageAsync(
        Stream stream,
        XlsxSafetyLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= XlsxSafetyLimits.Default;

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDataException("XLSX safety validation requires a readable, seekable stream.");
        }

        var initialPosition = stream.Position;
        try
        {
            if (stream.Length - initialPosition > limits.MaxCompressedBytes)
            {
                throw new InvalidDataException(
                    $"XLSX compressed size exceeds the {limits.MaxCompressedBytes}-byte extraction limit.");
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > limits.MaxArchiveEntries)
            {
                throw new InvalidDataException(
                    $"XLSX contains {archive.Entries.Count} ZIP entries; the limit is {limits.MaxArchiveEntries}.");
            }

            var expandedBudget = new ExpandedByteBudget(limits.MaxExpandedArchiveBytes);
            var worksheetCount = 0;
            long cellCount = 0;
            var drainBuffer = new byte[81_920];

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var guardedEntry = new BudgetedReadStream(entry.Open(), expandedBudget);
                if (!IsWorksheetXml(entry.FullName))
                {
                    await DrainAsync(guardedEntry, drainBuffer, cancellationToken);
                    continue;
                }

                worksheetCount++;
                if (worksheetCount > limits.MaxWorksheets)
                {
                    throw new InvalidDataException(
                        $"XLSX contains more than {limits.MaxWorksheets} worksheet parts.");
                }

                var settings = new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = limits.MaxExpandedArchiveBytes
                };

                using var reader = XmlReader.Create(guardedEntry, settings);
                while (await reader.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
                    {
                        continue;
                    }

                    cellCount++;
                    if (cellCount > limits.MaxCells)
                    {
                        throw new InvalidDataException(
                            $"XLSX contains more than {limits.MaxCells} cells.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or XmlException or NotSupportedException)
        {
            throw new InvalidDataException("XLSX package failed safety validation.", ex);
        }
        finally
        {
            stream.Position = initialPosition;
        }
    }

    public static void ValidateMarkdown(string markdown, XlsxSafetyLimits? limits = null)
    {
        limits ??= XlsxSafetyLimits.Default;
        if (markdown.Length > limits.MaxMarkdownCharacters)
        {
            throw new InvalidDataException(
                $"XLSX Markdown output contains {markdown.Length} characters; " +
                $"the limit is {limits.MaxMarkdownCharacters}.");
        }
    }

    private static bool IsWorksheetXml(string entryName)
        => entryName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
           entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static async Task DrainAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken) > 0)
        {
            // Reading through BudgetedReadStream accounts for actual expanded bytes.
        }
    }

    private sealed class ExpandedByteBudget
    {
        private readonly long _limit;
        private long _consumed;

        public ExpandedByteBudget(long limit)
        {
            _limit = limit;
        }

        public void Consume(int count)
        {
            _consumed = checked(_consumed + count);
            if (_consumed > _limit)
            {
                throw new InvalidDataException(
                    $"XLSX expanded content exceeds the {_limit}-byte extraction limit.");
            }
        }
    }

    private sealed class BudgetedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly ExpandedByteBudget _budget;

        public BudgetedReadStream(Stream inner, ExpandedByteBudget budget)
        {
            _inner = inner;
            _budget = budget;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _budget.Consume(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            _budget.Consume(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            _budget.Consume(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            _budget.Consume(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
