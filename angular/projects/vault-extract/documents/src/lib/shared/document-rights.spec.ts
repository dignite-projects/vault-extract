import { describe, expect, it } from 'vitest';
import { DocumentTypeDto, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import {
  assignableDocumentTypes,
  canEditAnyDocumentType,
  canRestoreAnyDocumentType,
  documentRights,
  documentRightsAccessor,
  isDocumentAccessGranted,
  readDocumentModuleWidePolicies,
} from './document-rights';

// #632: the client mirror of DocumentTypeAccessChecker — "the module-wide permission for the operation, OR
// the matching grant on the document's current type", with untyped documents reduced to the module-wide half.
// These specs are the load-bearing ones: the components delegate every per-row decision here, so a break in
// this table is a break in every gate on both pages.

const RESOURCES = EXTRACT_PERMISSIONS.DocumentTypes.Resources;

/** Type A — the caller holds every per-type grant on it. */
const TYPE_A: DocumentTypeDto = {
  id: 'type-a',
  typeCode: 'contract',
  displayName: 'Contract',
  resourcePermissions: {
    [RESOURCES.Read]: true,
    [RESOURCES.Edit]: true,
    [RESOURCES.Delete]: true,
    [RESOURCES.Upload]: true,
  },
};

/** Type B — visible to the caller (GetVisibleAsync returns every type of the layer) but granted nothing. */
const TYPE_B: DocumentTypeDto = {
  id: 'type-b',
  typeCode: 'invoice',
  displayName: 'Invoice',
  resourcePermissions: {
    [RESOURCES.Read]: false,
    [RESOURCES.Edit]: false,
    [RESOURCES.Delete]: false,
    [RESOURCES.Upload]: false,
  },
};

const TYPES = [TYPE_A, TYPE_B];

const A_DOCUMENT = { documentTypeCode: 'contract' };
const B_DOCUMENT = { documentTypeCode: 'invoice' };
/** Unclassified / failed classification / container: no type, so no grant can ever name it. */
const UNTYPED_DOCUMENT = { documentTypeCode: null };

function policiesFrom(...grantedPolicies: string[]) {
  const granted = new Set(grantedPolicies);
  return readDocumentModuleWidePolicies({ getGrantedPolicy: key => granted.has(key) });
}

/** Holds every module-wide permission — the pre-#632 operator. */
const MODULE_WIDE = policiesFrom(
  EXTRACT_PERMISSIONS.Documents.ReadAll,
  EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  EXTRACT_PERMISSIONS.Documents.Delete,
  EXTRACT_PERMISSIONS.Documents.Restore,
);

/** Holds only entry (which this helper never consults) — everything must come from the per-type grants. */
const GRANT_ONLY = policiesFrom(EXTRACT_PERMISSIONS.Documents.Default);

describe('readDocumentModuleWidePolicies', () => {
  it('reads each half of the rule from its own module-wide permission name', () => {
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.ReadAll)).toEqual({
      readAll: true,
      edit: false,
      delete: false,
      restore: false,
    });
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.ConfirmClassification)).toEqual({
      readAll: false,
      edit: true,
      delete: false,
      restore: false,
    });
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.Delete)).toEqual({
      readAll: false,
      edit: false,
      delete: true,
      restore: false,
    });
    // Restore is its own module-wide name even though it shares Delete's per-type grant.
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.Restore)).toEqual({
      readAll: false,
      edit: false,
      delete: false,
      restore: true,
    });
  });

  it('does not treat the entry permission as any of them (#632 decision 1)', () => {
    expect(GRANT_ONLY).toEqual({ readAll: false, edit: false, delete: false, restore: false });
  });
});

