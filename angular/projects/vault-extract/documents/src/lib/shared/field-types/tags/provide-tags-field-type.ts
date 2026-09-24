import { EnvironmentProviders } from '@angular/core';
import { provideFlexFieldTypes } from '@dignite/ng.flex-fields';
import { TAGS_FIELD_TYPE } from './tags-field-type';

/**
 * Registers the `Tags` field type. `DOCUMENTS_ROUTES` already provides it on its own route, together with
 * `provideFlexFields()`, `provideCKEditorFieldType()` and a route-level `FieldTypeResolver`, so a host that
 * mounts `DOCUMENTS_ROUTES` needs nothing else.
 */
export function provideTagsFieldType(): EnvironmentProviders {
  return provideFlexFieldTypes(TAGS_FIELD_TYPE);
}
