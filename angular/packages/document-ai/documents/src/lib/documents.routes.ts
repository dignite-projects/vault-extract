import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { DOCUMENT_AI_PERMISSIONS } from '@dignite/document-ai';

export const DOCUMENTS_ROUTES: Routes = [
  {
    path: '',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-home/document-home.component').then(c => c.DocumentHomeComponent),
  },
  {
    path: 'list',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-list/document-list.component').then(c => c.DocumentListComponent),
  },
  {
    path: 'recycle',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Restore },
    loadComponent: () =>
      import('./documents/document-recycle-bin/document-recycle-bin.component').then(
        c => c.DocumentRecycleBinComponent,
      ),
  },
  {
    path: 'review',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.ConfirmClassification },
    loadComponent: () =>
      import('./documents/document-review-queue/document-review-queue.component').then(
        c => c.DocumentReviewQueueComponent,
      ),
  },
  {
    path: 'types',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.DocumentTypes.Default },
    loadComponent: () =>
      import('./document-types/document-type-list/document-type-list.component').then(
        c => c.DocumentTypeListComponent,
      ),
  },
  {
    path: 'types/:typeId/fields',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.FieldDefinitions.Default },
    loadComponent: () =>
      import('./fields/field-definition-list/field-definition-list.component').then(
        c => c.FieldDefinitionListComponent,
      ),
  },
  {
    path: 'export-templates',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Templates.Default },
    loadComponent: () =>
      import('./exports/export-template-list/export-template-list.component').then(
        c => c.ExportTemplateListComponent,
      ),
  },
  {
    path: 'cabinets',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Cabinets.Default },
    loadComponent: () =>
      import('./cabinets/cabinet-list/cabinet-list.component').then(c => c.CabinetListComponent),
  },
  {
    path: ':id/file',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-file-preview/document-file-preview.component').then(
        c => c.DocumentFilePreviewComponent,
      ),
  },
  {
    path: ':id',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: DOCUMENT_AI_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-detail/document-detail.component').then(c => c.DocumentDetailComponent),
  },
];
