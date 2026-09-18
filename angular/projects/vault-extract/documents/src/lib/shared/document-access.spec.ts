import { describe, expect, it } from 'vitest';
import { DocumentTypeDto, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import {
  assignableDocumentTypes,
  canAssignAnyDocumentType,
  canEditAnyDocumentType,
  canUploadIntoAnyDocumentType,
  reclassificationTargetTypes,
  rightsOf,
} from './document-access';

// #635 decision 5: the client no longer re-derives what it may do with a document — the server decides and
// sends the verdict down with the row. What survives here is the pair of questions that are about TYPES
// rather than documents, plus the fail-closed reading of the verdict itself.

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

/** An Edit grant without an Upload one: may correct this type's documents, may not declare the type. */
const TYPE_C: DocumentTypeDto = {
  id: 'type-c',
  typeCode: 'receipt',
  displayName: 'Receipt',
  resourcePermissions: { [RESOURCES.Edit]: true },
};

const TYPES = [TYPE_A, TYPE_B];

describe('rightsOf — the server verdict, read fail-closed (#635)', () => {
  it('reads every right the row carries', () => {
    expect(
      rightsOf({
        rights: {
          canRead: true,
          canEdit: true,
          canReview: false,
          canDelete: true,
          canRestore: true,
          canRetry: true,
        },
      }),
    ).toEqual({
      canRead: true,
      canEdit: true,
      canReview: false,
      canDelete: true,
      canRestore: true,
      canRetry: true,
    });
  });

  it('denies everything for a document that is not in hand yet', () => {
    // The detail page holds `null` until its load resolves, and every gate on it reads through here.
    expect(rightsOf(null)).toEqual({
      canRead: false,
      canEdit: false,
      canReview: false,
      canDelete: false,
      canRestore: false,
      canRetry: false,
    });
    expect(rightsOf(undefined).canDelete).toBe(false);
  });

  it('denies everything for a row that carries no rights at all', () => {
    // The generated proxy types `rights` as optional, so an older server — or any row the mapper missed —
    // must read as a denial rather than as `undefined` slipping through a truthiness check somewhere.
    expect(rightsOf({}).canEdit).toBe(false);
    expect(rightsOf({ rights: undefined }).canRetry).toBe(false);
  });

  it('treats an absent individual right as denied, not as inherited from its neighbours', () => {
    expect(rightsOf({ rights: { canRead: true } })).toEqual({
      canRead: true,
      canEdit: false,
      canReview: false,
      canDelete: false,
      canRestore: false,
      canRetry: false,
    });
  });
});

describe('assignableDocumentTypes — which types may this caller declare (#629)', () => {
  it('lists every visible type for a ConfirmClassification holder', () => {
    expect(assignableDocumentTypes(TYPES, true)).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted types otherwise', () => {
    expect(assignableDocumentTypes(TYPES, false)).toEqual([TYPE_A]);
  });

  it('does not take an Edit grant as permission to declare the type', () => {
    // Edit and Upload are separate grants: correcting this type's documents is not the same right as
    // deciding that a document belongs to it.
    expect(assignableDocumentTypes([TYPE_C], false)).toEqual([]);
  });

  it('returns nothing when no type carries an Upload grant', () => {
    expect(assignableDocumentTypes([TYPE_B], false)).toEqual([]);
  });
});

describe('canEditAnyDocumentType — the review-queue affordances (#635 decision 6)', () => {
  it('is true module-wide, whatever the type list says', () => {
    expect(canEditAnyDocumentType([TYPE_B], true)).toBe(true);
    expect(canEditAnyDocumentType([], true)).toBe(true);
  });

  it('is true through an Edit grant on a single visible type', () => {
    expect(canEditAnyDocumentType([TYPE_B, TYPE_C], false)).toBe(true);
  });

  it('is false when no visible type carries an Edit grant', () => {
    expect(canEditAnyDocumentType([TYPE_B], false)).toBe(false);
  });

  it('is false while the type list is still empty', () => {
    // The state the store starts in. Consumers gate on DocumentTypesStore.isLoading() so this answer is
    // never rendered as a denial before the fetch lands.
    expect(canEditAnyDocumentType([], false)).toBe(false);
  });
});

// #645: the upload entry points show for anyone who may upload — Documents.Upload (every type), or a
// type-level Upload grant on at least one visible type, which since #645 suffices on its own.
describe('canUploadIntoAnyDocumentType — the upload entry points (#645)', () => {
  it('is true with Documents.Upload, whatever the type list says', () => {
    expect(canUploadIntoAnyDocumentType([TYPE_B], true)).toBe(true);
    expect(canUploadIntoAnyDocumentType([], true)).toBe(true);
  });

  it('is true through an Upload grant on a single visible type', () => {
    expect(canUploadIntoAnyDocumentType([TYPE_B, TYPE_A], false)).toBe(true);
  });

  it('is false when no visible type carries an Upload grant', () => {
    // TYPE_C carries Edit only: editing a type's documents is not uploading into it.
    expect(canUploadIntoAnyDocumentType([TYPE_B, TYPE_C], false)).toBe(false);
  });
});

// #648: the role-level arm on its own, because AI re-classification has no target type to judge.
describe('canAssignAnyDocumentType — the DeclareType role-level arm (#648)', () => {
  it('is true for ConfirmClassification, for Documents.Upload, and for both', () => {
    expect(canAssignAnyDocumentType(true, false)).toBe(true);
    expect(canAssignAnyDocumentType(false, true)).toBe(true);
    expect(canAssignAnyDocumentType(true, true)).toBe(true);
  });

  it('is false for a caller holding neither', () => {
    expect(canAssignAnyDocumentType(false, false)).toBe(false);
  });
});

// #645: a reclassification target mirrors the server's DeclareType row, the only row whose role-level arm
// has two members.
describe('reclassificationTargetTypes — the confirm / reclassify pickers (#645)', () => {
  it('lists every type for ConfirmClassification', () => {
    expect(reclassificationTargetTypes(TYPES, true, false)).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists every type for Documents.Upload', () => {
    expect(reclassificationTargetTypes(TYPES, false, true)).toEqual([TYPE_A, TYPE_B]);
  });

  it('lists only the Upload-granted types for a caller holding neither', () => {
    expect(reclassificationTargetTypes(TYPES, false, false)).toEqual([TYPE_A]);
  });
});