describe('documentRights — module-wide holder', () => {
  it('may read / edit / delete / restore every type', () => {
    expect(documentRights(A_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
    expect(documentRights(B_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
  });

  it('reaches untyped documents, which no grant ever covers', () => {
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
  });
});

describe('documentRights — grant-only holder', () => {
  it('may act on the granted type', () => {
    expect(documentRights(A_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
      canRestore: true,
    });
  });

  it('may not act on a different type', () => {
    expect(documentRights(B_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
      canRestore: false,
    });
  });

  it('may not act on an untyped document — fail-closed, module-wide only', () => {
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
      canRestore: false,
    });
    // An absent code is the same case as an explicit null (the DTO field is optional).
    expect(documentRights({}, TYPES, GRANT_ONLY).canRead).toBe(false);
    expect(documentRights({ documentTypeCode: '' }, TYPES, GRANT_ONLY).canRead).toBe(false);
  });

  it('keeps each grant to its own operation family', () => {
    const readOnlyOnA = policiesFrom();
    const types: DocumentTypeDto[] = [
      { ...TYPE_A, resourcePermissions: { [RESOURCES.Read]: true } },
    ];
    expect(documentRights(A_DOCUMENT, types, readOnlyOnA)).toEqual({
      canRead: true,
      canEdit: false,
      canDelete: false,
      // A Read grant is not a Delete grant, so it is not a restore right either.
      canRestore: false,
    });
  });

  it('denies when the document names a type that is not in the loaded list', () => {
    // A since-archived type, or the types fetch still in flight / failed. Can only hide an action from a
    // grant-only caller; a module-wide holder is unaffected.
    expect(documentRights(A_DOCUMENT, [], GRANT_ONLY).canRead).toBe(false);
    expect(documentRights(A_DOCUMENT, [], MODULE_WIDE).canRead).toBe(true);
  });

  it('denies when the dictionary is absent (GetVisibleAsync was asked to skip the populator)', () => {
    const types: DocumentTypeDto[] = [{ id: 'type-a', typeCode: 'contract', displayName: 'Contract' }];
    expect(documentRights(A_DOCUMENT, types, GRANT_ONLY).canRead).toBe(false);
  });
});

describe('documentRights — no document in hand', () => {
  it('denies everything rather than falling back to the module-wide half', () => {
    expect(documentRights(null, TYPES, MODULE_WIDE)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
      canRestore: false,
    });
    expect(documentRights(undefined, TYPES, MODULE_WIDE).canDelete).toBe(false);
  });
});

describe('isDocumentAccessGranted — restore (#632 change 2: whoever may delete may undo)', () => {
  /** Holds module-wide Restore and nothing else — the classic recycle-bin operator. */
  const RESTORE_ONLY = policiesFrom(EXTRACT_PERMISSIONS.Documents.Restore);

  it('is admitted by module-wide Documents.Restore on any type', () => {
    expect(isDocumentAccessGranted('restore', TYPE_B, RESTORE_ONLY)).toBe(true);
    expect(documentRights(B_DOCUMENT, TYPES, RESTORE_ONLY).canRestore).toBe(true);
  });

  it('is admitted by the Delete grant on the document own type — no Restore grant exists', () => {
    expect(documentRights(A_DOCUMENT, TYPES, GRANT_ONLY).canRestore).toBe(true);
  });

  it('is denied on a type the caller holds no Delete grant on', () => {
    expect(documentRights(B_DOCUMENT, TYPES, GRANT_ONLY).canRestore).toBe(false);
  });

  it('is denied on an untyped document, which no grant can name', () => {
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, GRANT_ONLY).canRestore).toBe(false);
    // ...and admitted for the same document once the module-wide half is held.
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, RESTORE_ONLY).canRestore).toBe(true);
  });

  it('does not borrow the module-wide Delete permission', () => {
    // Documents.Delete is soft delete; it says nothing about undoing one. Only the per-type grant is shared.
    const deleteOnly = policiesFrom(EXTRACT_PERMISSIONS.Documents.Delete);
    expect(isDocumentAccessGranted('restore', TYPE_B, deleteOnly)).toBe(false);
    expect(isDocumentAccessGranted('delete', TYPE_B, deleteOnly)).toBe(true);
  });
});

