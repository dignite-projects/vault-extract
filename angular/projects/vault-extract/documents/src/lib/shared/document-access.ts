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
 * What is left here are the questions that are genuinely about **types** rather than about a document, and
 * so have no row to carry their answer:
 *
 * - {@link assignableDocumentTypes} — which types may this caller *name*: the upload card's picker, and via
 *   {@link reclassificationTargetTypes} the confirm / reclassify pickers, whose target type is judged
 *   separately from the document itself;
 * - {@link canUploadIntoAnyDocumentType} — may this caller upload at all, which gates the upload entry points;
 * - {@link canEditAnyDocumentType} — may this caller run the edit family on *anything* at all, which gates
 *   the navigational review-queue affordances that have no single document to judge.
 *
 * All are answered from `IDocumentTypeAppService.GetVisibleAsync`'s per-type `resourcePermissions`. None is
 * a security boundary; they only decide what the UI offers.
 *
 * #645: every one of them follows the server's one pattern — a role-level permission means "every type", a
 * type-level grant means "this type" — so each takes the role-level answer as a boolean and reads the
 * type-level answer from the list.
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

const RESOURCES = EXTRACT_PERMISSIONS.DocumentTypes.Resources;

/** A type-level grant — every `Resources` member except `Name`, which names the resource, not a grant. */
type DocumentTypeGrant = (typeof RESOURCES)[Exclude<keyof typeof RESOURCES, 'Name'>];

/** Whether this type carries the caller's own type-level `grant` (e.g. `Upload`, "上传到此文档类型"). */
function hasGrant(type: DocumentTypeDto, grant: DocumentTypeGrant): boolean {
  return type.resourcePermissions?.[grant] === true;
}

/**
 * The types this caller may **name** for a document — the only ones a picker may list. Every type of the
 * layer when `canAssignAllTypes`; otherwise the types carrying the caller's own `Upload` grant, which is the
 * type-level arm of both the server's `Upload` and `DeclareType` rows.
 *
 * `canAssignAllTypes` is the role-level arm, and which permission it is depends on the question (#645):
 *
 * - **uploading** — `Documents.Upload` ("上传到所有文档类型") alone. `ConfirmClassification` left the upload
 *   path in #645;
 * - **reclassifying** — `ConfirmClassification` ("编辑所有类型的文档") **or** `Documents.Upload`, which is
 *   {@link canAssignAnyDocumentType}; {@link reclassificationTargetTypes} applies it.
 *
 * The role-level answer is snapshotted once per component: ABP's `PermissionService.getGrantedPolicy`
 * re-parses the policy expression and re-reads the config-state snapshot on every call, and these accessors
 * run inside templates on every change-detection pass.
 */
export function assignableDocumentTypes(
  types: readonly DocumentTypeDto[],
  canAssignAllTypes: boolean,
): DocumentTypeDto[] {
  if (canAssignAllTypes) {
    return [...types];
  }
  return types.filter(t => hasGrant(t, RESOURCES.Upload));
}

/**
 * The role-level arm of the server's `DeclareType` row (#645) — the only two-member set on the rule table: a
 * reviewer holding `ConfirmClassification` assigns any type, and so does someone holding `Documents.Upload`,
 * who could have uploaded the document into any type in the first place.
 *
 * #648: AI re-classification needs exactly this, because the classifier — not the caller — names the target,
 * so the server judges `DeclareType` on the empty subject and only this arm can answer. Exported so the OR
 * exists in one place on the client: the pickers below and the detail page's "重新分类" button read it.
 */
export function canAssignAnyDocumentType(
  canConfirmClassification: boolean,
  canUploadIntoAllTypes: boolean,
): boolean {
  return canConfirmClassification || canUploadIntoAllTypes;
}

/**
 * The target types a confirm / reclassify picker may list — the server's `DeclareType` row (#645). Every type
 * for {@link canAssignAnyDocumentType}; otherwise the types carrying an `Upload` grant.
 *
 * One function so the picker's answer exists once: the list's confirm dialog and the detail page's confirm /
 * reclassify dialog both call it, and a second hand-written copy is how the two pickers would drift apart.
 */
export function reclassificationTargetTypes(
  types: readonly DocumentTypeDto[],
  canConfirmClassification: boolean,
  canUploadIntoAllTypes: boolean,
): DocumentTypeDto[] {
  return assignableDocumentTypes(
    types,
    canAssignAnyDocumentType(canConfirmClassification, canUploadIntoAllTypes),
  );
}

/**
 * Whether the caller may upload at all: `Documents.Upload` ("upload into every type", which also admits an
 * untyped upload the AI classifies), or a type-level `Upload` grant on at least one visible type — which
 * since #645 suffices **on its own**. Gates the upload entry points (the overview's upload card, the list's
 * upload button), which would otherwise hide from exactly the ordinary uploader #645 describes: entry plus
 * an `Upload` grant on each type they may upload into, and nothing else.
 *
 * The type-level half can only answer once the visible types are in hand. Callers must therefore not read
 * `false` from an empty, still-loading or failed type list as "may not upload" — see the overview's upload
 * slot for how the three states are told apart.
 */
export function canUploadIntoAnyDocumentType(
  types: readonly DocumentTypeDto[],
  canUploadIntoAllTypes: boolean,
): boolean {
  return canUploadIntoAllTypes || types.some(t => hasGrant(t, RESOURCES.Upload));
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
  return canConfirmClassification || types.some(t => hasGrant(t, RESOURCES.Edit));
}
