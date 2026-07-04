import { describe, expect, it } from 'vitest';

import {
  ALLOWED_UPLOAD_CONTENT_TYPES,
  ALLOWED_UPLOAD_EXTENSIONS,
  UPLOAD_ACCEPT_ATTRIBUTE,
  isAllowedUpload,
} from './upload-constraints';

describe('upload constraints', () => {
  it.each([
    ['data.csv', 'text/csv'],
    ['data.csv', 'application/csv'],
    ['data.csv', 'application/vnd.ms-excel'],
    ['data.tsv', 'text/tab-separated-values'],
    ['data.tsv', 'text/tsv'],
    ['notes.txt', 'text/plain'],
    ['report.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    ['slides.pptx', 'application/vnd.openxmlformats-officedocument.presentationml.presentation'],
    ['book.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
  ])('accepts %s with %s', (name, type) => {
    expect(isAllowedUpload(new File(['content'], name, { type }))).toBe(true);
  });

  it('matches extensions case-insensitively', () => {
    const file = new File(['content'], 'REPORT.DOCX', {
      type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    });

    expect(isAllowedUpload(file)).toBe(true);
  });

  it.each([
    ['legacy.doc', 'application/msword'],
    ['legacy.xls', 'application/vnd.ms-excel'],
    ['macro.xlsm', 'application/vnd.ms-excel.sheet.macroEnabled.12'],
    ['readme.md', 'text/markdown'],
    ['archive.zip', 'application/zip'],
    ['unknown.xlsx', 'application/octet-stream'],
  ])('rejects unsupported upload %s', (name, type) => {
    expect(isAllowedUpload(new File(['content'], name, { type }))).toBe(false);
  });

  it.each([
    ['book.xlsx', 'text/plain'],
    ['notes.txt', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
    ['report.docx', 'application/vnd.ms-excel'],
    ['slides.pptx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    ['data.csv', 'text/tab-separated-values'],
  ])('rejects mismatched allowed pair %s with %s', (name, type) => {
    expect(isAllowedUpload(new File(['content'], name, { type }))).toBe(false);
  });

  it('keeps the picker attribute derived from both mirrored allow-lists', () => {
    const acceptValues = UPLOAD_ACCEPT_ATTRIBUTE.split(',');
    for (const type of ALLOWED_UPLOAD_CONTENT_TYPES) {
      expect(acceptValues).toContain(type);
    }
    for (const extension of ALLOWED_UPLOAD_EXTENSIONS) {
      expect(acceptValues).toContain(extension);
    }
  });
});
