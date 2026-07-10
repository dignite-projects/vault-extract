import type { ExportFormat } from './export-format.enum';
import type { DocumentLifecycleStatus } from '../document-lifecycle-status.enum';
import type { DocumentFieldFilter } from '../models';

export interface ExportDocumentsInput {
  documentTypeCode: string;
  format?: ExportFormat;
  lifecycleStatus?: DocumentLifecycleStatus | null;
  cabinetId?: string | null;
  creationTimeMin?: string | null;
  creationTimeMax?: string | null;
  hasReviewReasons?: boolean | null;
  originDocumentId?: string | null;
  fieldFilters?: DocumentFieldFilter[] | null;
}