describe('canRestoreAnyDocumentType', () => {
  it('is true module-wide even before the type list has landed', () => {
    expect(canRestoreAnyDocumentType([], policiesFrom(EXTRACT_PERMISSIONS.Documents.Restore))).toBe(
      true,
    );
  });

  it('is true for a grant-only holder with a Delete grant on at least one visible type', () => {
    expect(canRestoreAnyDocumentType(TYPES, GRANT_ONLY)).toBe(true);
  });

  it('is false for a grant-only holder with no Delete grant anywhere', () => {
    expect(canRestoreAnyDocumentType([TYPE_B], GRANT_ONLY)).toBe(false);
    // A Read grant is not a Delete grant.
    expect(
      canRestoreAnyDocumentType(
        [{ ...TYPE_A, resourcePermissions: { [RESOURCES.Read]: true } }],
        GRANT_ONLY,
      ),
    ).toBe(false);
  });
});

describe('isDocumentAccessGranted — declareType (the #629 rule, unchanged)', () => {
  it('pairs the target type with its Upload grant, not with Edit', () => {
    const uploadOnlyOnA: DocumentTypeDto = {
      ...TYPE_A,
      resourcePermissions: { [RESOURCES.Upload]: true },
    };
    expect(isDocumentAccessGranted('declareType', uploadOnlyOnA, GRANT_ONLY)).toBe(true);
    expect(isDocumentAccessGranted('edit', uploadOnlyOnA, GRANT_ONLY)).toBe(false);
  });

  it('accepts ConfirmClassification as its module-wide half', () => {
    const confirmOnly = policiesFrom(EXTRACT_PERMISSIONS.Documents.ConfirmClassification);
    expect(isDocumentAccessGranted('declareType', TYPE_B, confirmOnly)).toBe(true);
  });
});

describe('assignableDocumentTypes', () => {
  it('is every type of the layer for a ConfirmClassification holder', () => {
    expect(assignableDocumentTypes(TYPES, MODULE_WIDE)).toEqual(TYPES);
  });

  it('is only the Upload-granted types otherwise', () => {
    expect(assignableDocumentTypes(TYPES, GRANT_ONLY)).toEqual([TYPE_A]);
  });

  it('is empty when nothing is granted', () => {
    expect(assignableDocumentTypes([TYPE_B], GRANT_ONLY)).toEqual([]);
  });
});

describe('canEditAnyDocumentType', () => {
  it('is true module-wide even before the type list has landed', () => {
    expect(canEditAnyDocumentType([], MODULE_WIDE)).toBe(true);
  });

  it('is true for a grant-only holder with an Edit grant on at least one visible type', () => {
    expect(canEditAnyDocumentType(TYPES, GRANT_ONLY)).toBe(true);
  });

  it('is false for a grant-only holder with no Edit grant anywhere', () => {
    expect(canEditAnyDocumentType([TYPE_B], GRANT_ONLY)).toBe(false);
    // A Read-only grant is not an Edit grant.
    expect(
      canEditAnyDocumentType(
        [{ ...TYPE_A, resourcePermissions: { [RESOURCES.Read]: true } }],
        GRANT_ONLY,
      ),
    ).toBe(false);
  });
});

describe('documentRightsAccessor', () => {
  it('re-reads the type list on every call, so rows re-evaluate once the grants land', () => {
    let types: DocumentTypeDto[] = [];
    const rightsFor = documentRightsAccessor(() => types, GRANT_ONLY);

    expect(rightsFor(A_DOCUMENT).canDelete).toBe(false);

    types = TYPES;
    expect(rightsFor(A_DOCUMENT).canDelete).toBe(true);
    expect(rightsFor(B_DOCUMENT).canDelete).toBe(false);
  });
});
