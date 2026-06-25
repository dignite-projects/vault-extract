import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  Input,
  OnInit,
  Output,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { LocalizationPipe } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentReprocessingService,
  FieldReextractionPreviewDto,
} from '@dignite/vault-extract';

/**
 * Bulk field re-extraction preview and trigger modal (#289 scenario 2): leaf operation with a light
 * warning.
 * Fetches the preview on open, including affected document count and the current field list for the
 * type. Confirmation enqueues the dispatcher and immediately reports that work has moved to the
 * background.
 * This component owns preview, submit, and toaster behavior; the parent only controls open/close through
 * [documentTypeId] and (closed).
 */
@Component({
  selector: 'lib-field-reextraction-modal',
  templateUrl: './field-reextraction-modal.component.html',
  standalone: true,
  imports: [CommonModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FieldReextractionModalComponent implements OnInit {
  private readonly service = inject(DocumentReprocessingService);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);

  @Input({ required: true }) documentTypeId!: string;
  @Input() documentTypeDisplayName = '';

  @Output() closed = new EventEmitter<void>();

  readonly preview = signal<FieldReextractionPreviewDto | null>(null);
  readonly isLoadingPreview = signal(true);
  readonly previewFailed = signal(false);
  readonly isSubmitting = signal(false);

  ngOnInit(): void {
    this.loadPreview();
  }

  loadPreview(): void {
    this.isLoadingPreview.set(true);
    this.previewFailed.set(false);
    this.service
      .previewFieldExtraction(this.documentTypeId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => {
          this.preview.set(dto);
          this.isLoadingPreview.set(false);
        },
        error: () => {
          this.previewFailed.set(true);
          this.isLoadingPreview.set(false);
        },
      });
  }

  confirm(): void {
    if (this.isSubmitting()) return;
    this.isSubmitting.set(true);
    this.service
      .startFieldExtraction({ documentTypeId: this.documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.toaster.success('::Document:Reprocess:FieldExtraction:Queued', '::Success');
          this.close();
        },
        error: () => {
          this.isSubmitting.set(false);
          this.toaster.error('::Document:Reprocess:Failed', '::Error');
        },
      });
  }

  close(): void {
    this.closed.emit();
  }

  // Backdrop close guard: close only when both mousedown and click land on the backdrop itself, matching
  // document-type-list.
  private backdropMouseDownOnSelf = false;
  onBackdropMouseDown(event: MouseEvent): void {
    this.backdropMouseDownOnSelf = event.target === event.currentTarget;
  }
  onBackdropClick(event: MouseEvent): void {
    if (this.backdropMouseDownOnSelf && event.target === event.currentTarget && !this.isSubmitting()) {
      this.close();
    }
    this.backdropMouseDownOnSelf = false;
  }
}
