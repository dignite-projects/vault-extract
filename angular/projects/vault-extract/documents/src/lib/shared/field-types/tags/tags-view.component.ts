import { Component, Input, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CoreModule } from '@abp/ng.core';
import type { FlexFieldValue } from '@dignite/ng.flex-fields';

/** Displays the value of a `Tags` field read-only, as a comma join in a list cell or as chips in the detail view. */
@Component({
  selector: 'lib-tags-view',
  templateUrl: './tags-view.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, CoreModule],
})
export class TagsViewComponent {
  /** Renders bare, without the label wrapper, for use inside a table cell. */
  @Input() showInList = false;

  @Input() fields?: FlexFieldValue;

  /** Registration key of the field type, e.g. `Tags`. */
  @Input() type?: string;

  @Input() value: unknown = '';

  protected get tags(): string[] {
    return Array.isArray(this.value) ? (this.value as string[]) : [];
  }
}
