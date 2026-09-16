import { describe, expect, it } from 'vitest';
import { DocumentTypeDto, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import {
  assignableDocumentTypes,
  canEditAnyDocumentType,
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
);

/** Holds only entry (which this helper never consults) — everything must come from the per-type grants. */
const GRANT_ONLY = policiesFrom(EXTRACT_PERMISSIONS.Documents.Default);

describe('readDocumentModuleWidePolicies', () => {
  it('reads each half of the rule from its own module-wide permission name', () => {
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.ReadAll)).toEqual({
      readAll: true,
      edit: false,
      delete: false,
    });
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.ConfirmClassification)).toEqual({
      readAll: false,
      edit: true,
      delete: false,
    });
    expect(policiesFrom(EXTRACT_PERMISSIONS.Documents.Delete)).toEqual({
      readAll: false,
      edit: false,
      delete: true,
    });
  });

  it('does not treat the entry permission as any of them (#632 decision 1)', () => {
    expect(GRANT_ONLY).toEqual({ readAll: false, edit: false, delete: false });
  });
});

describe('documentRights — module-wide holder', () => {
  it('may read / edit / delete every type', () => {
    expect(documentRights(A_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
    });
    expect(documentRights(B_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
    });
  });

  it('reaches untyped documents, which no grant ever covers', () => {
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, MODULE_WIDE)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
    });
  });
});

describe('documentRights — grant-only holder', () => {
  it('may act on the granted type', () => {
    expect(documentRights(A_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: true,
      canEdit: true,
      canDelete: true,
    });
  });

  it('may not act on a different type', () => {
    expect(documentRights(B_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
    });
  });

  it('may not act on an untyped document — fail-closed, module-wide only', () => {
    expect(documentRights(UNTYPED_DOCUMENT, TYPES, GRANT_ONLY)).toEqual({
      canRead: false,
      canEdit: false,
      canDelete: false,
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
    });
    expect(documentRights(undefined, TYPES, MODULE_WIDE).canDelete).toBe(false);
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
