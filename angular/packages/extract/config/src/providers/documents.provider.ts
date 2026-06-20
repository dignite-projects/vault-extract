import { eLayoutType, RoutesService } from '@abp/ng.core';
import {
  EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import { EXTRACT_PERMISSIONS } from '@dignite/extract';

export function provideExtract(): EnvironmentProviders {
  return makeEnvironmentProviders([
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
          path: '/documents/review',
          name: '::Menu:DocumentReviewQueue',
          iconClass: 'fas fa-clipboard-check',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
          order: 2,
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
          path: '/documents/export-templates',
          name: '::Menu:ExportTemplates',
          iconClass: 'fas fa-file-export',
          parentName: '::Menu:Documents',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Templates.Default,
          order: 4,
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
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Restore,
          order: 6,
          layout: eLayoutType.application,
        },
      ]);
    }),
  ]);
}
