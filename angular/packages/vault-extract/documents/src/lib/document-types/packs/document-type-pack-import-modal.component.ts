import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { LocalizationPipe } from '@abp/ng.core';
import {
  DocumentTypePackDto,
  DocumentTypePackImportResultDto,
  DocumentTypePackService,
  PackImportMode,
  PackItemAction,
} from '@dignite/vault-extract';
import { MAX_PACKS_PER_IMPORT, PackParseError, parsePackFileText } from './pack-io';

/**
 * Import a document-type config pack (#444). Self-contained Pattern B modal: it owns file selection, local
 * shape validation (via `parsePackFileText`), the reconciliation-mode choice, the import call, and the
 * result view. The parent only toggles it open and reloads its list on `imported`.
 *
 * Layer-aware by construction: the server applies packs to the caller's current layer (Host or tenant), so
 * there is nothing layer-related to choose here — only whether existing config is updated or left alone.
 */
@Component({
  selector: 'lib-document-type-pack-import-modal',
  templateUrl: './document-type-pack-import-modal.component.html',
  standalone: true,
  imports: [CommonModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentTypePackImportModalComponent {
  private readonly service = inject(DocumentTypePackService);
  private readonly destroyRef = inject(DestroyRef);

  /** Dismissed — via Cancel, backdrop, or Done after viewing the result. */
  @Output() closed = new EventEmitter<void>();
  /** Fired once an import succeeds, so the parent reloads the type list. */
  @Output() imported = new EventEmitter<void>();

  // Enum + const handles for the template.
  readonly PackImportMode = PackImportMode;
  readonly PackItemAction = PackItemAction;
  // String form because it is only ever an `abpLocalization` interpolation arg (the pipe takes strings).
  readonly maxPacks = String(MAX_PACKS_PER_IMPORT);

  readonly fileName = signal('');
  readonly packs = signal<DocumentTypePackDto[] | null>(null);
  readonly parseError = signal<PackParseError | null>(null);
  readonly mode = signal<PackImportMode>(PackImportMode.CreateOrUpdate);
  readonly isSubmitting = signal(false);
  readonly result = signal<DocumentTypePackImportResultDto | null>(null);

  readonly typeCount = computed(() => this.packs()?.length ?? 0);
  readonly canImport = computed(() => this.typeCount() > 0 && !this.isSubmitting());

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Clear the input so re-picking the same file after a parse error still fires (change) again.
    input.value = '';

    this.result.set(null);
    this.parseError.set(null);
    this.packs.set(null);
    if (!file) {
      this.fileName.set('');
      return;
    }

    this.fileName.set(file.name);
    file
      .text()
      .then(text => this.applyParsed(text))
      .catch(() => {
        this.parseError.set('invalid-json');
        this.packs.set(null);
      });
  }

  private applyParsed(text: string): void {
    const { packs, error } = parsePackFileText(text);
    if (error) {
      this.parseError.set(error);
      this.packs.set(null);
    } else {
      this.parseError.set(null);
      this.packs.set(packs ?? null);
    }
  }

  setMode(mode: PackImportMode): void {
    this.mode.set(mode);
  }

  fieldCount(pack: DocumentTypePackDto): number {
    return pack.fields?.length ?? 0;
  }

  submit(): void {
    const packs = this.packs();
    if (!packs || packs.length === 0 || this.isSubmitting()) {
      return;
    }
    this.isSubmitting.set(true);
    this.service
      .import({ packs, mode: this.mode() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.isSubmitting.set(false);
          this.result.set(result);
          // The list underneath is stale now regardless of how the operator dismisses the modal.
          this.imported.emit();
        },
        // ABP's global HTTP error interceptor surfaces the server's localized message (unsupported version,
        // name collision, data-safety guard). Just release the button.
        error: () => this.isSubmitting.set(false),
      });
  }

  close(): void {
    if (this.isSubmitting()) return;
    this.closed.emit();
  }

  // Backdrop close guard, matching document-type-list and the reprocessing modals.
  private backdropMouseDownOnSelf = false;
  onBackdropMouseDown(event: MouseEvent): void {
    this.backdropMouseDownOnSelf = event.target === event.currentTarget;
  }
  onBackdropClick(event: MouseEvent): void {
    if (this.backdropMouseDownOnSelf && event.target === event.currentTarget) {
      this.close();
    }
    this.backdropMouseDownOnSelf = false;
  }
}
