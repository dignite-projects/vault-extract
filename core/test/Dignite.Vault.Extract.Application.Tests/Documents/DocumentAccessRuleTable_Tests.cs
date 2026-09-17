using System.Collections.Generic;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 2: the rule table, pinned field by field. Every row of the Issue's table appears here with its
/// three values, and the set of rules with <c>OwnerMayPerform</c> is asserted as a whole rather than row by row.
/// <para>
/// A pure data test with no container: the point is that the table in the Issue and the table in the code are the
/// same table. Changing a permission name, moving a family onto the wrong grant, or opening the ownership arm on
/// <see cref="DocumentAccessRule.Review"/> all red here, immediately and unambiguously, instead of surfacing as
/// one behavioural fact somewhere else that might be read as a test bug.
/// </para>
/// </summary>
public class DocumentAccessRuleTable_Tests
{
    public static TheoryData<string, DocumentAccessRule, string, string?, bool> Table => new()
    {
        {
            nameof(DocumentAccessRule.Read), DocumentAccessRule.Read,
            VaultExtractPermissions.Documents.ReadAll, VaultExtractResourcePermissions.Read, true
        },
        {
            nameof(DocumentAccessRule.Edit), DocumentAccessRule.Edit,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Edit, true
        },
        {
            nameof(DocumentAccessRule.Review), DocumentAccessRule.Review,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Edit, false
        },
        {
            nameof(DocumentAccessRule.Delete), DocumentAccessRule.Delete,
            VaultExtractPermissions.Documents.Delete, VaultExtractResourcePermissions.Delete, true
        },
        {
            nameof(DocumentAccessRule.Restore), DocumentAccessRule.Restore,
            VaultExtractPermissions.Documents.Restore, VaultExtractResourcePermissions.Delete, true
        },
        {
            nameof(DocumentAccessRule.Retry), DocumentAccessRule.Retry,
            VaultExtractPermissions.Documents.Pipelines.Retry, VaultExtractResourcePermissions.Edit, true
        },
        {
            nameof(DocumentAccessRule.DeclareType), DocumentAccessRule.DeclareType,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Upload, false
        },
        {
            nameof(DocumentAccessRule.Upload), DocumentAccessRule.Upload,
            VaultExtractPermissions.Documents.Upload, null, false
        },
        {
            nameof(DocumentAccessRule.PermanentDelete), DocumentAccessRule.PermanentDelete,
            VaultExtractPermissions.Documents.PermanentDelete, null, false
        },
        {
            nameof(DocumentAccessRule.ReprocessFieldExtraction), DocumentAccessRule.ReprocessFieldExtraction,
            VaultExtractPermissions.Documents.Reprocessing.FieldExtraction, null, false
        },
        {
            nameof(DocumentAccessRule.ReprocessReclassification), DocumentAccessRule.ReprocessReclassification,
            VaultExtractPermissions.Documents.Reprocessing.Reclassification, null, false
        },
        {
            nameof(DocumentAccessRule.Export), DocumentAccessRule.Export,
            VaultExtractPermissions.Documents.Export, null, false
        },
        {
            nameof(DocumentAccessRule.Statistics), DocumentAccessRule.Statistics,
            VaultExtractPermissions.Documents.ReadAll, null, false
        }
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_rule_carries_the_three_values_the_Issues_table_states(
        string name, DocumentAccessRule rule, string moduleWide, string? resource, bool ownerMayPerform)
    {
        rule.ModuleWidePermission.ShouldBe(moduleWide, $"{name}: module-wide permission");
        rule.ResourcePermission.ShouldBe(resource, $"{name}: per-type grant");
        rule.OwnerMayPerform.ShouldBe(ownerMayPerform, $"{name}: OwnerMayPerform");
    }

    /// <summary>
    /// The ownership arm is open for exactly five rules. Asserted as a set, not as five independent rows, so
    /// adding a sixth — most dangerously <see cref="DocumentAccessRule.Review"/>, whose two permission names are
    /// identical to <see cref="DocumentAccessRule.Edit"/>'s and which therefore differs <b>only</b> by this
    /// flag — reds here rather than silently widening what an uploader may do to their own document.
    /// </summary>
    [Fact]
    public void Ownership_is_open_for_exactly_Read_Edit_Delete_Restore_and_Retry()
    {
        var owned = new List<string>();
        foreach (var row in Table)
        {
            var name = (string)row[0]!;
            var rule = (DocumentAccessRule)row[1]!;
            if (rule.OwnerMayPerform)
            {
                owned.Add(name);
            }
        }

        owned.ShouldBe(
            [
                nameof(DocumentAccessRule.Read),
                nameof(DocumentAccessRule.Edit),
                nameof(DocumentAccessRule.Delete),
                nameof(DocumentAccessRule.Restore),
                nameof(DocumentAccessRule.Retry)
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// Review and Edit are the same two permission names and differ only in the ownership flag. Stated on its own
    /// because it is the whole reason ownership is a per-rule flag rather than one arm on the checker.
    /// </summary>
    [Fact]
    public void Review_differs_from_Edit_only_by_the_ownership_flag()
    {
        DocumentAccessRule.Review.ModuleWidePermission.ShouldBe(DocumentAccessRule.Edit.ModuleWidePermission);
        DocumentAccessRule.Review.ResourcePermission.ShouldBe(DocumentAccessRule.Edit.ResourcePermission);
        DocumentAccessRule.Review.OwnerMayPerform.ShouldBeFalse();
        DocumentAccessRule.Edit.OwnerMayPerform.ShouldBeTrue();
    }
}
