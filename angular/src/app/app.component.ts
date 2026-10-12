import { Component, ChangeDetectionStrategy } from '@angular/core';
import { DynamicLayoutComponent } from '@abp/ng.core';
import { LoaderBarComponent } from '@abp/ng.theme.shared';


@Component({
  selector: 'app-root',
  template: `
    <abp-loader-bar />
    <abp-dynamic-layout />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LoaderBarComponent, DynamicLayoutComponent],
})
export class AppComponent {}
