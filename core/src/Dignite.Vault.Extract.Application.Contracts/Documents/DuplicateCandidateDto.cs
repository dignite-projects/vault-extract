using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// A duplicate-detection candidate surfaced to the operator (#411): another document suspected to be the same
/// business entity (same layer + type + field fingerprint). Carries enough to recognize and open it — title (with
/// <see cref="FileName"/> fallback when the title is empty) + upload time. Recomputed on read and hard-capped.
/// </summary>
public class DuplicateCandidateDto
{
    public Guid Id { get; set; }

    public string? Title { get; set; }

    public string? FileName { get; set; }

    public DateTime CreationTime { get; set; }
}
