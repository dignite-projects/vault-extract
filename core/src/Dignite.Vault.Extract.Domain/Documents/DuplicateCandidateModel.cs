using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Read-model for a duplicate-detection candidate (#411): a small Domain projection (not an entity) describing
/// another document in the same layer + type that shares this document's <see cref="Document.FieldFingerprint"/>,
/// returned by <see cref="IDocumentRepository.FindDuplicateCandidatesAsync"/>. Carries just enough for the operator
/// to recognize and open the candidate — title (with file-name fallback) + upload time — without materializing the
/// full Document row (notably <c>Markdown</c>). The Application layer maps it to <c>DuplicateCandidateDto</c>.
/// </summary>
public class DuplicateCandidateModel
{
    public Guid Id { get; set; }

    /// <summary>Display title (derived from Markdown); may be null for historical / not-yet-titled documents.</summary>
    public string? Title { get; set; }

    /// <summary>Original uploaded file name (from <c>FileOrigin</c>); null for derived sub-documents with no source blob.</summary>
    public string? FileName { get; set; }

    public DateTime CreationTime { get; set; }
}
