import { DocumentRightsDto, DocumentTypeDto, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';

/**
 * What the client is still allowed to decide for itself about document access (#635 decision 5).
 *
 * Until #635 this file hand-mirrored the server's whole rule table: the client received the *inputs* of the
 * judgment — the caller's module-wide permissions plus its per-type grant dictionary — and re-derived the
 * answer per row. That copy keyed types by `typeCode` against the *active* types while the server keys by id
 * across soft-deleted ones, so a document on an archived type came back with every action missing; and it
 * could not express ownership at all, which is not a property of a type. The judgment now happens once, on
 * the server, and rides down with the row as {@link DocumentRightsDto} — see {@link rightsOf}.
 *
 * What is left here are the two questions that are genuinely about **types** rather than about a document,
 * and so have no row to carry their answer:
 *
 * - {@link assignableDocumentTypes} — which types may this caller *declare* (the upload card's picker, and
 *   the confirm / reclassify pickers, whose target type is judged separately from the document itself);
 * - {@link canEditAnyDocumentType} — may this caller run the edit family on *anything* at all, which gates
 *   the navigational review-queue affordances that have no single document to judge.
 *
 * Both are answered from `IDocumentTypeAppService.GetVisibleAsync`'s per-type `resourcePermissions`, exactly
 * as before. Neither is a security boundary; both only decide what the UI offers.
 */

/** Everything denied — the fail-closed answer when a row carries no rights at all. */
const NO_RIGHTS: Required<DocumentRightsDto> = {
  canRead: false,
  canEdit: false,
  canReview: false,
  canDelete: false,
  canRestore: false,
  canRetry: false,
};

/** Anything that carries the server's per-document verdict: `DocumentDto`, `DocumentListItemDto`, or a stub. */
export interface DocumentRightsCarrier {
  rights?: DocumentRightsDto;
}

/**
 * The rights the server sent down with this document, with every unanswered question denied.
 *
 * The generated proxy types every DTO member as optional, so a row from an older server — or the `null`
 * document a detail page holds before its load resolves — must not read as permission. One place decides
 * that, rather than an `?? false` at each of the dozen call sites.
 */
export function rightsOf(document: DocumentRightsCarrier | null | undefined): Required<DocumentRightsDto> {
  const rights = document?.rights;
  if (!rights) {
    return NO_RIGHTS;
  }
  return {
    canRead: rights.canRead === true,
    canEdit: rights.canEdit === true,
    canReview: rights.canReview === true,
    canDelete: rights.canDelete === true,
    canRestore: rights.canRestore === true,
    canRetry: rights.canRetry === true,
  };
}

/**
 * The types this caller may **assign** to a document — the only ones a declare / confirm / reclassify picker
 * may list. `Documents.ConfirmClassification` admits every type of the layer; otherwise it is the types
 * carrying this caller's own `Upload` grant. That is the `DeclareType` row of the server's rule table
 * (#629, unchanged by #635): deciding a document's type at upload is the same act as confirming its
 * classification afterwards.
 *
 * `canConfirmClassification` is the module-wide half, snapshotted once per component: ABP's
 * `PermissionService.getGrantedPolicy` re-parses the policy expression and re-reads the config-state snapshot
 * on every call, and these accessors run inside templates on every change-detection pass.
 */
export function assignableDocumentTypes(
  types: readonly DocumentTypeDto[],
  canConfirmClassification: boolean,
): DocumentTypeDto[] {
  if (canConfirmClassification) {
    return [...types];
  }
  return types.filter(
    t => t.resourcePermissions?.[EXTRACT_PERMISSIONS.DocumentTypes.Resources.Upload] === true,
  );
}

/**
 * Whether the caller may run the edit family on *anything* reachable: module-wide, or through an `Edit` grant
 * on at least one visible type.
 *
 * Gates the navigational review-queue affordances — the list's needs-review toggle and the overview's quick
 * link. They are filters, not per-document actions, so there is no row whose `rights` could answer them, and
 * a per-type grant is not a name in `grantedPolicies` and so cannot be a route policy either. #635 decision 6
 * keeps the review queue a *reviewer's* surface: an uploader whose own document is pending review meets it in
 * the ordinary list, with its status.
 */
export function canEditAnyDocumentType(
  types: readonly DocumentTypeDto[],
  canConfirmClassification: boolean,
): boolean {
  return (
    canConfirmClassification ||
    types.some(t => t.resourcePermissions?.[EXTRACT_PERMISSIONS.DocumentTypes.Resources.Edit] === true)
  );
}
