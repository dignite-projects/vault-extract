import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { LocalizationPipe } from '@abp/ng.core';
import { DocumentDto, DocumentService } from '@dignite/paperbase';

// 文件预览页（路由 documents/:id/file）。替代旧详情页 openFile() 的 blob: 新标签直开——
// 地址栏改为可读的 /documents/{id}/file，含文档 ID。文件本体仍经 DocumentService.getBlob
// （带 Bearer token）拉取后用 blob 内嵌，token 不进 URL。
@Component({
  selector: 'lib-document-file-preview',
  templateUrl: './document-file-preview.component.html',
  imports: [CommonModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentFilePreviewComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly destroyRef = inject(DestroyRef);

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  hasError = signal(false);
  // raw blob: URL —— 用于 <img> src、下载链接、ngOnDestroy 回收。
  blobUrl = signal<string | null>(null);
  // iframe 的 resource URL 必须经 DomSanitizer 放行，否则 Angular 会判 unsafe 丢弃。
  safeUrl = signal<SafeResourceUrl | null>(null);

  private documentId!: string;

  readonly fileName = computed(
    () => this.document()?.fileOrigin?.originalFileName || this.document()?.title || '',
  );
  readonly contentType = computed(() => this.document()?.fileOrigin?.contentType ?? '');
  readonly isImage = computed(() => this.contentType().startsWith('image/'));
  readonly isPdf = computed(() => this.contentType() === 'application/pdf');

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.hasError.set(false);
    // 先取元数据拿 contentType / 文件名决定渲染方式，再取 blob 本体。
    this.documentService
      .get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: doc => {
          this.document.set(doc);
          this.loadBlob();
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  private loadBlob(): void {
    this.documentService
      .getBlob(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: blob => {
          const url = URL.createObjectURL(blob);
          this.blobUrl.set(url);
          this.safeUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
          this.isLoading.set(false);
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  back(): void {
    this.router.navigate(['/documents', this.documentId]);
  }

  ngOnDestroy(): void {
    const url = this.blobUrl();
    if (url) URL.revokeObjectURL(url);
  }
}
