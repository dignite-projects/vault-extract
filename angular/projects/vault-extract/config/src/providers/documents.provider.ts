import { eLayoutType, RoutesService } from '@abp/ng.core';
import {
  EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import { provideFlexFields } from '@dignite/ng.flex-fields';
import { provideCKEditorFieldType } from '@dignite/ng.flex-fields-ckeditor';
import { EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import { provideTagsFieldType } from '@dignite/ng.vault-extract/documents';

export function provideExtract(): EnvironmentProviders {
  return makeEnvironmentProviders([
    // The field-type designer/control/search/view components <ff-flex-field-*> dispatches to. Not
    // optional, so not left to the host: the server offers CKEditor and Tags for every new field
    // (IVaultExtractFieldTypeRegistry), and a type with no registered components makes the extracted-
    // fields form render empty, silently. provideFlexFields() supplies the kernel built-ins (Text/
    // Number/Boolean/DateTime/Select/Tree/Matrix/Table, #625); Tree/Matrix are never offered here, so
    // registering them only means they would render if ever encountered. FieldTypeResolver is root-
    // provided and reads the registry once, when first injected, so this has to be application-config
    // level - which is where provideExtract() is called - never from inside the lazy-loaded routes.
    // FLEX_FIELD_TYPES is keyed by type name, so a host that still registers any of these itself
    // registers the same component again, harmlessly.
    provideFlexFields(),
    provideCKEditorFieldType(),
    provideTagsFieldType(),
    provideAppInitializer(() => {
      const routes = inject(RoutesService);
      routes.add([
        {
          path: '/documents',
          name: '::Menu:Documents',
          iconClass: 'fas fa-file-alt',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 2,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/overview',
          name: '::Menu:DocumentOverview',
          iconClass: 'fas fa-chart-bar',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 0,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/list',
          name: '::Menu:DocumentList',
          iconClass: 'fas fa-list',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 1,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/types',
          name: '::Menu:DocumentTypes',
          iconClass: 'fas fa-tags',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.DocumentTypes.Default,
          order: 3,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/cabinets',
          name: '::Menu:Cabinets',
          iconClass: 'fas fa-folder',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.Cabinets.Default,
          order: 5,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/recycle',
          name: '::Menu:DocumentRecycleBin',
          iconClass: 'fas fa-trash-can',
          parentName: '::Menu:Documents',
          // #632/#645: must match the route guard, for the same reason — the page is for anyone who may
          // restore something, and neither a per-type Delete grant nor ownership is a policy name, so a menu
          // entry on a role-level permission would leave those callers no way into a page they may use. The
          // page always asks the server, and each row carries its own restore right.
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 6,
          layout: eLayoutType.application,
        },
      ]);
    }),
  ]);
}
