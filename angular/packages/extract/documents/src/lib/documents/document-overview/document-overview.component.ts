import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import {
  CabinetDto,
  CabinetService,
  DocumentStatisticsDto,
  DocumentStatisticsService,
  DocumentTypeDto,
  DocumentTypeService,
  EXTRACT_PERMISSIONS,
} from '@dignite/extract';
import { EMPTY, Subject } from 'rxjs';
import { catchError, switchMap, tap } from 'rxjs/operators';
import { DocumentUploadComponent } from '../document-upload/document-upload.component';
import { formatBytes } from '../../shared/format-bytes';

@Component({
  selector: 'lib-document-overview',
  templateUrl: './document-overview.component.html',
  styleUrls: ['./document-overview.component.scss'],
  imports: [CommonModule, RouterModule, LocalizationPipe, DocumentUploadComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentOverviewComponent implements OnInit {
  private readonly permissionService = inject(PermissionService);
  private readonly statisticsService = inject(DocumentStatisticsService);
  private readonly cabinetService = inject(CabinetService);
  private readonly documentTypeService = inject(DocumentTypeService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canUpload = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.Upload,
  );
  readonly canReview = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Documents.ConfirmClassification,
  );
  readonly canViewCabinets = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Default,
  );
  readonly canCreateCabinet = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.Cabinets.Create,
  );
  readonly canManageTypes = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.DocumentTypes.Default,
  );
  readonly canCreateType = this.permissionService.getGrantedPolicy(
    EXTRACT_PERMISSIONS.DocumentTypes.Create,
  );

  // Filtered-entry navigation (#335): cabinets and visible document types as quick links into the
  // document list. No per-entity counts — navigation, not a dashboard. Cabinet list is gated by
  // Cabinets.Default; document types are visible to any Documents.Default operator (GetVisible is
  // decoupled from DocumentTypes.Default, #223).
  readonly cabinets = signal<CabinetDto[]>([]);
  readonly documentTypes = signal<DocumentTypeDto[]>([]);
  // Loading starts true only when a fetch will actually run, so the empty state never flashes first.
  readonly cabinetsLoading = signal(this.canViewCabinets);
  readonly typesLoading = signal(true);

  // Show a section while it is still loading (avoids a layout pop), when it has items, or when the
  // user can create the first one (actionable empty state). A plain viewer with neither items nor
  // create rights sees nothing instead of a dead "empty" box.
  readonly showCabinetSection = computed(
    () =>
      this.canViewCabinets &&
      (this.cabinetsLoading() || this.cabinets().length > 0 || this.canCreateCabinet),
  );
  readonly showTypeSection = computed(
    () => this.typesLoading() || this.documentTypes().length > 0 || this.canCreateType,
  );

  readonly stats = signal<DocumentStatisticsDto | null>(null);
  readonly statsLoading = signal(true);
  readonly statsError = signal(false);

  // The loading skeleton must render the same number of tiles the data grid will, otherwise non-reviewers
  // (who don't see the needs-review tile) get a 6 -> 5 layout jump when stats resolve.
  readonly skeletonSlots = this.canReview ? [0, 1, 2, 3, 4, 5] : [0, 1, 2, 3, 4];

  // In-flight = stored-but-not-started (Uploaded) + actively processing. Composing the display bucket here
  // keeps the API contract a faithful per-status projection (#333 decision: granularity in the DTO, grouping in the UI).
  readonly processingCount = computed(() => {
    const s = this.stats();
    return (s?.uploadedCount ?? 0) + (s?.processingCount ?? 0);
  });

  readonly isEmpty = computed(() => (this.stats()?.totalCount ?? 0) === 0);

  // Exposed so the template can format the storage tile.
  readonly formatBytes = formatBytes;

  // A trigger drives loads through switchMap so a slower earlier request can never overwrite a newer one
  // (e.g. rapid Refresh clicks): each emission cancels the previous in-flight GET. catchError keeps the
  // stream alive across failures so later retries still work.
  private readonly reload$ = new Subject<void>();

  constructor() {
    this.reload$
      .pipe(
        tap(() => {
          this.statsLoading.set(true);
          this.statsError.set(false);
        }),
        switchMap(() =>
          this.statisticsService.get().pipe(
            catchError(() => {
              this.statsError.set(true);
              this.statsLoading.set(false);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(stats => {
        this.stats.set(stats);
        this.statsLoading.set(false);
      });
  }

  ngOnInit(): void {
    this.loadStatistics();
    if (this.canViewCabinets) {
      this.loadCabinets();
    }
    this.loadDocumentTypes();
  }

  loadStatistics(): void {
    this.reload$.next();
  }

  private loadCabinets(): void {
    this.cabinetService
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.cabinets.set(list);
          this.cabinetsLoading.set(false);
        },
        error: () => {
          this.cabinets.set([]);
          this.cabinetsLoading.set(false);
        },
      });
  }

  private loadDocumentTypes(): void {
    this.documentTypeService
      .getVisible()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.documentTypes.set(list);
          this.typesLoading.set(false);
        },
        error: () => {
          this.documentTypes.set([]);
          this.typesLoading.set(false);
        },
      });
  }
}
