import {
  AuthService,
  ConfigStateService,
  LocalizationPipe,
  PermissionService,
} from '@abp/ng.core';
import type { CurrentTenantDto, CurrentUserDto } from '@abp/ng.core';
import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { EXTRACT_PERMISSIONS } from '@dignite/extract';

interface HomeEntryPoint {
  title: string;
  description: string;
  route: string;
  iconClass: string;
  toneClass: string;
  policies?: string[];
}

@Component({
  selector: 'app-home',
  templateUrl: './home.component.html',
  styleUrls: ['./home.component.scss'],
  imports: [CommonModule, RouterLink, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomeComponent {
  private readonly authService = inject(AuthService);
  private readonly configState = inject(ConfigStateService);
  private readonly permissionService = inject(PermissionService);

  private readonly entryPoints: HomeEntryPoint[] = [
    {
      title: 'Dignite Extract',
      description: 'Open the module for document intake, review queues, schema management, and exports.',
      route: '/documents',
      iconClass: 'fas fa-file-lines',
      toneClass: 'text-bg-primary',
      policies: [EXTRACT_PERMISSIONS.Documents.Default],
    },
    {
      title: 'Users',
      description: 'Manage user accounts and access for this host application.',
      route: '/identity/users',
      iconClass: 'fas fa-users',
      toneClass: 'text-bg-info',
      policies: ['AbpIdentity.Users'],
    },
    {
      title: 'Roles',
      description: 'Review role assignments and permission sets for operators and administrators.',
      route: '/identity/roles',
      iconClass: 'fas fa-user-shield',
      toneClass: 'text-bg-secondary',
      policies: ['AbpIdentity.Roles'],
    },
    {
      title: 'Settings',
      description: 'Adjust host-level settings that are exposed through ABP setting management.',
      route: '/setting-management',
      iconClass: 'fas fa-sliders',
      toneClass: 'text-bg-warning',
      policies: ['SettingManagement.Emailing', 'SettingManagement.TimeZone'],
    },
    {
      title: 'My account',
      description: 'Update your profile, password, and personal account preferences.',
      route: '/account/manage',
      iconClass: 'fas fa-user-gear',
      toneClass: 'text-bg-dark',
    },
  ];

  readonly currentUser: CurrentUserDto = this.configState.getOne('currentUser');
  readonly currentTenant: CurrentTenantDto = this.configState.getOne('currentTenant');

  get hasLoggedIn(): boolean {
    return this.authService.isAuthenticated;
  }

  get displayName(): string {
    return this.currentUser.name || this.currentUser.userName || this.currentUser.email || 'Operator';
  }

  get userInitials(): string {
    const nameParts = [this.currentUser.name, this.currentUser.surName]
      .filter(Boolean)
      .join(' ')
      .trim();
    const source = nameParts || this.currentUser.userName || this.currentUser.email || 'Operator';

    return source
      .split(/\s+|[.@_-]/)
      .filter(Boolean)
      .slice(0, 2)
      .map(part => part.charAt(0).toUpperCase())
      .join('');
  }

  get tenantName(): string {
    return this.currentTenant.name || 'Host';
  }

  get visibleEntryPoints(): HomeEntryPoint[] {
    return this.entryPoints.filter(entry => this.isEntryVisible(entry));
  }

  login(): void {
    this.authService.navigateToLogin();
  }

  private isEntryVisible(entry: HomeEntryPoint): boolean {
    if (!entry.policies?.length) {
      return true;
    }

    return entry.policies.some(policy => this.permissionService.getGrantedPolicy(policy));
  }
}
