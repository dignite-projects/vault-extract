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
import { escapeHtmlChars, ListService, LocalizationPipe, PermissionService } from '@abp/ng.core';
import type { ABP } from '@abp/ng.core';
import {
  EntityProp,
  EXTENSIONS_IDENTIFIER,
  ExtensionsService,
  ExtensibleTableComponent,
  ePropType,
} from '@abp/ng.components/extensible';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { NgbDropdownModule } from '@ng-bootstrap/ng-bootstrap';
import { of } from 'rxjs';
import {
  CabinetDto,
  CabinetService,
  CreateCabinetDto,
  EXTRACT_PERMISSIONS,
} from '@dignite/vault-extract';
import {
  ClientPagedResult,
  configureEntityTable,
  pageClientItems,
  EXTRACT_TABLES,
  SortAccessors,
} from '../../shared/extensible-table';

// Mirrors CabinetConsts (Domain.Shared): Name / Description length caps.
const MAX_NAME_LENGTH = 128;
const MAX_DESCRIPTION_LENGTH = 512;

const CABINET_SORTS: SortAccessors<CabinetDto> = {
  name: cabinet => cabinet.name,
};

@Component({
  selector: 'lib-cabinet-list',
  templateUrl: './cabinet-list.component.html',
  styleUrls: ['./cabinet-list.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe, ExtensibleTableComponent, NgbDropdownModule],
  providers: [
    ListService,
    {
      provide: EXTENSIONS_IDENTIFIER,
      useValue: EXTRACT_TABLES.Cabinets,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CabinetListComponent implements OnInit {
  private readonly service = inject(CabinetService);
  private readonly fb = inject(FormBuilder);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly extensions = inject(ExtensionsService);

  readonly list = inject(ListService);

  readonly canCreate = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Create);
  readonly canUpdate = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Update);
  readonly canDelete = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Delete);

  allCabinets = signal<CabinetDto[]>([]);
  cabinets = signal<ClientPagedResult<CabinetDto>>({ totalCount: 0, items: [] });
  isLoading = signal(true);

  // null = closed; 'create' / CabinetDto = open in the matching mode.
  editing = signal<CabinetDto | 'create' | null>(null);
  isSubmitting = signal(false);
  private tableQuery: Partial<ABP.PageQueryParams> = {};

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(MAX_NAME_LENGTH)]],
    description: ['', [Validators.maxLength(MAX_DESCRIPTION_LENGTH)]],
  });

  constructor() {
    configureEntityTable<CabinetDto>(this.extensions, EXTRACT_TABLES.Cabinets, [
      EntityProp.create<CabinetDto>({
        type: ePropType.String,
        name: 'name',
        displayName: '::Cabinet:Name',
        sortable: true,
        valueResolver: data => {
          const name = escapeHtmlChars(data.record.name);
          const description = data.record.description
            ? `<div class="small text-muted fw-normal mt-1">${escapeHtmlChars(data.record.description)}</div>`
            : '';
          return of(`<span class="fw-semibold"><i class="fas fa-folder text-warning me-2"></i>${name}</span>${description}`);
        },
      }),
    ]);
  }

  ngOnInit(): void {
    this.hookTableQuery();
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
          this.allCabinets.set(list);
          this.list.totalCount = list.length;
          this.applyTableQuery();
          this.isLoading.set(false);
        },
        error: () => {
          this.allCabinets.set([]);
          this.cabinets.set({ totalCount: 0, items: [] });
          this.list.totalCount = 0;
          this.isLoading.set(false);
        },
      });
  }

  private hookTableQuery(): void {
    this.list.query$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(query => this.applyTableQuery(query));
  }

  private applyTableQuery(query: Partial<ABP.PageQueryParams> = this.tableQuery): void {
    this.tableQuery = query;
    this.cabinets.set(pageClientItems(this.allCabinets(), query, CABINET_SORTS));
  }

  openCreate(): void {
    this.form.reset({ name: '', description: '' });
    this.editing.set('create');
  }

  openEdit(cabinet: CabinetDto): void {
    this.form.reset({ name: cabinet.name, description: cabinet.description ?? '' });
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
    // Normalize blank descriptions to undefined; backend ValidateDescription also treats blanks as
    // "no description".
    const description = raw.description.trim() || undefined;

    if (mode === 'create') {
      const input: CreateCabinetDto = { name: raw.name, description };
      this.service.create(input)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: () => this.onSaved('::Cabinet:CreatedSuccessfully'),
          error: () => this.isSubmitting.set(false),
        });
    } else {
      this.service.update(mode.id!, { name: raw.name, description })
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
        this.service.delete(cabinet.id!)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.toaster.success('::Cabinet:DeletedSuccessfully', '::Success');
              this.load();
            },
            error: () => this.toaster.error('::Cabinet:DeleteFailed', '::Error'),
          });
      });
  }
}
