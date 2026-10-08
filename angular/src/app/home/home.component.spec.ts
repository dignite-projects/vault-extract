import {
  AbpApplicationConfigurationService,
  AbpApplicationLocalizationService,
  AuthService,
  ConfigStateService,
  EnvironmentService,
} from '@abp/ng.core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EXTRACT_FEATURES, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';
import { HomeComponent } from './home.component';

const EXTRACT_ENTRY = 'Dignite Vault Extract';

function configure(featureValue: string | undefined): void {
  TestBed.inject(ConfigStateService).setState({
    auth: { grantedPolicies: { [EXTRACT_PERMISSIONS.Documents.Default]: true } },
    currentUser: { isAuthenticated: true, userName: 'admin' },
    currentTenant: {},
    features: { values: featureValue === undefined ? {} : { [EXTRACT_FEATURES.Enable]: featureValue } },
  } as never);
}

describe('HomeComponent entry points', () => {
  beforeEach(() => {
    // The real ConfigStateService and PermissionService; only what would reach the network is stubbed.
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated: true } },
        { provide: AbpApplicationConfigurationService, useValue: {} },
        { provide: AbpApplicationLocalizationService, useValue: {} },
        { provide: EnvironmentService, useValue: { getEnvironment: () => ({}) } },
      ],
    });
  });

  // Rendered, not read off the getter: the component is OnPush, so what a later configuration refresh does to the
  // page is a change-detection question, not a question about the value the getter would return.
  function render() {
    const fixture = TestBed.createComponent(HomeComponent);
    fixture.detectChanges();
    return fixture;
  }

  function entryTitles(fixture: ReturnType<typeof render>): string[] {
    const cards: HTMLElement[] = Array.from(fixture.nativeElement.querySelectorAll('.entry-card'));
    return cards.map(card => card.querySelector('.entry-title')?.textContent?.trim() ?? '');
  }

  it('offers Vault Extract while the feature is enabled', () => {
    configure('true');

    expect(entryTitles(render())).toContain(EXTRACT_ENTRY);
  });

  it('does not offer Vault Extract when the feature is off, whatever the caller is granted', () => {
    configure('false');

    const titles = entryTitles(render());
    expect(titles).not.toContain(EXTRACT_ENTRY);
    expect(titles).toContain('My account');
  });

  it('offers Vault Extract when the configuration carries no value for the feature', () => {
    configure(undefined);

    expect(entryTitles(render())).toContain(EXTRACT_ENTRY);
  });

  it('follows the feature when the configuration is fetched again while the page is open', () => {
    configure('true');
    const fixture = render();
    expect(entryTitles(fixture)).toContain(EXTRACT_ENTRY);

    // A sign-in or tenant switch refreshes application-configuration.
    configure('false');
    fixture.detectChanges();
    expect(entryTitles(fixture)).not.toContain(EXTRACT_ENTRY);

    configure('true');
    fixture.detectChanges();
    expect(entryTitles(fixture)).toContain(EXTRACT_ENTRY);
  });
});
