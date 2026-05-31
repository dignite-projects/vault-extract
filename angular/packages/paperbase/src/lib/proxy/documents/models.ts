import type { EntityDto, ExtensibleObject } from '@abp/ng.core';
import type { DocumentLifecycleStatus } from './document-lifecycle-status.enum';
import type { DocumentReviewStatus } from './document-review-status.enum';
import type { PipelineRunStatus } from './pipeline-run-status.enum';

export interface FileOriginDto {
  uploadedByUserName: string;
  originalFileName?: string;
  contentType: string;
  fileSize: number;
}

// Mirrors C# Dignite.Paperbase.Documents.Pipelines.PipelineRunCandidate (Domain.Shared).
export interface PipelineRunCandidate {
  typeCode: string;
  confidenceScore: number;
}

export interface DocumentPipelineRunDto extends ExtensibleObject {
  id: string;
  documentId: string;
  pipelineCode: string;
  status: PipelineRunStatus;
  attemptNumber: number;
  startedAt: string;
  completedAt?: string;
  statusMessage?: string;
  // Top-K classification candidates surfaced from the low-confidence path —
  // strong-typed projection of ExtraProperties["Candidates"]. null when there
  // is no low-confidence outcome to review. Prefer this over reading
  // extraProperties by key.
  candidates?: PipelineRunCandidate[] | null;
  extraProperties?: Record<string, unknown>;
}

// Returned by GetAsync — full document aggregate including Markdown payload and
// pipeline run history. The list endpoint returns the slim DocumentListItemDto.
export interface DocumentDto extends EntityDto<string> {
  tenantId?: string;
  originalFileBlobName: string;
  fileOrigin: FileOriginDto;
  // 所属文件柜（#194）。null = 未归类。柜名由前端用柜列表 map 显示。
  cabinetId?: string | null;
  documentTypeCode?: string;
  lifecycleStatus: DocumentLifecycleStatus;
  reviewStatus: DocumentReviewStatus;
  classificationConfidence: number;
  classificationReason?: string | null;
  // Display title generated from extracted Markdown (text extraction pipeline).
  // Pre-migration documents may be null — UI must fall back to fileOrigin.originalFileName.
  title?: string | null;
  markdown?: string | null;
  // Type-bound field extraction result (field architecture v2). Key = field name
  // (same shape as FieldDefinitionDto.name). null when nothing has been extracted.
  // JSON values are decoded server-side from a SQL Server json column, so each
  // value may be a string, number, boolean, or null.
  extractedFields?: Record<string, unknown> | null;
  creationTime: string;
  pipelineRuns: DocumentPipelineRunDto[];
}

// Returned by GetListAsync — deliberately slim (no Markdown, no pipelineRuns).
export interface DocumentListItemDto extends EntityDto<string> {
  tenantId?: string;
  originalFileBlobName: string;
  fileOrigin: FileOriginDto;
  // 所属文件柜（#194）。null = 未归类。柜名由前端用柜列表 map 显示。
  cabinetId?: string | null;
  documentTypeCode?: string;
  lifecycleStatus: DocumentLifecycleStatus;
  reviewStatus: DocumentReviewStatus;
  classificationConfidence: number;
  title?: string | null;
  creationTime: string;
  // 软删除时间（仅当 isDeleted=true 的回收站视图查询时有值）。
  deletionTime?: string | null;
  // Type-bound field extraction result (field architecture v2). Key = field name
  // (same shape as FieldDefinitionDto.name). null when nothing has been extracted.
  // JSON values are decoded server-side from a SQL Server json column, so each
  // value may be a string, number, boolean, or null.
  extractedFields?: Record<string, unknown> | null;
}

export interface DocumentFieldFilter {
  name?: string;
  value?: string | null;
  min?: string | null;
  max?: string | null;
}

export interface GetDocumentListInput {
  maxResultCount?: number;
  skipCount?: number;
  sorting?: string;
  lifecycleStatus?: DocumentLifecycleStatus | number | null;
  documentTypeCode?: string | null;
  reviewStatus?: DocumentReviewStatus | null;
  // true = 仅返回已软删除文档（回收站视图）；undefined/false = 仅返回未删除文档
  isDeleted?: boolean | null;
  // 按文件柜筛选（#194）。null/undefined = 不筛选。
  cabinetId?: string | null;
  // ExtractedFields 字段值过滤器（多个之间 AND，锚定 documentTypeCode）。null/undefined = 仅按元数据检索。
  fieldFilters?: DocumentFieldFilter[] | null;
}
