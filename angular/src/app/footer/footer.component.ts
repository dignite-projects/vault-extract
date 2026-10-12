import { Component, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'app-footer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="lpx-footbar-container end-0">
      <div class="lpx-footbar">
        <div class="lpx-footbar-copyright">
          <span>© {{ currentYear }} Dignite Vault Extract</span>
        </div>
        <div class="lpx-footbar-solo-links">
          <a href="https://dignite.com/vault-extract" target="_blank" rel="noopener">About</a>
        </div>
      </div>
    </div>
  `,
})
export class FooterComponent {
  currentYear = new Date().getFullYear();
}
