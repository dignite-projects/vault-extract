import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import {
  CabinetDto,
  CabinetService,
  CreateCabinetDto,
  PAPERBASE_PERMISSIONS,
} from '@dignite/paperbase';

// Mirrors CabinetConsts (Domain.Shared): DisplayName length cap.
const MAX_DISPLAY_NAME_LENGTH = 128;

@Component({
  selector: 'lib-cabinet-list',
  templateUrl: './cabinet-list.component.html',
  styleUrls: ['./cabinet-list.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CabinetListComponent implements OnInit {
  private readonly service = inject(CabinetService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canCreate = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Create);
  readonly canUpdate = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Update);
  readonly canDelete = this.permissionService.getGrantedPolicy(PAPERBASE_PERMISSIONS.Cabinets.Delete);

  cabinets = signal<CabinetDto[]>([]);
  isLoading = signal(true);

  // null = closed; 'create' / CabinetDto = open in the matching mode.
  editing = signal<CabinetDto | 'create' | null>(null);
  isSubmitting = signal(false);

  readonly form = this.fb.nonNullable.group({
    displayName: ['', [Validators.required, Validators.maxLength(MAX_DISPLAY_NAME_LENGTH)]],
  });

  ngOnInit(): void {
    this.load();
  }

  refresh(): void {
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.service.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.cabinets.set(list);
          this.isLoading.set(false);
        },
        error: () => this.isLoading.set(false),
      });
  }

  openCreate(): void {
    this.form.reset({ displayName: '' });
    this.editing.set('create');
  }

  openEdit(cabinet: CabinetDto): void {
    this.form.reset({ displayName: cabinet.displayName });
    this.editing.set(cabinet);
  }

  closeModal(): void {
    this.editing.set(null);
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const mode = this.editing();
    if (mode === null) return;

    this.isSubmitting.set(true);
    const raw = this.form.getRawValue();

    if (mode === 'create') {
      const input: CreateCabinetDto = { displayName: raw.displayName };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::Cabinet:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id, { displayName: raw.displayName })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::Cabinet:UpdatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    }
  }

  private onSaved(messageKey: string): void {
    this.isSubmitting.set(false);
    this.closeModal();
    this.toaster.success(messageKey, '::Success');
    this.load();
  }

  delete(cabinet: CabinetDto): void {
    this.confirmation
      .warn('::Cabinet:AreYouSureToDelete', '::AreYouSure')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(status => {
        if (status !== Confirmation.Status.confirm) return;
        this.service.delete(cabinet.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Cabinet:DeletedSuccessfully', '::Success');
              this.load();
            },
          });
      });
  }
}
