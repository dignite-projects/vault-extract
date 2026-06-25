using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using NSubstitute;
using Volo.Abp;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Shared by Application.Tests: an NSubstitute fake factory for
/// <see cref="IDocumentPipelineRunRepository"/> with a closure-state list. Manager.QueueAsync/Begin/
/// Complete paths need to query existing runs to calculate AttemptNumber and derive LifecycleStatus.
/// A simple <c>Substitute.For&lt;...&gt;()</c> returns null by default, making DeriveLifecycle always take
/// the Processing branch and hiding status-transition bugs.
/// <para>
/// Each test class calls <see cref="Create"/> once to get an independent fake instance, registered as a
/// singleton in that class's test module. All [Fact]s in the same class share one list; query isolation is
/// provided by using a fresh doc.Id per Fact.
/// </para>
/// </summary>
public static class PipelineRunRepositoryFake
{
    public static IDocumentPipelineRunRepository Create()
    {
        var runs = new List<DocumentPipelineRun>();
        var mock = Substitute.For<IDocumentPipelineRunRepository>();

        mock.InsertAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.Arg<DocumentPipelineRun>();
                runs.Add(run);
                return Task.FromResult(run);
            });

        // InsertNewAttemptAsync simulates the (DocumentId, PipelineCode, AttemptNumber) unique index:
        // colliding keys throw RetryInProgress, matching EfCoreDocumentPipelineRunRepository after it
        // translates DbUpdateException; otherwise this is equivalent to ordinary InsertAsync into the
        // list. Happy-path tests use a fresh doc.Id per Fact, so normal state does not collide.
        mock.InsertNewAttemptAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.Arg<DocumentPipelineRun>();
                var collides = runs.Any(r =>
                    r.DocumentId == run.DocumentId
                    && r.PipelineCode == run.PipelineCode
                    && r.AttemptNumber == run.AttemptNumber);
                if (collides)
                {
                    throw new BusinessException(ExtractErrorCodes.Pipeline.RetryInProgress)
                        .WithData("PipelineCode", run.PipelineCode)
                        .WithData("DocumentId", run.DocumentId);
                }
                runs.Add(run);
                return Task.CompletedTask;
            });

        mock.UpdateAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<DocumentPipelineRun>()));

        mock.FindLatestByDocumentAndCodeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var code = call.Arg<string>();
                var match = runs
                    .Where(r => r.DocumentId == docId && r.PipelineCode == code)
                    .OrderByDescending(r => r.AttemptNumber)
                    .FirstOrDefault();
                return Task.FromResult<DocumentPipelineRun?>(match);
            });

        mock.GetLatestRunsByCodesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var codes = call.Arg<IReadOnlyCollection<string>>();
                var dict = runs
                    .Where(r => r.DocumentId == docId && codes.Contains(r.PipelineCode))
                    .GroupBy(r => r.PipelineCode)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).First());
                return Task.FromResult(dict);
            });

        mock.GetListByDocumentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var list = runs
                    .Where(r => r.DocumentId == docId)
                    .OrderBy(r => r.PipelineCode)
                    .ThenBy(r => r.AttemptNumber)
                    .ToList();
                return Task.FromResult(list);
            });

        mock.FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var runId = call.Arg<Guid>();
                var match = runs.FirstOrDefault(r => r.Id == runId);
                return Task.FromResult<DocumentPipelineRun?>(match);
            });

        return mock;
    }
}
