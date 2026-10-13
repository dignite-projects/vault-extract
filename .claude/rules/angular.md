---
description: "ABP Angular UI patterns and best practices"
paths:
  - "**/angular/**/*.ts"
  - "**/angular/**/*.html"
  - "**/*.component.ts"
---

# ABP Angular UI

> **Docs**: https://abp.io/docs/latest/framework/ui/angular/overview

## Project Structure

This is an Angular CLI workspace (`angular/`), with `angular.json` declaring both projects. It was an
Nx workspace until #579; anything still referring to `project.json` or `nx` commands is stale.

```
angular/
├── src/                             # The only runnable app (ng serve host / npm start)
│   └── app/
│       ├── app.config.ts            # bootstrapApplication providers
│       ├── app.routes.ts            # APP_ROUTES (lazy loadChildren to lib route arrays)
│       └── home/
└── projects/
    └── vault-extract/               # Angular library; imported as @dignite/ng.vault-extract
        ├── src/lib/
        │   ├── proxy/               # ⚠️  GENERATED — never edit by hand
        │   │   └── http-api/documents/  # typed service classes + models
        │   ├── services/            # hand-written, regeneration-safe wrappers
        │   └── shared/tokens/
        │       └── extract-permissions.ts   # EXTRACT_PERMISSIONS constant
        ├── documents/               # sub-entry-point (@dignite/ng.vault-extract/documents)
        │   └── src/lib/
        │       ├── documents.routes.ts       # exports DOCUMENTS_ROUTES
        │       ├── cabinets/
        │       ├── document-types/
        │       ├── documents/
        │       ├── exports/
        │       ├── fields/
        │       ├── reprocessing/    # dedicated standalone modal components
        │       └── shared/
        └── config/                  # sub-entry-point (@dignite/ng.vault-extract/config)
```

## Generate Service Proxies
```bash
cd angular
npm run generate-proxy
```

The npm script wraps ABP's standard `abp generate-proxy -t ng`, which generates typed service classes
under `projects/vault-extract/src/lib/proxy/`. The host API must be running at `https://localhost:44348`.

Until #579 this repository was an Nx workspace with no `angular.json`, so `abp generate-proxy` could
not run and `@abp/nx.generators` had to stand in for it. That is no longer true — the nx generator and
its undeclared `@nx/devkit` peer are both gone.

The `proxy/` folder is fully owned by the generator and is overwritten on every run —
never edit it by hand. Hand-written, regeneration-safe code lives OUTSIDE `proxy/`:
the FormData upload wrapper in `lib/services/`, the flat re-export adapter in
`public-api.ts`, and the enum contract spec.

## Standalone Component Anatomy

**Every component is standalone** — zero `NgModule`, zero `*.module.ts` in this repo.

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { LocalizationPipe, ListService, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { SomeService, EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';

@Component({
  selector: 'lib-some-list',
  templateUrl: './some-list.component.html',
  standalone: true,
  imports: [CommonModule, LocalizationPipe],
  providers: [ListService],          // scoped providers declared here, not in a module
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SomeListComponent implements OnInit {
  // DI via inject() — never constructor injection for services
  private readonly service = inject(SomeService);
  private readonly toaster = inject(ToasterService);
  private readonly permissionService = inject(PermissionService);
  private readonly destroyRef = inject(DestroyRef);

  readonly list = inject(ListService);

  // Permissions stored as plain boolean fields (evaluated once at construction)
  readonly canCreate = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Create);
  readonly canUpdate = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Update);
  readonly canDelete = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Delete);

  // State as signals
  items = signal<ItemDto[]>([]);
  isLoading = signal(true);
  readonly showActions = computed(() => this.canUpdate || this.canDelete);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.service.getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => { this.items.set(list); this.isLoading.set(false); },
        error: () => this.isLoading.set(false),
      });
  }
}
```

## Route Definition

Routes use `loadComponent` for individual components and `loadChildren` for route arrays.
Guards are functional: `[authGuard, permissionGuard]` from `@abp/ng.core`.

```typescript
// projects/vault-extract/documents/src/lib/documents.routes.ts
import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { EXTRACT_PERMISSIONS } from '@dignite/ng.vault-extract';

