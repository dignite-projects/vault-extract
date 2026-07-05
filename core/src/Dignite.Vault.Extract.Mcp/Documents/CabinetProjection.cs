using Dignite.Vault.Extract.Ai;

namespace Dignite.Vault.Extract.Mcp.Documents;

internal static class CabinetProjection
{
    public static CabinetSchema Project(CabinetDto cabinet)
    {
        return new CabinetSchema
        {
            Id = cabinet.Id,
            Uri = CabinetResourceUri.Format(cabinet.Id),
            Name = PromptBoundary.WrapField(cabinet.Name)!,
            Description = PromptBoundary.WrapField(cabinet.Description)
        };
    }
}
