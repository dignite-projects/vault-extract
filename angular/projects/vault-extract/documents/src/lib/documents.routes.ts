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
    // #632: the entry permission, like every other documents route. A per-type Delete grant — which is now
    // half of "may restore" — is not a name in `grantedPolicies`, so it cannot be expressed as a route policy
    // at all; leaving the guard on `Documents.Restore` would keep the per-type deleter out of the page
    // entirely and make the new rule unreachable from the UI. The page's own content gate
    // (`canRestoreAnything`) and the server's `CheckOnAnyTypeAsync` are the authority.
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
