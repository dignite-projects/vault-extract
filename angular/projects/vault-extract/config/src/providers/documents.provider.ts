import { ConfigStateService, eLayoutType, RoutesService } from '@abp/ng.core';
import {
  DestroyRef,
  EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EXTRACT_FEATURES, EXTRACT_PERMISSIONS, isExtractFeatureDisabled } from '@dignite/ng.vault-extract';
import { distinctUntilChanged, map } from 'rxjs';

// The parent entry. Its children name it as their `parentName`, and hiding it is what hides the menu.
const DOCUMENTS_MENU = '::Menu:Documents';

/**
 * Adds the Documents menu, and takes it out of sight while the tenant does not have the
 * `VaultExtract.Enable` feature. The field types the document pages need are not registered here:
 * DOCUMENTS_ROUTES registers them on its own route, so they load with it and stay out of the host's initial
 * bundle.
 */
export function provideExtract(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideAppInitializer(() => {
      const routes = inject(RoutesService);
      const configState = inject(ConfigStateService);
      const destroyRef = inject(DestroyRef);
      routes.add([
        {
          path: '/documents',
          name: DOCUMENTS_MENU,
          iconClass: 'fas fa-file-alt',
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 2,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/overview',
          name: '::Menu:DocumentOverview',
          iconClass: 'fas fa-chart-bar',
          parentName: DOCUMENTS_MENU,
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 0,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/list',
          name: '::Menu:DocumentList',
          iconClass: 'fas fa-list',
          parentName: DOCUMENTS_MENU,
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 1,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/types',
          name: '::Menu:DocumentTypes',
          iconClass: 'fas fa-tags',
          parentName: DOCUMENTS_MENU,
          requiredPolicy: EXTRACT_PERMISSIONS.DocumentTypes.Default,
          order: 3,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/cabinets',
          name: '::Menu:Cabinets',
          iconClass: 'fas fa-folder',
          parentName: DOCUMENTS_MENU,
          requiredPolicy: EXTRACT_PERMISSIONS.Cabinets.Default,
          order: 5,
          layout: eLayoutType.application,
        },
        {
          path: '/documents/recycle',
          name: '::Menu:DocumentRecycleBin',
          iconClass: 'fas fa-trash-can',
          parentName: DOCUMENTS_MENU,
          // #632/#645: must match the route guard, for the same reason — the page is for anyone who may
          // restore something, and neither a per-type Delete grant nor ownership is a policy name, so a menu
          // entry on a role-level permission would leave those callers no way into a page they may use. The
          // page always asks the server, and each row carries its own restore right.
          requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
          order: 6,
          layout: eLayoutType.application,
        },
      ]);

      // A stream and not a read at startup, for two reasons: the initial configuration may not have arrived
      // when this initializer runs, and it is fetched again on sign-in, sign-out and tenant switch, where the
      // feature can change. ABP's own `requiredPolicy` filtering re-runs on every update the same way. Hiding
      // the parent is enough: the visible tree is built from the items that pass the filter, and an item whose
      // parent did not pass is dropped with it (documents.provider.spec.ts pins that).
      //
      // Not covered: `RoutesService.add` replaces an item of the same name wholesale and drops `invisible`, so a
      // host that registers this parent entry again after the menu was hidden shows it until the feature value
      // changes. A host that customizes the entry should `patch` it, which keeps the flag.
      configState
        .getFeature$(EXTRACT_FEATURES.Enable)
        .pipe(map(isExtractFeatureDisabled), distinctUntilChanged(), takeUntilDestroyed(destroyRef))
        .subscribe(invisible => routes.patch(DOCUMENTS_MENU, { invisible }));
    }),
  ]);
}
