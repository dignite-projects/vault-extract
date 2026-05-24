import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { PAPERBASE_PERMISSIONS } from '@dignite/paperbase';

export const DOCUMENTS_ROUTES: Routes = [
  {
    path: '',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./document-list/document-list.component').then(c => c.DocumentListComponent),
  },
  {
    path: 'upload',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.Upload },
    loadComponent: () =>
      import('./document-upload/document-upload.component').then(c => c.DocumentUploadComponent),
  },
  {
    path: 'recycle',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.Restore },
    loadComponent: () =>
      import('./document-recycle-bin/document-recycle-bin.component').then(
        c => c.DocumentRecycleBinComponent,
      ),
  },
  {
    path: 'review',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.ConfirmClassification },
    loadComponent: () =>
      import('./document-review-queue/document-review-queue.component').then(
        c => c.DocumentReviewQueueComponent,
      ),
  },
  {
    path: 'types',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.ConfirmClassification },
    loadComponent: () =>
      import('./document-type-list/document-type-list.component').then(
        c => c.DocumentTypeListComponent,
      ),
  },
  {
    path: 'types/:typeCode/fields',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.ConfirmClassification },
    loadComponent: () =>
      import('./field-definition-list/field-definition-list.component').then(
        c => c.FieldDefinitionListComponent,
      ),
  },
  {
    path: 'cabinets',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Cabinets.Default },
    loadComponent: () =>
      import('./cabinet-list/cabinet-list.component').then(c => c.CabinetListComponent),
  },
  {
    path: ':id',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: PAPERBASE_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./document-detail/document-detail.component').then(c => c.DocumentDetailComponent),
  },
];
