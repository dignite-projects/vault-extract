import { EnvironmentProviders } from '@angular/core';
import { provideFlexFieldTypes } from '@dignite/ng.flex-fields';
import { TAGS_FIELD_TYPE } from './tags-field-type';

/**
 * Registers the `Tags` field type. `provideExtract()` (from `@dignite/ng.vault-extract/config`) already
 * calls this, together with `provideFlexFields()` and `provideCKEditorFieldType()`, so a host that calls
 * `provideExtract()` needs nothing else.
 */
export function provideTagsFieldType(): EnvironmentProviders {
  return provideFlexFieldTypes(TAGS_FIELD_TYPE);
}
