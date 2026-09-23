using Xunit;

namespace Dignite.Vault.Extract;

/// <summary>
/// Test classes that assign a process-wide static — a host-settable limit such as
/// <c>DocumentConsts.MaxUploadFileBytes</c> — and restore it in <c>finally</c>.
/// <para>
/// xUnit runs test classes in parallel, so while one of them holds a lowered value, every other class in the process
/// sees it. Lowering the upload limit to 4 bytes made upload tests in other classes fail now and then as
/// <c>FileTooLarge</c> on a 5-byte file. "This class runs serially" is true only within the class. A collection with
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> runs on its own, after every parallel collection
/// has finished.
/// </para>
/// <para>
/// A collection definition is only seen by the assembly it lives in; the EntityFrameworkCore test assembly has its
/// own copy.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideStaticsCollection
{
    public const string Name = "Mutates process-wide statics";
}
