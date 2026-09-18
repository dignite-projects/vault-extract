using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// One <b>asserting</b> call on the checker: <see cref="DocumentAccessChecker.CheckEntryAsync"/> (no rule, no
/// subject) or <see cref="DocumentAccessChecker.CheckAsync"/>. The non-throwing shapes a method uses to project
/// rights onto its response are deliberately not recorded — they decide nothing about whether the call runs.
/// </summary>
public sealed record RecordedAccessCheck(DocumentAccessRule? Rule, DocumentAccessSubject? Subject)
{
    public bool IsEntry => Rule is null;
}

/// <summary>Shared, per-application list of the asserting checks one test's calls made, in order.</summary>
public sealed class DocumentAccessCheckRecorder
{
    public List<RecordedAccessCheck> Checks { get; } = [];
}

/// <summary>
/// The real checker with a tap on its two asserting methods, so a fact can state <b>which</b> judgments an
/// operation makes and in what order — e.g. that <c>UploadAsync</c> is judged by one rule (#645) — rather than only
/// what the outcome was. A leftover second check that admits a superset of the first is invisible to every
/// outcome-based fact; it is not invisible here. It decides nothing itself: every call is passed through.
/// </summary>
[DisableConventionalRegistration]
public class RecordingDocumentAccessChecker : DocumentAccessChecker
{
    private readonly DocumentAccessCheckRecorder _recorder;

    public RecordingDocumentAccessChecker(
        ICurrentUser currentUser,
        DocumentAccessMemo accessMemo,
        DocumentAccessCheckRecorder recorder)
        : base(currentUser, accessMemo)
    {
        _recorder = recorder;
    }

    public override Task CheckEntryAsync()
    {
        _recorder.Checks.Add(new RecordedAccessCheck(null, null));
        return base.CheckEntryAsync();
    }

    public override Task CheckAsync(DocumentAccessRule rule, DocumentAccessSubject subject)
    {
        _recorder.Checks.Add(new RecordedAccessCheck(rule, subject));
        return base.CheckAsync(rule, subject);
    }
}

public static class RecordingDocumentAccessCheckerRegistration
{
    public static void UseRecordingAccessChecker(this IServiceCollection services)
    {
        services.AddSingleton<DocumentAccessCheckRecorder>();
        services.Replace(ServiceDescriptor.Transient<DocumentAccessChecker, RecordingDocumentAccessChecker>());
    }
}
