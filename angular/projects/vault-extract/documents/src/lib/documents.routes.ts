import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';

export const DOCUMENTS_ROUTES: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'overview',
  },
  {
    path: 'overview',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-overview/document-overview.component').then(c => c.DocumentOverviewComponent),
  },
  {
    path: 'list',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-list/document-list.component').then(c => c.DocumentListComponent),
  },
  {
    // #632/#635: the entry permission, like every other documents route. Neither arm that now reaches the
    // recycle bin — a per-type Delete grant, or owning the document — is a name in `grantedPolicies`, so
    // neither can be expressed as a route policy at all; guarding on `Documents.Restore` would keep both
    // personas out of the page entirely. The server is the authority: admission to the recycle-bin query is
    // entry alone, and it answers with exactly the deleted documents this caller may read.
    path: 'recycle',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-recycle-bin/document-recycle-bin.component').then(
        c => c.DocumentRecycleBinComponent,
      ),
  },
  {
    path: 'types',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.DocumentTypes.Default },
    loadComponent: () =>
      import('./document-types/document-type-list/document-type-list.component').then(
        c => c.DocumentTypeListComponent,
      ),
  },
  {
    path: 'types/:typeId/fields',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.FieldDefinitions.Default },
    loadComponent: () =>
      import('./fields/field-definition-list/field-definition-list.component').then(
        c => c.FieldDefinitionListComponent,
      ),
  },
  {
    path: 'cabinets',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Cabinets.Default },
    loadComponent: () =>
      import('./cabinets/cabinet-list/cabinet-list.component').then(c => c.CabinetListComponent),
  },
  {
    path: ':id/file',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-file-preview/document-file-preview.component').then(
        c => c.DocumentFilePreviewComponent,
      ),
  },
  {
    path: ':id',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-detail/document-detail.component').then(c => c.DocumentDetailComponent),
  },
];
