import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AbstractControl, FormControl, ReactiveFormsModule, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { NzSelectModule } from 'ng-zorro-antd/select';
import { FieldTypeControlBase, FlexFieldsStyleLoader, NZ_SELECT_STYLE } from '@dignite/ng.flex-fields';
import { TagsConfiguration } from './tags-configuration';

/**
 * Edits the value of a `Tags` field — a list of short free-form strings.
 *
 * Rendered as `nz-select` in Ant Design's `tags` mode: typed text becomes a removable chip inline in
 * the same box, the input-and-display-in-one-control pattern the flex-fields kernel's own `Select`
 * control already renders with (also an `nz-select`, just closed-vocabulary). `MaxCount` is enforced
 * live via `nzMaxMultipleCount`; both `MaxCount` and `MaxLength` are re-checked as form validators —
 * a client convenience only, since the server's `FlexFieldValueReader` remains the actual gate and
 * rejects the whole array rather than truncating it, so a value that slipped past this UI still
 * fails loudly there.
 */
@Component({
  selector: 'lib-tags-control',
  templateUrl: './tags-control.component.html',
  styleUrls: ['./tags-control.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, NzSelectModule],
})
export class TagsControlComponent extends FieldTypeControlBase implements OnInit {
  private readonly styleLoader = inject(FlexFieldsStyleLoader);

  protected readonly tokenSeparators = [','];

  /**
   * Fetches `ng-zorro-antd-select.css` by bundle name, the way the kernel's own `Select` control does:
   * the host declares that bundle with `inject: false`, so nothing else puts it on the page. Same
   * loader as flex-fields, so it is requested once however many selects render.
   */
  ngOnInit(): void {
    this.styleLoader.load(NZ_SELECT_STYLE);
  }

  protected get valueControl(): FormControl<string[]> {
    return this.fieldControl as FormControl<string[]>;
  }

  protected get maxCount(): number {
    return Number(this.fieldValue?.field.configuration['Tags.MaxCount'] ?? 100);
  }

  protected get maxLength(): number {
    return Number(this.fieldValue?.field.configuration['Tags.MaxLength'] ?? 256);
  }

  protected get placeholder(): string {
    const value = this.fieldValue?.field.configuration['Tags.Placeholder'];
    return typeof value === 'string' ? value : '';
  }

  protected configurationDefaults(): object {
    return new TagsConfiguration();
  }

  protected createControl(): AbstractControl {
    const validators = [
      ...(this.fieldValue!.required ? [Validators.required] : []),
      tagsMaxCountValidator(this.maxCount),
      tagsMaxLengthValidator(this.maxLength),
    ];
    return this.fb.control<string[]>(
      Array.isArray(this.selectedValue) ? (this.selectedValue as string[]) : [],
      validators,
    );
  }
}

/** Rejects the whole value once it carries more tags than the field allows. */
function tagsMaxCountValidator(maxCount: number): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const tags = (control.value as string[] | null) ?? [];
    return tags.length > maxCount ? { tagsMaxCount: { maxCount, actual: tags.length } } : null;
  };
}

/** Rejects the whole value if any single tag exceeds the configured length. */
function tagsMaxLengthValidator(maxLength: number): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const tags = (control.value as string[] | null) ?? [];
    return tags.some(tag => tag.length > maxLength) ? { tagsMaxLength: { maxLength } } : null;
  };
}
