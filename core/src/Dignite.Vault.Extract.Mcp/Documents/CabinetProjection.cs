using System;
using Dignite.Vault.Extract.Ai;

namespace Dignite.Vault.Extract.Mcp.Documents;

internal static class CabinetProjection
{
    public static CabinetSchema Project(CabinetDto cabinet, Guid? tenantId = null)
    {
        return new CabinetSchema
        {
            Id = cabinet.Id,
            Uri = CabinetResourceUri.Format(cabinet.Id, tenantId),
            Name = PromptBoundary.WrapField(cabinet.Name)!,
            Description = PromptBoundary.WrapField(cabinet.Description)
        };
    }
}
