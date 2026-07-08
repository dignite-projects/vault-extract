using System;
using System.Security.Cryptography;

namespace Dignite.Vault.Extract.Abstractions.Parse;

/// <summary>
/// Shared format for the #477 retained-figure Markdown reference (<c>figures/{hash}.{ext}</c>) and its content
/// hash — used by every image-bearing text-extraction provider (PDF, DOCX, PPTX) so the reference the provider
/// inlines into the Markdown, the <see cref="ExtractedFigure.ContentHash"/> it surfaces, and the blob key the
/// Application layer writes (<c>extraction-figures/{documentId}/{hash}</c>) all agree. Centralized here (a sibling
/// of <see cref="ImageOcrMarkup"/>) so a provider cannot drift the format.
/// </summary>
public static class FigureReference
{
    /// <summary>SHA-256 (lowercase hex) of the image bytes — the retained figure's content hash / dedup key.</summary>
    public static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>The document-relative Markdown reference for a retained figure (<c>figures/{hash}.{ext}</c>). The
    /// egress resolves the blob by hash, so the extension is cosmetic (renderer-friendly).</summary>
    public static string Build(string contentHash, string contentType)
        => "figures/" + contentHash + "." + Extension(contentType);

    /// <summary>Maps an image MIME type to a file extension. Any <c>image/*</c> subtype passes through
    /// (<c>jpeg</c> → <c>jpg</c>); a missing / non-image type falls back to <c>img</c>.</summary>
    private static string Extension(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType) ||
            !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "img";
        }

        var subtype = contentType["image/".Length..].Trim().ToLowerInvariant();
        return subtype switch
        {
            "" => "img",
            "jpeg" => "jpg",
            _ => subtype
        };
    }
}
