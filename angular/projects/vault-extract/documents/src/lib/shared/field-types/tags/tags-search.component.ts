import { Component, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AbstractControl, ReactiveFormsModule } from '@angular/forms';
import { FieldTypeControlBase } from '@dignite/ng.flex-fields';
import { TagsConfiguration } from './tags-configuration';

/**
 * Filters by a `Tags` field: does the document have this exact tag. `Tags.IndexValueType` is `String`
 * (the kernel decomposes each tag into its own index row, the same slot `Text` uses), so one text box
 * is the whole widget — the same reason `ff-text-search` needs no more than that.
 */
@Component({
  selector: 'lib-tags-search',
  templateUrl: './tags-search.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, ReactiveFormsModule],
})
export class TagsSearchComponent extends FieldTypeControlBase {
  protected configurationDefaults(): object {
    return new TagsConfiguration();
  }

  protected createControl(): AbstractControl {
    return this.fb.control(this.selectedValue);
  }
}
