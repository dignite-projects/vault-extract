using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #635 decision 2: the rule table, pinned field by field. Every row of the Issue's table appears here with its
/// three values, and the set of rules per owner-arm value is asserted as a whole rather than row by row.
/// <para>
/// A pure data test with no container: the point is that the table in the Issue and the table in the code are the
/// same table. Changing a permission name, moving a family onto the wrong grant, or opening the ownership arm on
/// <see cref="DocumentAccessRule.Review"/> all red here, immediately and unambiguously, instead of surfacing as
/// one behavioural fact somewhere else that might be read as a test bug.
/// </para>
/// </summary>
public class DocumentAccessRuleTable_Tests
{
    public static TheoryData<string, DocumentAccessRule, string, string?, DocumentOwnerArm> Table => new()
    {
        {
            nameof(DocumentAccessRule.Read), DocumentAccessRule.Read,
            VaultExtractPermissions.Documents.ReadAll, VaultExtractResourcePermissions.Read,
            DocumentOwnerArm.Always
        },
        {
            nameof(DocumentAccessRule.Edit), DocumentAccessRule.Edit,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Edit,
            DocumentOwnerArm.UnlessUnderReview
        },
        {
            nameof(DocumentAccessRule.Review), DocumentAccessRule.Review,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Edit,
            DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.Delete), DocumentAccessRule.Delete,
            VaultExtractPermissions.Documents.Delete, VaultExtractResourcePermissions.Delete,
            DocumentOwnerArm.Always
        },
        {
            nameof(DocumentAccessRule.Restore), DocumentAccessRule.Restore,
            VaultExtractPermissions.Documents.Restore, VaultExtractResourcePermissions.Delete,
            DocumentOwnerArm.Always
        },
        {
            nameof(DocumentAccessRule.Retry), DocumentAccessRule.Retry,
            VaultExtractPermissions.Documents.Pipelines.Retry, VaultExtractResourcePermissions.Edit,
            DocumentOwnerArm.UnlessUnderReview
        },
        {
            nameof(DocumentAccessRule.DeclareType), DocumentAccessRule.DeclareType,
            VaultExtractPermissions.Documents.ConfirmClassification, VaultExtractResourcePermissions.Upload,
            DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.Upload), DocumentAccessRule.Upload,
            VaultExtractPermissions.Documents.Upload, null, DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.PermanentDelete), DocumentAccessRule.PermanentDelete,
            VaultExtractPermissions.Documents.PermanentDelete, null, DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.ReprocessFieldExtraction), DocumentAccessRule.ReprocessFieldExtraction,
            VaultExtractPermissions.Documents.Reprocessing.FieldExtraction, null, DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.ReprocessReclassification), DocumentAccessRule.ReprocessReclassification,
            VaultExtractPermissions.Documents.Reprocessing.Reclassification, null, DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.Export), DocumentAccessRule.Export,
            VaultExtractPermissions.Documents.Export, null, DocumentOwnerArm.Never
        },
        {
            nameof(DocumentAccessRule.Statistics), DocumentAccessRule.Statistics,
            VaultExtractPermissions.Documents.ReadAll, null, DocumentOwnerArm.Never
        }
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_rule_carries_the_three_values_the_Issues_table_states(
        string name, DocumentAccessRule rule, string moduleWide, string? resource, DocumentOwnerArm ownerArm)
    {
        rule.ModuleWidePermission.ShouldBe(moduleWide, $"{name}: module-wide permission");
        rule.ResourcePermission.ShouldBe(resource, $"{name}: per-type grant");
        rule.OwnerArm.ShouldBe(ownerArm, $"{name}: owner arm");
    }

    /// <summary>
    /// The table above is hand-written, so on its own it can only check the rows somebody remembered to list. A
    /// rule added to <see cref="DocumentAccessRule"/> and forgotten here would be enforced in production and
    /// pinned nowhere — so the two sets are asserted equal, in both directions.
    /// </summary>
    [Fact]
    public void The_table_covers_exactly_the_rules_that_exist()
    {
        var declared = typeof(DocumentAccessRule)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(DocumentAccessRule))
            .Select(f => f.Name)
            .ToList();

        declared.ShouldNotBeEmpty();
        declared.ShouldBe(Table.Select(row => (string)row[0]!).ToList(), ignoreOrder: true);
    }

    /// <summary>
    /// The owner arm per rule, asserted as three sets rather than thirteen independent rows, so <b>moving</b> a
    /// rule between arms reds here — most dangerously <see cref="DocumentAccessRule.Review"/>, whose two
    /// permission names are identical to <see cref="DocumentAccessRule.Edit"/>'s and which therefore differs
    /// <b>only</b> by this value.
    /// </summary>
    [Fact]
    public void Each_owner_arm_holds_exactly_the_rules_the_table_assigns_to_it()
    {
        RulesWith(DocumentOwnerArm.Always).ShouldBe(
            [
                nameof(DocumentAccessRule.Read),
                nameof(DocumentAccessRule.Delete),
                nameof(DocumentAccessRule.Restore)
            ],
            ignoreOrder: true);

        RulesWith(DocumentOwnerArm.UnlessUnderReview).ShouldBe(
            [
                nameof(DocumentAccessRule.Edit),
                nameof(DocumentAccessRule.Retry)
            ],
            ignoreOrder: true);

        // Everything else. Stated as "the remainder" rather than listed twice, so a new rule lands here by
        // default and only an explicit decision moves it out.
        RulesWith(DocumentOwnerArm.Never).Count.ShouldBe(Table.Count - 5);
    }

    /// <summary>
    /// Review and Edit are the same two permission names and differ only in the owner arm. Stated on its own
    /// because it is the whole reason the arm is per-rule rather than one arm on the checker.
    /// </summary>
    [Fact]
    public void Review_differs_from_Edit_only_by_the_owner_arm()
    {
        DocumentAccessRule.Review.ModuleWidePermission.ShouldBe(DocumentAccessRule.Edit.ModuleWidePermission);
        DocumentAccessRule.Review.ResourcePermission.ShouldBe(DocumentAccessRule.Edit.ResourcePermission);
        DocumentAccessRule.Review.OwnerArm.ShouldBe(DocumentOwnerArm.Never);
        DocumentAccessRule.Edit.OwnerArm.ShouldBe(DocumentOwnerArm.UnlessUnderReview);
    }

    /// <summary>
    /// The owner-locking set is <b>derived</b> from the blocking set, so a blocking reason added later locks
    /// owners out by default and has to be excluded on purpose. Classification is the one exclusion.
    /// </summary>
    [Fact]
    public void The_owner_locking_reasons_are_every_blocking_reason_except_classification()
    {
        ReviewReasonPolicy.OwnerLocking.ShouldBe(
            DocumentReviewReasons.DuplicateSuspected |
            DocumentReviewReasons.FieldExtractionIncomplete |
            DocumentReviewReasons.FieldValidationWarning);

        ReviewReasonPolicy.LocksOwnerEdits(DocumentReviewReasons.UnresolvedClassification).ShouldBeFalse();
        ReviewReasonPolicy.LocksOwnerEdits(DocumentReviewReasons.MissingRequiredFields).ShouldBeFalse();
        ReviewReasonPolicy.LocksOwnerEdits(DocumentReviewReasons.None).ShouldBeFalse();
        ReviewReasonPolicy.LocksOwnerEdits(DocumentReviewReasons.DuplicateSuspected).ShouldBeTrue();
        ReviewReasonPolicy.LocksOwnerEdits(
            DocumentReviewReasons.UnresolvedClassification | DocumentReviewReasons.FieldValidationWarning)
            .ShouldBeTrue();
    }

    private static List<string> RulesWith(DocumentOwnerArm arm)
    {
        var names = new List<string>();
        foreach (var row in Table)
        {
            if ((DocumentOwnerArm)row[4]! == arm)
            {
                names.Add((string)row[0]!);
            }
        }

        return names;
    }
}
