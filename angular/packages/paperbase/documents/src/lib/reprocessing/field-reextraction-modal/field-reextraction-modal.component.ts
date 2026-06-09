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
} from '@dignite/paperbase';

/**
 * 批量「字段重抽」预览 + 触发模态（#289 场景二）——叶子操作、轻警告。
 * 打开即拉预览（受影响文档数 + 该类型当前字段清单），确认后入队 dispatcher、立即「已进后台」。
 * 自身负责预览 / 提交 / toaster；父组件只控制开/关（[documentTypeId] + (closed)）。
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

  // 遮罩关闭防误触：mousedown 与 click 都落在遮罩本身才关（与 document-type-list 同例）。
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
