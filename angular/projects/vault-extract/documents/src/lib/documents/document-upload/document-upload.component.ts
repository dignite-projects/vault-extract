import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnInit, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  CabinetDto,
  CabinetService,
  DocumentTypeDto,
  DocumentUploadService,
  EXTRACT_PERMISSIONS,
} from '@dignite/ng.vault-extract';
import { from, of } from 'rxjs';
import { catchError, map, mergeMap } from 'rxjs/operators';
import {
  MAX_UPLOAD_FILE_BYTES,
  UPLOAD_ACCEPT_ATTRIBUTE,
  isAllowedUpload,
} from '../upload-constraints';

// Limits the number of concurrent /api/documents/upload requests to avoid
// exhausting the browser's per-origin connection pool and overloading the
// server when the user drops dozens of files at once.
const MAX_CONCURRENT_UPLOADS = 3;

interface FileUploadState {
  key: string;
  name: string;
  done: boolean;
  error: boolean;
  documentId?: string;
  errorMessage?: string;
}

@Component({
  selector: 'lib-document-upload',
  templateUrl: './document-upload.component.html',
  styleUrls: ['./document-upload.component.scss'],
  imports: [CommonModule, FormsModule, LocalizationPipe, RouterModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentUploadComponent implements OnInit {
  private readonly documentUploadService = inject(DocumentUploadService);
  private readonly cabinetService = inject(CabinetService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  // Primary file-picker trigger; used to restore focus after the result queue is
  // cleared, because the button the user just clicked is removed from the DOM.
  private readonly browseButton = viewChild<ElementRef<HTMLButtonElement>>('browseButton');

  // Cabinet selection requires Cabinets.Default permission because backend getList is [Authorize].
  // Without permission, hide the dropdown and upload as unclassified.
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Default,
  );
  cabinets = signal<CabinetDto[]>([]);
  selectedCabinetId = signal<string>('');

  // Declaring a type at upload is equivalent to an operator confirming classification
  // (#623): it skips the LLM classification call entirely, so it requires
  // ConfirmClassification. Without permission, the caller falls back to #629's narrower
  // per-type rule below — every type it does not cover still uploads through ordinary LLM
  // classification as before.
  //
  // Code review (2026-09-05): the type list itself is no longer fetched by this component —
  // its only mount site (DocumentOverviewComponent) already fetches it for its own quick-links
  // section and passes it down via the `documentTypes` input, so there is no second request and
  // no need to mirror the list-read permission (Documents.Default / DocumentTypes.Default) here.
  //
  // #629: a caller without ConfirmClassification no longer has an untyped fallback (the backend
  // now requires ConfirmClassification for untyped upload too, to keep the per-type ACL from
  // being bypassed by letting the LLM pick the type). Its type scope narrows to the types it
  // holds the Resources.Upload grant on, reported per-type on DocumentTypeDto.resourcePermissions
  // by GetVisibleAsync. Such a caller therefore MUST declare a type to upload at all.
  readonly canDeclareType = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly documentTypes = input<DocumentTypeDto[]>([]);
  // Whether the parent's own types fetch is still in flight / has failed (#629 code review): while
  // loading, `documentTypes` still reads as its default `[]`, which is indistinguishable from "loaded,
  // nothing granted" unless the parent tells us which state we are actually in.
  readonly documentTypesLoading = input(false);
  readonly documentTypesUnavailable = input(false);
  selectedDocumentTypeId = signal<string>('');

  // The types this caller may actually declare: every type of the layer for a ConfirmClassification
  // holder (unchanged #623 behaviour), otherwise only the ones carrying its own Upload resource grant.
  readonly declarableTypes = computed(() =>
    this.canDeclareType
      ? this.documentTypes()
      : this.documentTypes().filter(
          t => t.resourcePermissions?.[EXTRACT_PERMISSIONS.DocumentTypes.Resources.Upload] === true,
        ),
  );
  readonly requiresTypeSelection = !this.canDeclareType;
  // Loading folds in here (not just into hasNoGrantableTypes): until the types fetch resolves, the
  // declarable set cannot be trusted, so the picker/dropzone must stay disabled rather than briefly
  // usable with a set that is about to change. Membership (not just non-empty) guards a stale selection
  // that no longer names one of the caller's currently declarable types (#629 code review).
  readonly typeSelectionSatisfied = computed(
    () =>
      !this.requiresTypeSelection ||
      (!this.documentTypesLoading() &&
        this.declarableTypes().some(t => t.id === this.selectedDocumentTypeId())),
  );
  readonly hasNoGrantableTypes = computed(
    () =>
      this.requiresTypeSelection &&
      !this.documentTypesLoading() &&
      !this.documentTypesUnavailable() &&
      this.declarableTypes().length === 0,
  );
  // Distinct from hasNoGrantableTypes: the fetch itself failed, so "no types" cannot be trusted as
  // "nothing granted" — telling the operator to ask an admin for a grant they may already have would
  // be actively misleading.
  readonly showTypesUnavailable = computed(
    () => this.requiresTypeSelection && !this.documentTypesLoading() && this.documentTypesUnavailable(),
  );

  // Picker `accept` filter, derived from the shared whitelist (mirrors backend, #221).
  readonly acceptAttribute = UPLOAD_ACCEPT_ATTRIBUTE;

  isDragOver = signal(false);
  isUploading = signal(false);
  uploadingFiles = signal<FileUploadState[]>([]);

  readonly hasUploadResults = computed(
    () =>
      this.uploadingFiles().length > 0 &&
      !this.isUploading() &&
      this.uploadingFiles().every(file => file.done || file.error),
  );
  readonly hasUploadErrors = computed(
    () => this.hasUploadResults() && this.uploadingFiles().some(file => file.error),
  );
  readonly successfulUploadCount = computed(
    () => this.uploadingFiles().filter(file => file.done).length,
  );
  readonly canAcceptFiles = computed(() => !this.isUploading() && !this.hasUploadResults());

  constructor() {
    // #629: when a caller's declarable scope is exactly one type, there is nothing to actually
    // choose, so pre-select it — otherwise every upload would need a pointless extra click on a
    // single-option dropdown. With two or more declarable types the caller still has to pick.
    effect(() => {
      const types = this.declarableTypes();
      if (this.requiresTypeSelection && types.length === 1 && !this.selectedDocumentTypeId()) {
        this.selectedDocumentTypeId.set(types[0].id ?? '');
      }
    });
  }

  ngOnInit(): void {
    if (this.canViewCabinets) {
      this.cabinetService.getList()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: list => this.cabinets.set(list),
          error: () => this.cabinets.set([]),
        });
    }
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.canAcceptFiles() || !this.typeSelectionSatisfied()) {
      // Show the browser's not-allowed cursor instead of silently ignoring the drag (#629 code review).
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = 'none';
      }
      return;
    }

    this.isDragOver.set(true);
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver.set(false);
    if (!this.canAcceptFiles() || !this.typeSelectionSatisfied()) {
      // Dropping files before a required type is selected was silently swallowed; tell the operator
      // why nothing happened instead of leaving them to guess (#629 code review). Only this specific
      // case warrants a toast — the other guard branch (uploading / showing results) is a timing
      // window, not a mistake the operator needs to be told about.
      if (this.canAcceptFiles() && !this.typeSelectionSatisfied()) {
        this.toaster.warn('::Document:SelectDocumentType:Required');
      }
      return;
    }

    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.uploadFiles(Array.from(files));
    }
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.uploadFiles(Array.from(input.files));
      input.value = '';
    }
  }

  openFilePicker(input: HTMLInputElement): void {
    if (!this.canAcceptFiles() || !this.typeSelectionSatisfied()) return;

    input.click();
  }

  resetQueue(): void {
    this.uploadingFiles.set([]);
    this.isDragOver.set(false);
    // Restore focus to the primary trigger once the empty state re-renders;
    // the button the user just clicked is removed when the queue is cleared.
    afterNextRender(() => this.browseButton()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }

  private uploadFiles(files: File[]): void {
    // #629 defense in depth: the picker/dropzone bindings already refuse to reach this point
    // without a satisfied type selection, but a caller with no declarable type at all has no way
    // to select one, so this is the actual backstop against starting an upload doomed to a 403.
    if (!this.canAcceptFiles() || !this.typeSelectionSatisfied()) return;

    // Mirror the backend fail-closed gate (#221): MIME + extension whitelist, then size.
    const valid = files.filter(f => {
      if (!isAllowedUpload(f)) {
        this.toaster.error('::Document:UnsupportedFileType', '::Error');
        return false;
      }
      if (f.size > MAX_UPLOAD_FILE_BYTES) {
        this.toaster.error('::Document:FileTooLarge', '::Error');
        return false;
      }
      return true;
    });

    if (valid.length === 0) return;

    this.isUploading.set(true);
    this.uploadingFiles.set(
      valid.map((file, index) => ({
        key: `${file.name}-${file.lastModified}-${file.size}-${index}`,
        name: file.name,
        done: false,
        error: false,
      })),
    );

    const indexed = valid.map((file, idx) => ({ file, idx }));
    from(indexed)
      .pipe(
        mergeMap(
          ({ file, idx }) =>
            this.documentUploadService
              .upload(
                file,
                this.selectedCabinetId() || undefined,
                this.selectedDocumentTypeId() || undefined,
              )
              .pipe(
                map(document => ({
                  idx,
                  success: true,
                  documentId: document.id,
                  errorMessage: undefined as string | undefined,
                })),
                catchError(err => {
                  const errorMessage: string | undefined = err?.error?.error?.message;
                  return of({ idx, success: false, documentId: undefined, errorMessage });
                }),
              ),
          MAX_CONCURRENT_UPLOADS,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ idx, success, documentId, errorMessage }) => {
          this.uploadingFiles.update(list =>
            list.map((item, j) =>
              j === idx
                ? {
                    ...item,
                    done: success,
                    error: !success,
                    documentId: success ? documentId : undefined,
                    errorMessage,
                  }
                : item,
            ),
          );
        },
        complete: () => {
          this.isUploading.set(false);
          const hasError = this.uploadingFiles().some(f => f.error);
          if (!hasError) {
            this.toaster.success('::Document:UploadedSuccessfully', '::Success');
          }
        },
      });
  }
}
