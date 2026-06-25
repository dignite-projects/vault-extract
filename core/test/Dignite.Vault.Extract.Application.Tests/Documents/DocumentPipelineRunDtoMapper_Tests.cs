using System.Collections.Generic;
using System.Text.Json;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Volo.Abp.Data;
using Xunit;

namespace Dignite.Vault.Extract.Application.Tests.Documents;

/// <summary>
/// Verifies that <see cref="Dignite.Vault.Extract.DocumentPipelineRunToDocumentPipelineRunDtoMapper"/>
/// correctly projects both shapes of <c>ExtraProperties["Candidates"]</c> into strongly typed
/// <see cref="DocumentPipelineRunDto.Candidates"/>：
///   (a) the original written <see cref="PipelineRunCandidate"/> list before persistence round-trip in the same UoW;
///   (b) <see cref="JsonElement"/> when read back through EF Core / ABP persistence.
///
/// This mapping is the core guarantee that Angular / .NET HttpApi.Client receive strongly typed candidates.
/// If the ExtraProperties-to-Candidates wrapper is removed from the mapper, the frontend falls back to
/// string-key casts and drift risk returns immediately.
/// </summary>
public class DocumentPipelineRunDtoMapper_Tests
{
    private readonly DocumentPipelineRunToDocumentPipelineRunDtoMapper _mapper = new();

    [Fact]
    public void Maps_Candidates_From_Raw_List_In_ExtraProperties()
    {
        var source = CreateClassificationRun();
        source.SetProperty(
            PipelineRunExtraPropertyNames.ClassificationCandidates,
            new List<PipelineRunCandidate>
            {
                new("contract.general", 0.64),
                new("invoice.standard", 0.31)
            });

        var dto = _mapper.Map(source);

        dto.Candidates.ShouldNotBeNull();
        dto.Candidates.Count.ShouldBe(2);
        dto.Candidates[0].TypeCode.ShouldBe("contract.general");
        dto.Candidates[0].ConfidenceScore.ShouldBe(0.64);
        dto.Candidates[1].TypeCode.ShouldBe("invoice.standard");
        dto.Candidates[1].ConfidenceScore.ShouldBe(0.31);
    }

    [Fact]
    public void Maps_Candidates_From_JsonElement_As_Persisted_Reads_Return()
    {
        var source = CreateClassificationRun();
        var json = JsonSerializer.SerializeToElement(new[]
        {
            new PipelineRunCandidate("contract.general", 0.64),
            new PipelineRunCandidate("invoice.standard", 0.31)
        });
        source.SetProperty(PipelineRunExtraPropertyNames.ClassificationCandidates, json);

        var dto = _mapper.Map(source);

        dto.Candidates.ShouldNotBeNull();
        dto.Candidates.Count.ShouldBe(2);
        dto.Candidates[0].TypeCode.ShouldBe("contract.general");
        dto.Candidates[0].ConfidenceScore.ShouldBe(0.64);
    }

    [Fact]
    public void Candidates_Is_Null_When_ExtraProperties_Has_No_Key()
    {
        var source = CreateClassificationRun();

        var dto = _mapper.Map(source);

        dto.Candidates.ShouldBeNull();
    }

    [Fact]
    public void Candidates_Is_Null_When_Stored_Value_Is_Not_An_Array()
    {
        var source = CreateClassificationRun();
        var notAnArray = JsonSerializer.SerializeToElement(new { unexpected = "shape" });
        source.SetProperty(PipelineRunExtraPropertyNames.ClassificationCandidates, notAnArray);

        var dto = _mapper.Map(source);

        dto.Candidates.ShouldBeNull();
    }

    /// <summary>
    /// Simulates the HttpApi.Client deserialization path in .NET clients:
    /// server STJ serializes DTO -> JSON -> client STJ deserializes back to DTO -> Candidates remain strongly typed.
    /// Candidates must be <c>{ get; set; }</c> so STJ can set it directly; if changed to a get-only computed
    /// property, this test fails immediately; see the review counterexample.
    /// </summary>
    [Fact]
    public void Candidates_Survives_StjRoundtrip_For_DotNetHttpApiClient()
    {
        var source = CreateClassificationRun();
        source.SetProperty(
            PipelineRunExtraPropertyNames.ClassificationCandidates,
            new List<PipelineRunCandidate> { new("contract.general", 0.64) });
        var serverDto = _mapper.Map(source);

        var json = JsonSerializer.Serialize(serverDto);
        var roundtripped = JsonSerializer.Deserialize<DocumentPipelineRunDto>(json);

        roundtripped.ShouldNotBeNull();
        roundtripped.Candidates.ShouldNotBeNull();
        roundtripped.Candidates.Count.ShouldBe(1);
        roundtripped.Candidates[0].TypeCode.ShouldBe("contract.general");
        roundtripped.Candidates[0].ConfidenceScore.ShouldBe(0.64);
    }

    // Explicit subclass accesses the protected parameterless constructor, avoiding reflection tricks.
    // This deliberately does not call ABP's factory path because PipelineRunManager.QueueAsync requires
    // full DI + UoW. This test covers only mapper field deserialization and is unrelated to aggregate-root
    // factory semantics.
    private static DocumentPipelineRun CreateClassificationRun() => new TestDocumentPipelineRun();

    private sealed class TestDocumentPipelineRun : DocumentPipelineRun;
}
