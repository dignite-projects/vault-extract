import { Component, ChangeDetectionStrategy } from '@angular/core';
import { CoreModule } from '@abp/ng.core';
import { ReactiveFormsModule } from '@angular/forms';
import { FieldTypeConfigBase } from '@dignite/ng.flex-fields';
import { TagsConfiguration } from './tags-configuration';

/** Designer-side editor for a `Tags` field's configuration: how many values, how long each may be. */
@Component({
  selector: 'lib-tags-config',
  templateUrl: './tags-config.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CoreModule, ReactiveFormsModule],
})
export class TagsConfigComponent extends FieldTypeConfigBase {
  protected configurationDefaults(): object {
    return new TagsConfiguration();
  }
}
