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
import { LocalizationPipe } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentReprocessingService,
  ReclassificationScope,
  ReclassificationScopeInput,
} from '@dignite/paperbase';

/**
 * 批量「重新分类」预览 + 触发模态（#289 场景一）——级联 + 破坏性、重警告。
 * 范围由人按意图选（仅该类型 / 全量·跨类型 / 待审核队列），系统不预设默认；默认保护人工确认
 * （覆盖人工成果须显式 opt-in）。范围 / 开关变化即时刷新受影响文档数预览。
 *
 * 从某文档类型菜单打开时传 [documentTypeId]，默认范围为「仅该类型」；可切到全量 / 待审核队列。
 * 无类型上下文（全局入口）打开时「仅该类型」不可选，默认「全量·跨类型」。
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

  // 待审核队列范围下「保护人工确认」开关无意义（待审文档本就未确认）。
  get includeToggleApplies(): boolean {
    return this.form.controls.scope.value !== ReclassificationScope.PendingReviewQueue;
  }

  ngOnInit(): void {
    // 无类型上下文：默认「全量·跨类型」（「仅该类型」不可选）。
    this.form.controls.scope.setValue(
      this.hasType ? ReclassificationScope.OnlyCurrentType : ReclassificationScope.AllDocuments,
      { emitEvent: false },
    );

    this.form.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refreshPreview());

    this.refreshPreview();
  }

  private buildInput(): ReclassificationScopeInput {
    const raw = this.form.getRawValue();
    return {
      scope: Number(raw.scope) as ReclassificationScope,
      documentTypeId: Number(raw.scope) === ReclassificationScope.OnlyCurrentType ? this.documentTypeId : undefined,
      includeManuallyConfirmed: raw.includeManuallyConfirmed,
    };
  }

  refreshPreview(): void {
    this.isLoadingPreview.set(true);
    this.previewFailed.set(false);
    this.service
      .previewReclassification(this.buildInput())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => {
          this.documentCount.set(dto.documentCount);
          this.isLoadingPreview.set(false);
        },
        error: () => {
          this.documentCount.set(null);
          this.previewFailed.set(true);
          this.isLoadingPreview.set(false);
        },
      });
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
