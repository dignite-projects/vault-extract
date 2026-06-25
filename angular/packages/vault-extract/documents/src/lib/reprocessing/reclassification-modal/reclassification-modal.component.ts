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
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { EMPTY, Subject, catchError, switchMap } from 'rxjs';
import { LocalizationPipe } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentReprocessingService,
  ReclassificationScope,
  ReclassificationScopeInput,
} from '@dignite/vault-extract';

/**
 * Bulk reclassification preview and trigger modal (#289 scenario 1): cascading, destructive, and
 * strongly warned.
 * Humans choose the scope by intent (only this type / all documents across types / pending review
 * queue); the system does not prescribe a default. Manual confirmations are protected by default, and
 * overwriting manual results requires an explicit opt-in. Scope and toggle changes refresh the affected
 * document count preview immediately.
 *
 * When opened from a document type menu, [documentTypeId] is passed and the default scope is only that
 * type; users can switch to all documents or the pending review queue.
 * When opened without type context, such as a global entry, only-this-type is unavailable and the
 * default is all documents across types.
 */
@Component({
  selector: 'lib-reclassification-modal',
  templateUrl: './reclassification-modal.component.html',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReclassificationModalComponent implements OnInit {
  private readonly service = inject(DocumentReprocessingService);
  private readonly toaster = inject(ToasterService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  @Input() documentTypeId?: string;
  @Input() documentTypeDisplayName = '';

  @Output() closed = new EventEmitter<void>();

  readonly Scope = ReclassificationScope;

  readonly documentCount = signal<number | null>(null);
  readonly isLoadingPreview = signal(false);
  readonly previewFailed = signal(false);
  readonly isSubmitting = signal(false);

  readonly form = this.fb.nonNullable.group({
    scope: ReclassificationScope.OnlyCurrentType,
    includeManuallyConfirmed: false,
  });

  get hasType(): boolean {
    return !!this.documentTypeId;
  }

  // The protect-manual-confirmations toggle is meaningless for the pending review queue, because pending
  // review documents are not confirmed yet.
  get includeToggleApplies(): boolean {
    return this.form.controls.scope.value !== ReclassificationScope.PendingReviewQueue;
  }

  private readonly previewTrigger = new Subject<void>();

  ngOnInit(): void {
    // No type context: default to all documents across types; only-this-type is unavailable.
    this.form.controls.scope.setValue(
      this.hasType ? ReclassificationScope.OnlyCurrentType : ReclassificationScope.AllDocuments,
      { emitEvent: false },
    );
    this.applyIncludeTogglePolicy();

    // switchMap cancels in-flight preview requests when scope or toggles change quickly, preventing
    // out-of-order responses from showing counts for stale scopes (review round 1).
    this.previewTrigger
      .pipe(
        switchMap(() => {
          this.isLoadingPreview.set(true);
          this.previewFailed.set(false);
          return this.service.previewReclassification(this.buildInput()).pipe(
            catchError(() => {
              this.documentCount.set(null);
              this.previewFailed.set(true);
              this.isLoadingPreview.set(false);
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(dto => {
        this.documentCount.set(dto.documentCount ?? null);
        this.isLoadingPreview.set(false);
      });

    this.form.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.applyIncludeTogglePolicy();
        this.previewTrigger.next();
      });

    this.previewTrigger.next();
  }

  // The protect-manual-confirmations toggle is meaningless for the pending review queue. Drive this with
  // reactive-forms disable/enable instead of template [disabled], avoiding Angular dev warnings on
  // formControlName [disabled] and keeping FormControl.disabled truly synchronized.
  private applyIncludeTogglePolicy(): void {
    const control = this.form.controls.includeManuallyConfirmed;
    if (this.includeToggleApplies) {
      if (control.disabled) control.enable({ emitEvent: false });
    } else if (control.enabled) {
      control.disable({ emitEvent: false });
    }
  }

  // Explicit retry after preview failure, aligned with the field re-extraction modal UX; switchMap
  // cancels any in-flight request.
  refreshPreview(): void {
    this.previewTrigger.next();
  }

  private buildInput(): ReclassificationScopeInput {
    const raw = this.form.getRawValue();
    return {
      scope: Number(raw.scope) as ReclassificationScope,
      documentTypeId: Number(raw.scope) === ReclassificationScope.OnlyCurrentType ? this.documentTypeId : undefined,
      includeManuallyConfirmed: raw.includeManuallyConfirmed,
    };
  }

  confirm(): void {
    if (this.isSubmitting()) return;
    this.isSubmitting.set(true);
    this.service
      .startReclassification(this.buildInput())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.toaster.success('::Document:Reprocess:Reclassification:Queued', '::Success');
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
