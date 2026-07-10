// Public API surface of @dignite/vault-extract.
//
// The proxy/ folder is fully owned by `nx g @abp/ng.schematics:proxy-add`
// (see proxy/README.md) and is overwritten on every regeneration — never edit it by hand.
// Per the generator's README we re-export proxy files DIRECTLY here (not via their
// index.ts barrels) so this published surface stays flat and ng-packagr-safe.
//
// Hand-written, regeneration-safe additions live under ./lib/services (outside proxy/).
export * from './lib/shared';

// Hand-written, proxy-external services (survive proxy regeneration).
export * from './lib/services/document-upload.service';
export * from './lib/services/document-list-query.service';

// --- generated proxy: services ---
export * from './lib/proxy/http-api/documents/document.service';
export * from './lib/proxy/http-api/documents/document-statistics.service';
export * from './lib/proxy/http-api/documents/cabinets/cabinet.service';
export * from './lib/proxy/http-api/documents/document-types/document-type.service';
export * from './lib/proxy/http-api/documents/document-types/packs/document-type-pack.service';
export * from './lib/proxy/http-api/documents/exports/document-export.service';
export * from './lib/proxy/http-api/documents/fields/field-definition.service';
export * from './lib/proxy/http-api/documents/fields/field-draft-suggestion.service';
// #447: was a hand-written wrapper under ./lib/services, added because `npm run generate-proxy` could not
// run (the workspace never installed @nx/devkit, which @abp/nx.generators requires but does not declare).
// The generator works now, so the wrapper is gone and this is the real generated service.
export * from './lib/proxy/http-api/documents/fields/field-prompt-polish.service';
export * from './lib/proxy/http-api/documents/pipelines/document-pipeline-run.service';
export * from './lib/proxy/http-api/documents/reprocessing/document-reprocessing.service';
export * from './lib/proxy/http-api/slugging/slug-suggestion.service';

// --- generated proxy: models ---
export * from './lib/proxy/documents/models';
export * from './lib/proxy/documents/cabinets/models';
export * from './lib/proxy/documents/document-types/models';
export * from './lib/proxy/documents/document-types/packs/models';
export * from './lib/proxy/documents/exports/models';
export * from './lib/proxy/documents/fields/models';
export * from './lib/proxy/documents/pipelines/models';
export * from './lib/proxy/documents/reprocessing/models';
export * from './lib/proxy/slugging/models';
// NOTE: './lib/proxy/system/text/json/models' is intentionally NOT exported. The generator
// emits invalid TS there (`interface any extends any {}`) for the backend
// System.Text.Json.JsonElement type used by ExtractedFields. It is orphaned — no generated
// service/model imports it (DocumentDto.extractedFields is Record<string, any>) — so leaving it
// unreferenced keeps it out of the ng-packagr compile graph. Survives regeneration: public-api.ts
// lives outside proxy/, so this skip is never overwritten.
export * from './lib/proxy/volo/abp/content/models';

// --- generated proxy: enums ---
export * from './lib/proxy/documents/document-lifecycle-status.enum';
export * from './lib/proxy/documents/document-review-disposition.enum';
export * from './lib/proxy/documents/document-review-reasons.enum';
export * from './lib/proxy/documents/document-types/packs/pack-import-mode.enum';
export * from './lib/proxy/documents/document-types/packs/pack-item-action.enum';
export * from './lib/proxy/documents/exports/export-format.enum';
export * from './lib/proxy/documents/fields/field-data-type.enum';
export * from './lib/proxy/documents/pipelines/pipeline-run-status.enum';
export * from './lib/proxy/documents/reprocessing/reclassification-scope.enum';
