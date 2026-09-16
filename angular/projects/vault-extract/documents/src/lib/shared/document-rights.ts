import { DocumentTypeDto, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';

/**
 * #632: the client-side mirror of the backend's one rule —
 * **an operation on a document is authorized by the module-wide permission for that operation, OR by the
 * matching grant on the document's current type.**
 *
 * The authority is `DocumentTypeAccessChecker` in the Application layer; nothing here is a security boundary.
 * Its only job is that the UI stops offering what the server will refuse, and stops hiding what it will
 * allow. Every list the UI renders is already narrowed server-side (#632 decision 3), so this file never
 * filters rows — only per-row actions and the assign-a-type pickers.
 *
 * The module-wide half comes from `PermissionService`; the per-type half comes from
 * `DocumentTypeDto.resourcePermissions`, which `IDocumentTypeAppService.GetVisibleAsync` fills per type for
 * the calling principal. Keeping the pairing in ONE table here is the point of this file: the backend's
 * `DocumentAccessRule` exists for exactly the same reason, and the two are only equivalent as long as
 * neither side hand-writes the OR at its call sites.
 */

/** The operation families of the rule table. Mirrors `DocumentAccessRule`'s four static members. */
export type DocumentAccessRule = 'read' | 'edit' | 'delete' | 'declareType';

/**
 * The module-wide permissions this rule table consults, snapshotted once per component.
 *
 * Snapshotted rather than re-read per call because `PermissionService.getGrantedPolicy` parses the policy
 * expression and reads the config-state snapshot on every call, and these accessors run inside templates on
 * every change-detection pass. This also matches the repo convention of storing permissions as plain boolean
 * fields evaluated at construction (`.claude/rules/angular.md`).
 */
export interface DocumentModuleWidePolicies {
  /** `Documents.ReadAll` — read every type of the layer. */
  readonly readAll: boolean;
  /** `Documents.ConfirmClassification` — the module-wide half of BOTH edit and declare-a-type. */
  readonly edit: boolean;
  /** `Documents.Delete` — module-wide soft delete. */
  readonly delete: boolean;
}

/** The module-wide permission name behind each field of {@link DocumentModuleWidePolicies}. */
const MODULE_WIDE_PERMISSION_NAMES: Record<keyof DocumentModuleWidePolicies, string> = {
  readAll: EXTRACT_PERMISSIONS.Documents.ReadAll,
  edit: EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  delete: EXTRACT_PERMISSIONS.Documents.Delete,
};

/**
 * The rule table itself — the single place the two halves of each OR are paired. `declareType` deliberately
 * shares `edit`'s module-wide permission (`ConfirmClassification`) while carrying the *Upload* grant, exactly
 * as #629 defined it: deciding a document's type is the same act an upload-time declared type performs.
 */
const DOCUMENT_ACCESS_RULES: Record<
  DocumentAccessRule,
  { readonly moduleWide: keyof DocumentModuleWidePolicies; readonly resource: string }
> = {
  read: { moduleWide: 'readAll', resource: EXTRACT_PERMISSIONS.DocumentTypes.Resources.Read },
  edit: { moduleWide: 'edit', resource: EXTRACT_PERMISSIONS.DocumentTypes.Resources.Edit },
  delete: { moduleWide: 'delete', resource: EXTRACT_PERMISSIONS.DocumentTypes.Resources.Delete },
  declareType: { moduleWide: 'edit', resource: EXTRACT_PERMISSIONS.DocumentTypes.Resources.Upload },
};

/** Minimal shape of ABP's `PermissionService`, so specs can pass a plain object. */
export interface GrantedPolicyReader {
  getGrantedPolicy(name: string): boolean;
}

/** Whatever carries a document's exit-contract type code — `DocumentDto`, `DocumentListItemDto`, or a stub. */
export interface DocumentTypeCoded {
  documentTypeCode?: string | null;
}

/** What the caller may do to one document. */
export interface DocumentRights {
  readonly canRead: boolean;
  readonly canEdit: boolean;
  readonly canDelete: boolean;
}

/** Everything denied — the fail-closed answer for "no document in hand". */
const NO_RIGHTS: DocumentRights = { canRead: false, canEdit: false, canDelete: false };

/** Snapshots the three module-wide permissions the rule table needs. */
export function readDocumentModuleWidePolicies(
  permissionService: GrantedPolicyReader,
): DocumentModuleWidePolicies {
  return {
    readAll: permissionService.getGrantedPolicy(MODULE_WIDE_PERMISSION_NAMES.readAll),
    edit: permissionService.getGrantedPolicy(MODULE_WIDE_PERMISSION_NAMES.edit),
    delete: permissionService.getGrantedPolicy(MODULE_WIDE_PERMISSION_NAMES.delete),
  };
}

/**
 * The rule. `documentType` is the type the operation is judged against — a document's current type for
 * read / edit / delete, the **target** type for `declareType`.
 *
 * `null`/`undefined` means the operation has no type to name, which happens in two ways and is fail-closed in
 * both: an **untyped document** (unclassified, failed classification, container — no grant can ever cover it,
 * mirroring `DocumentTypeAccessChecker`), and a typed document whose type row is not among the loaded visible
 * types (a since-archived type, or the types fetch still in flight / failed). The second case can only ever
 * *hide* an action from a grant-only caller; a module-wide holder is unaffected, and the server remains the
 * authority either way.
 */
export function isDocumentAccessGranted(
  rule: DocumentAccessRule,
  documentType: DocumentTypeDto | null | undefined,
  policies: DocumentModuleWidePolicies,
): boolean {
  const { moduleWide, resource } = DOCUMENT_ACCESS_RULES[rule];
  if (policies[moduleWide]) {
    return true;
  }
  if (!documentType) {
    return false;
  }
  return documentType.resourcePermissions?.[resource] === true;
}

/**
 * Resolves a document's exit-contract `documentTypeCode` to the loaded type row. The DTOs carry the code, not
 * the id (the code is the output contract; the id is the command contract), which is why both components
 * already map code → type this way.
 */
export function findDocumentType(
  documentTypeCode: string | null | undefined,
  types: readonly DocumentTypeDto[],
): DocumentTypeDto | null {
  if (!documentTypeCode) {
    return null;
  }
  return types.find(t => t.typeCode === documentTypeCode) ?? null;
}

/** What the caller may do to this document. `null`/`undefined` document ⇒ everything denied. */
export function documentRights(
  document: DocumentTypeCoded | null | undefined,
  types: readonly DocumentTypeDto[],
  policies: DocumentModuleWidePolicies,
): DocumentRights {
  if (!document) {
    return NO_RIGHTS;
  }
  const type = findDocumentType(document.documentTypeCode, types);
  return {
    canRead: isDocumentAccessGranted('read', type, policies),
    canEdit: isDocumentAccessGranted('edit', type, policies),
    canDelete: isDocumentAccessGranted('delete', type, policies),
  };
}

/**
 * The types the caller may **assign** — the only ones a Confirm / Reclassify picker may list.
 * `ConfirmClassification` admits every type of the layer; otherwise it is the types carrying an Upload grant,
 * which is the same set `DocumentUploadComponent.declarableTypes` computes for the upload picker (#629).
 */
export function assignableDocumentTypes(
  types: readonly DocumentTypeDto[],
  policies: DocumentModuleWidePolicies,
): DocumentTypeDto[] {
  return types.filter(t => isDocumentAccessGranted('declareType', t, policies));
}

/**
 * Whether the caller may run the edit family on *anything* reachable: module-wide, or through an Edit grant on
 * at least one visible type. Gates the navigational review-queue affordances (the list's Needs-review toggle,
 * the overview's quick link), which are filters rather than per-document actions and so have no single
 * document to judge. Per-document actions use {@link documentRights} instead.
 */
export function canEditAnyDocumentType(
  types: readonly DocumentTypeDto[],
  policies: DocumentModuleWidePolicies,
): boolean {
  return policies.edit || types.some(t => isDocumentAccessGranted('edit', t, policies));
}

/**
 * Binds the rule to one component's live type list: returns the accessor its template calls per row
 * (`rightsFor(doc).canDelete`). Reading the signal inside keeps the template reactive to the types landing,
 * and every component shares this one implementation rather than re-deriving the OR.
 */
export function documentRightsAccessor(
  documentTypes: () => readonly DocumentTypeDto[],
  policies: DocumentModuleWidePolicies,
): (document: DocumentTypeCoded | null | undefined) => DocumentRights {
  return document => documentRights(document, documentTypes(), policies);
}