export const DOCUMENTS_ROUTES: Routes = [
  {
    path: 'documents',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.Documents.Default },
    loadComponent: () =>
      import('./documents/document-list/document-list.component').then(c => c.DocumentListComponent),
  },
  {
    path: 'document-types',
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: EXTRACT_PERMISSIONS.DocumentTypes.Default },
    loadComponent: () =>
      import('./document-types/document-type-list/document-type-list.component').then(c => c.DocumentTypeListComponent),
  },
];
```

The host app wires route arrays via `loadChildren`:

```typescript
// src/app/app.routes.ts
export const APP_ROUTES: Routes = [
  {
    path: 'vault-extract',
    loadChildren: () => import('@dignite/ng.vault-extract/documents').then(m => m.DOCUMENTS_ROUTES),
  },
];
```

## Permissions

### Runtime check (stored boolean field + `@if`)

Import `EXTRACT_PERMISSIONS` from `@dignite/ng.vault-extract`. Call
`permissionService.getGrantedPolicy(...)` at class field level (not in methods) and
bind it in the template with `@if`.

```typescript
// In component class
readonly canCreate = this.permissionService.getGrantedPolicy(EXTRACT_PERMISSIONS.Cabinets.Create);
```

```html
<!-- In template — use @if, not *abpPermission, as the primary idiom -->
@if (canCreate) {
  <button class="btn btn-primary" (click)="openCreate()">
    {{ '::Cabinet:New' | abpLocalization }}
  </button>
}
```

`*abpPermission="'VaultExtract.Cabinets.Create'"` is the directive alternative; it is
valid but **`getGrantedPolicy` + `@if` is the pattern used throughout this codebase**.
Never use raw string literals like `'VaultExtract.Cabinets.Create'` inline in templates
or components — always go through the typed `EXTRACT_PERMISSIONS` constant.

### Route guard

```typescript
{
  canActivate: [authGuard, permissionGuard],
  data: { requiredPolicy: EXTRACT_PERMISSIONS.DocumentTypes.Default },
  loadComponent: () => import('./...').then(c => c.SomeComponent),
}
```

Both guards are functional guards from `@abp/ng.core`; do **not** use the old class-based
`PermissionGuard` (deprecated).

## Modal Patterns

There are two patterns in this codebase. Pick based on complexity.

### Pattern A — Signal-driven inline modal (create/edit forms)

Used by `CabinetListComponent` and `DocumentTypeListComponent`. The list component owns
the form and modal state. A discriminated-union signal drives open/close. No separate
modal service is involved.

```typescript
// In the list component class
editing = signal<CabinetDto | 'create' | null>(null);
isSubmitting = signal(false);

readonly form = inject(FormBuilder).nonNullable.group({
  name: ['', [Validators.required, Validators.maxLength(128)]],
});

openCreate(): void {
  this.form.reset({ name: '' });
  this.editing.set('create');
}

openEdit(item: CabinetDto): void {
  this.form.reset({ name: item.name });
  this.editing.set(item);
}

closeModal(): void {
  this.editing.set(null);
}

