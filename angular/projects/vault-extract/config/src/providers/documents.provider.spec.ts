import {
  AbpApplicationConfigurationService,
  AbpApplicationLocalizationService,
  ConfigStateService,
  EnvironmentService,
  OTHERS_GROUP,
  RoutesService,
  SORT_COMPARE_FUNC,
} from '@abp/ng.core';
import { TestBed } from '@angular/core/testing';
import { EXTRACT_FEATURES, EXTRACT_PERMISSIONS, isExtractFeatureDisabled } from '@dignite/ng.vault-extract';
import { provideExtract } from './documents.provider';

const DOCUMENTS_MENU = '::Menu:Documents';

// The permissions a signed-in operator needs to see every entry of the menu, so that a missing entry can only
// be the feature, not a policy.
const GRANTED_POLICIES = {
  [EXTRACT_PERMISSIONS.Documents.Default]: true,
  [EXTRACT_PERMISSIONS.DocumentTypes.Default]: true,
  [EXTRACT_PERMISSIONS.Cabinets.Default]: true,
};

function configure(featureValue: string | undefined): void {
  TestBed.inject(ConfigStateService).setState({
    auth: { grantedPolicies: GRANTED_POLICIES },
    features: featureValue === undefined ? { values: {} } : { values: { [EXTRACT_FEATURES.Enable]: featureValue } },
  } as never);
}

describe('provideExtract menu', () => {
  let routes: RoutesService;

  beforeEach(() => {
    // The real RoutesService, ConfigStateService and PermissionService, with only what they would reach the
    // network or the theme for stubbed: the configuration is set by hand below, never fetched.
    TestBed.configureTestingModule({
      providers: [
        provideExtract(),
        { provide: AbpApplicationConfigurationService, useValue: {} },
        { provide: AbpApplicationLocalizationService, useValue: {} },
        { provide: EnvironmentService, useValue: { getEnvironment: () => ({}) } },
        { provide: OTHERS_GROUP, useValue: 'Others' },
        {
          provide: SORT_COMPARE_FUNC,
          useValue: (a: { order?: number }, b: { order?: number }) => (a.order ?? 0) - (b.order ?? 0),
        },
      ],
    });
    routes = TestBed.inject(RoutesService);
  });

  function visibleMenu(): { name: string; children: string[] } | undefined {
    const documents = routes.visible.find(node => node.name === DOCUMENTS_MENU);
    return documents && { name: documents.name, children: (documents.children ?? []).map(child => child.name) };
  }

  it('shows the Documents menu with its entries while the feature is enabled', () => {
    configure('true');

    expect(visibleMenu()?.children).toEqual([
      '::Menu:DocumentOverview',
      '::Menu:DocumentList',
      '::Menu:DocumentTypes',
      '::Menu:Cabinets',
      '::Menu:DocumentRecycleBin',
    ]);
  });

  it('takes the whole Documents menu out of sight when the feature is switched off', () => {
    configure('false');

    expect(visibleMenu()).toBeUndefined();
    // Only the parent is patched. This is what says its children go with it, rather than turning up at the top
    // level of the menu: ABP drops an item whose parent did not pass the visibility filter.
    expect(routes.visible.map(node => node.name)).toEqual([]);
  });

  it('keeps the menu when the configuration carries no value for the feature', () => {
    // An older server that does not define the feature, or a configuration without `features`.
    configure(undefined);

    expect(visibleMenu()).toBeDefined();
  });

  it('follows the feature when the configuration is refreshed', () => {
    configure('false');
    expect(visibleMenu()).toBeUndefined();

    // A tenant switch or a sign-in fetches application-configuration again.
    configure('true');
    expect(visibleMenu()).toBeDefined();

    configure('false');
    expect(visibleMenu()).toBeUndefined();
  });

  it('keeps the menu hidden when its parent entry is registered again', () => {
    configure('false');
    expect(visibleMenu()).toBeUndefined();

    // `RoutesService.add` replaces an item of the same name wholesale, which drops the flag; the feature value
    // has not changed, so nothing but the route list can bring the hiding back.
    routes.add([
      {
        path: '/documents',
        name: DOCUMENTS_MENU,
        requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default,
        order: 2,
      },
    ]);

    expect(visibleMenu()).toBeUndefined();
  });

  it('leaves the other entries of the host alone', () => {
    routes.add([{ path: '/', name: '::Menu:Home', order: 1 }]);
    configure('false');

    expect(routes.visible.map(node => node.name)).toEqual(['::Menu:Home']);
  });
});

describe('isExtractFeatureDisabled', () => {
  it('only reads an explicit false as off', () => {
    expect(isExtractFeatureDisabled('false')).toBe(true);
    expect(isExtractFeatureDisabled('False')).toBe(true);
    expect(isExtractFeatureDisabled(' false ')).toBe(true);

    expect(isExtractFeatureDisabled('true')).toBe(false);
    expect(isExtractFeatureDisabled('')).toBe(false);
    expect(isExtractFeatureDisabled(undefined)).toBe(false);
  });
});
