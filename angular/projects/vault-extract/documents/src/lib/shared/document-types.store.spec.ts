import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DocumentTypeDto, DocumentTypeService } from '@dignite/ng.vault-extract';
import { DocumentTypesStore } from './document-types.store';

// #635 decision 7: one fetch of the visible document types, one state machine, shared by every page that
// needs them. The four models the four components used to keep for this one call collapse into these facts.

const TYPE_A: DocumentTypeDto = { id: 'type-a', typeCode: 'contract', displayName: 'Contract' };
const TYPE_B: DocumentTypeDto = { id: 'type-b', typeCode: 'invoice', displayName: 'Invoice' };

function provide(getVisible: () => Observable<DocumentTypeDto[]>) {
  TestBed.configureTestingModule({
    providers: [{ provide: DocumentTypeService, useValue: { getVisible } }],
  });
  return TestBed.inject(DocumentTypesStore);
}

describe('DocumentTypesStore (#635)', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('is loading, with no types and no error, until the fetch answers', () => {
    const pending = new Subject<DocumentTypeDto[]>();
    const store = provide(() => pending.asObservable());

    expect(store.isLoading()).toBe(true);
    expect(store.value()).toEqual([]);
    expect(store.error()).toBe(false);

    pending.next([TYPE_A, TYPE_B]);

    expect(store.isLoading()).toBe(false);
    expect(store.value()).toEqual([TYPE_A, TYPE_B]);
  });

  it('holds the types once the fetch resolves', () => {
    const store = provide(() => of([TYPE_A]));

    expect(store.isLoading()).toBe(false);
    expect(store.error()).toBe(false);
    expect(store.value()).toEqual([TYPE_A]);
  });

  it('reports a failure as its own state rather than as an empty list', () => {
    // The distinction the whole store exists to keep: "you are granted nothing" and "we could not find out"
    // are different answers, and only one of them may be shown to an operator as a denial.
    const store = provide(() => throwError(() => new Error('offline')));

    expect(store.error()).toBe(true);
    expect(store.isLoading()).toBe(false);
    expect(store.value()).toEqual([]);
  });

  it('fetches exactly once however many consumers inject it', () => {
    const getVisible = vi.fn().mockReturnValue(of([TYPE_A]));
    TestBed.configureTestingModule({
      providers: [{ provide: DocumentTypeService, useValue: { getVisible } }],
    });

    const first = TestBed.inject(DocumentTypesStore);
    const second = TestBed.inject(DocumentTypesStore);

    expect(first).toBe(second);
    expect(getVisible).toHaveBeenCalledTimes(1);
  });

  it('recovers from a failure through reload()', () => {
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('offline')))
      .mockReturnValue(of([TYPE_A, TYPE_B]));
    const store = provide(getVisible);

    expect(store.error()).toBe(true);

    store.reload();

    expect(getVisible).toHaveBeenCalledTimes(2);
    expect(store.error()).toBe(false);
    expect(store.value()).toEqual([TYPE_A, TYPE_B]);
  });

  it('keeps the last good answer visible while a reload is in flight', () => {
    const pending = new Subject<DocumentTypeDto[]>();
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(of([TYPE_A]))
      .mockReturnValue(pending.asObservable());
    const store = provide(getVisible);

    store.reload();

    expect(store.isLoading()).toBe(true);
    // Blanking the list here would make every consumer's picker flicker empty on a refresh.
    expect(store.value()).toEqual([TYPE_A]);

    pending.next([TYPE_A, TYPE_B]);
    expect(store.value()).toEqual([TYPE_A, TYPE_B]);
  });

  it('lets the newest reload win when two overlap', () => {
    // Two clicks on a retry button. Without unsubscribing the first request, whichever response happened to
    // arrive last would decide, including a stale one.
    const first = new Subject<DocumentTypeDto[]>();
    const second = new Subject<DocumentTypeDto[]>();
    const getVisible = vi
      .fn()
      .mockReturnValueOnce(first.asObservable())
      .mockReturnValue(second.asObservable());
    const store = provide(getVisible);

    store.reload();
    second.next([TYPE_B]);
    first.next([TYPE_A]);

    expect(store.value()).toEqual([TYPE_B]);
  });
});
