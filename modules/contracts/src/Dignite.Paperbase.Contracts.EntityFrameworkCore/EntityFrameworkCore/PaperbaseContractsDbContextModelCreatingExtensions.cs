using Dignite.Paperbase.Contracts;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Paperbase.Contracts.EntityFrameworkCore;

public static class PaperbaseContractsDbContextModelCreatingExtensions
{
    public static void ConfigureContracts(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Contract>(b =>
        {
            b.ToTable(PaperbaseContractsDbProperties.DbTablePrefix + "Contracts", PaperbaseContractsDbProperties.DbSchema);
            b.ConfigureByConvention();

            b.HasIndex(x => x.DocumentId).IsUnique();
            b.HasIndex(x => x.ExpirationDate);
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.ReviewStatus);

            // L2 RelationDiscovery lookup index (硬伤一): joins through normalized form so
            // "HT-2024-001" / "HT2024001" / "ＨＴ－２０２４－００１" all match. Filtered to
            // non-null because most contracts have a contract number; null rows shouldn't
            // bloat the index.
            b.HasIndex(x => x.NormalizedContractNumber)
                .HasFilter("NormalizedContractNumber IS NOT NULL");

            b.Property(x => x.DocumentTypeCode).HasMaxLength(ContractConsts.MaxDocumentTypeCodeLength).IsRequired();
            b.Property(x => x.Title).HasMaxLength(ContractConsts.MaxTitleLength);
            b.Property(x => x.ContractNumber).HasMaxLength(ContractConsts.MaxContractNumberLength);
            b.Property(x => x.NormalizedContractNumber).HasMaxLength(ContractConsts.MaxContractNumberLength);
            b.Property(x => x.PartyAName).HasMaxLength(ContractConsts.MaxPartyNameLength);
            b.Property(x => x.PartyBName).HasMaxLength(ContractConsts.MaxPartyNameLength);
            b.Property(x => x.Currency).HasMaxLength(ContractConsts.MaxCurrencyLength);
            b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
            b.Property(x => x.GoverningLaw).HasMaxLength(ContractConsts.MaxGoverningLawLength);
            b.Property(x => x.Summary).HasMaxLength(ContractConsts.MaxSummaryLength);
        });
    }
}
