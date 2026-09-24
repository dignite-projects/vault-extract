import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { FieldTypeResolver, provideFlexFields } from '@dignite/ng.flex-fields';
import { provideCKEditorFieldType } from '@dignite/ng.flex-fields-ckeditor';
import { EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import { provideTagsFieldType } from './shared/field-types/tags/provide-tags-field-type';

export const DOCUMENTS_ROUTES: Routes = [
  {
    path: '',
    // The field-type designer/control/search/view components <ff-flex-field-*> dispatches to, registered
    // on this route so they load with it: a host that lazy-loads DOCUMENTS_ROUTES keeps flex-fields and the
    // CKEditor adapter out of its initial bundle (0.5.0-preview.8 registered them in provideExtract(), at the
    // root, which grew a lazy-loading host's initial bundle by ~26%). Not optional, so not left to the host:
    // the server offers CKEditor and Tags for every new field, and a type with no registered components
    // renders empty, silently. provideFlexFields() supplies the kernel built-ins; Tree/Matrix are never
    // offered here, so registering them only means they would render if ever encountered.
    // FieldTypeResolver is listed too: it is providedIn 'root' and reads FLEX_FIELD_TYPES once, when it is
    // built, so the root instance would never see types registered here. Providing it on this route builds
    // this route's own instance, which does. The flip side: a field type a host registers anywhere above
    // this route - at the root, or on a route wrapping this one - is not visible on these pages, because
    // this route's multi-provider set replaces its parents' rather than extending them. No host adds one
    // today; the server-side registry decides which types exist, and a new one belongs in this list.
    providers: [provideFlexFields(), provideCKEditorFieldType(), provideTagsFieldType(), FieldTypeResolver],
    children: [
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
        // neither can be expressed as a route policy at all; guarding on the role-level `Documents.Delete` (which
        // covers restore since #645) would keep both personas out of the page entirely. The server is the authority: admission to the recycle-bin query is
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
    ],
  },
];