// Backdrop close guard: close only when both mousedown AND click land on the
// backdrop itself (not on text dragged out of an input field).
private backdropMouseDownOnSelf = false;
onBackdropMouseDown(e: MouseEvent): void { this.backdropMouseDownOnSelf = e.target === e.currentTarget; }
onBackdropClick(e: MouseEvent): void {
  if (this.backdropMouseDownOnSelf && e.target === e.currentTarget) this.closeModal();
  this.backdropMouseDownOnSelf = false;
}
```

```html
<!-- Modal rendered inline in the list template -->
@if (editing()) {
  <div class="modal-backdrop" (mousedown)="onBackdropMouseDown($event)" (click)="onBackdropClick($event)">
    <div class="modal-dialog">
      <form [formGroup]="form" (ngSubmit)="submit()">
        ...
        <button type="submit" [disabled]="isSubmitting()">
          {{ '::Save' | abpLocalization }}
        </button>
      </form>
    </div>
  </div>
}
```

### Pattern B — Dedicated standalone modal component (complex workflows)

Used by `ReclassificationModalComponent` and `FieldReextractionModalComponent`. When
a modal has enough logic to warrant its own class (preview calls, scoped loading state,
multi-step form), extract it as a dedicated standalone component. The parent holds a
nullable signal for the open target and imports the component.

```typescript
// The dedicated modal component
@Component({
  selector: 'lib-field-reextraction-modal',
  templateUrl: './field-reextraction-modal.component.html',
  standalone: true,
  imports: [CommonModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FieldReextractionModalComponent implements OnInit {
  private readonly service = inject(DocumentReprocessingService);
  private readonly destroyRef = inject(DestroyRef);

  @Input({ required: true }) documentTypeId!: string;
  @Output() closed = new EventEmitter<void>();

  readonly isSubmitting = signal(false);
  // ... preview signals, loadPreview(), confirm(), close()
}
```

```typescript
// In the parent list component
// 1. Import the dedicated component
imports: [FieldReextractionModalComponent],

// 2. Signal for open target (null = closed)
reextractTarget = signal<DocumentTypeDto | null>(null);

openReextractFields(type: DocumentTypeDto): void {
  this.reextractTarget.set(type);
}
```

```html
<!-- In the parent list template -->
@if (reextractTarget(); as t) {
  <lib-field-reextraction-modal
    [documentTypeId]="t.id!"
    [documentTypeDisplayName]="t.displayName ?? ''"
    (closed)="reextractTarget.set(null)"
  />
}
```

ABP `ModalService` is available as an alternative but is **not** the established pattern
in this codebase. Do not introduce it unless a genuine need arises (e.g., opening a modal
from a non-template context).

## ListService with Signals

`ListService` is provided in the component's `providers: []` array and obtained via
`inject()`. Subscribe to `query$` and `hookToQuery` with `takeUntilDestroyed`.

```typescript
providers: [ListService],
// ...
readonly list = inject(ListService);

ngOnInit(): void {
  this.list.requestStatus$
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe(status => this.isLoading.set(status === 'loading'));

  this.list
    .hookToQuery(query => this.service.getList({ ...query }))
    .pipe(takeUntilDestroyed(this.destroyRef))
    .subscribe(result => this.items.set(result.items ?? []));
}
```

## Localization

Import `LocalizationPipe` from `@abp/ng.core` in the component's `imports` array.

```html
<h1>{{ '::Cabinet:Title' | abpLocalization }}</h1>
<p>{{ '::WelcomeMessage' | abpLocalization: userName }}</p>
```

For programmatic access inject `LocalizationService`:

```typescript
private readonly localization = inject(LocalizationService);
getText(): string { return this.localization.instant('::Cabinet:Title'); }
```

## Toast Notifications

```typescript
private readonly toaster = inject(ToasterService);

onSuccess(): void { this.toaster.success('::Cabinet:CreatedSuccessfully', '::Success'); }
onError(): void   { this.toaster.error('::Cabinet:DeleteFailed', '::Error'); }
```

## Template Control Flow

Use Angular 17+ block syntax — NOT `*ngIf` / `*ngFor`:

```html
@if (canCreate) {
  <button>Create</button>
}

@for (item of items(); track item.id) {
  <tr>...</tr>
}

@if (isLoading()) {
  <div class="spinner-border"></div>
} @else {
  <!-- content -->
}
```

## Theme & Styling

- Bootstrap utility classes for layout/spacing
- ABP LeptonX theme variables via CSS custom properties
- Component-specific styles in `.component.scss`
- Icons: Font Awesome (`fas fa-*`)
