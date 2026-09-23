using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

/// <summary>
/// Test classes that assign a process-wide static — a host-settable limit such as
/// <c>DocumentExportConsts.MaxExportDocumentCount</c> — and restore it in <c>finally</c>.
/// <para>
/// xUnit runs test classes in parallel, so while one of them holds a lowered value, every other class in the process
/// sees it: an export test in another class would be refused as over the cap. A collection with
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> runs on its own, after every parallel collection
/// has finished.
/// </para>
/// <para>
/// A collection definition is only seen by the assembly it lives in; the Application test assembly has its own copy.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideStaticsCollection
{
    public const string Name = "Mutates process-wide statics";
}
