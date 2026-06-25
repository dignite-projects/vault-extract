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
```
src/app/
├── proxy/              # Auto-generated service proxies
├── shared/             # Shared components, pipes, directives
├── book/               # Feature module
│   ├── book.module.ts
│   ├── book-routing.module.ts
│   ├── book-list/
│   │   ├── book-list.component.ts
│   │   ├── book-list.component.html
│   │   └── book-list.component.scss
│   └── book-detail/
```

## Generate Service Proxies
```bash
cd angular
npm run generate-proxy
```

This repository is an Nx workspace and does not have `angular.json`.
Do not run `abp generate-proxy -t ng` directly here; it expects a plain Angular CLI workspace.

The npm script wraps ABP's official nx generator `nx g @abp/nx.generators:generate-proxy`
(`@abp/nx.generators` is ABP's nx-specific wrapper; it internally calls the
`@abp/ng.schematics:proxy-add` schematic) and generates typed service classes under
`packages/vault-extract/src/lib/proxy/`. The host API must be running at `https://localhost:44348`.

The `proxy/` folder is fully owned by the generator and is overwritten on every run —
never edit it by hand. Hand-written, regeneration-safe code lives OUTSIDE `proxy/`:
the FormData upload wrapper in `lib/services/`, the flat re-export adapter in
`public-api.ts`, and the enum contract spec.

## List Component Pattern
```typescript
@Component({
  selector: 'app-book-list',
  templateUrl: './book-list.component.html'
})
export class BookListComponent implements OnInit {
  books = { items: [], totalCount: 0 } as PagedResultDto<BookDto>;

  constructor(
    public readonly list: ListService,
    private bookService: BookService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.hookToQuery();
  }

  private hookToQuery(): void {
    this.list.hookToQuery(query => 
      this.bookService.getList(query)
    ).subscribe(response => {
      this.books = response;
    });
  }

  create(): void {
    // Open create modal
  }

  delete(book: BookDto): void {
    this.confirmation
      .warn('::AreYouSureToDelete', '::AreYouSure')
      .subscribe(status => {
        if (status === Confirmation.Status.confirm) {
          this.bookService.delete(book.id).subscribe(() => this.list.get());
        }
      });
  }
}
```

## Localization
```typescript
// In component
constructor(private localizationService: LocalizationService) {}

getText(): string {
  return this.localizationService.instant('::Books');
}
```

```html
<!-- In template -->
<h1>{{ '::Books' | abpLocalization }}</h1>

<!-- With parameters -->
<p>{{ '::WelcomeMessage' | abpLocalization: userName }}</p>
```

## Authorization

### Permission Directive
```html
<button *abpPermission="'BookStore.Books.Create'">Create</button>
```

### Permission Guard
```typescript
const routes: Routes = [
  {
    path: '',
    component: BookListComponent,
    canActivate: [PermissionGuard],
    data: {
      requiredPolicy: 'BookStore.Books'
    }
  }
];
```

### Programmatic Check
```typescript
constructor(private permissionService: PermissionService) {}

canCreate(): boolean {
  return this.permissionService.getGrantedPolicy('BookStore.Books.Create');
}
```

## Forms with Validation
```typescript
@Component({...})
export class BookFormComponent {
  form: FormGroup;

  constructor(private fb: FormBuilder) {
    this.buildForm();
  }

  buildForm(): void {
    this.form = this.fb.group({
      name: ['', [Validators.required, Validators.maxLength(128)]],
      price: [0, [Validators.required, Validators.min(0)]]
    });
  }

  save(): void {
    if (this.form.invalid) return;
    
    this.bookService.create(this.form.value).subscribe(() => {
      // Handle success
    });
  }
}
```

```html
<form [formGroup]="form" (ngSubmit)="save()">
  <div class="form-group">
    <label for="name">{{ '::Name' | abpLocalization }}</label>
    <input type="text" id="name" formControlName="name" class="form-control" />
  </div>
  
  <button type="submit" class="btn btn-primary" [disabled]="form.invalid">
    {{ '::Save' | abpLocalization }}
  </button>
</form>
```

## Configuration API
```typescript
constructor(private configService: ConfigStateService) {}

getCurrentUser(): CurrentUserDto {
  return this.configService.getOne('currentUser');
}

getSettings(): void {
  const setting = this.configService.getSetting('MyApp.MaxItemCount');
}
```

## Modal Service
```typescript
constructor(private modalService: ModalService) {}

openCreateModal(): void {
  const modalRef = this.modalService.open(BookFormComponent, {
    size: 'lg'
  });

  modalRef.result.then(result => {
    if (result) {
      this.list.get();
    }
  });
}
```

## Toast Notifications
```typescript
constructor(private toaster: ToasterService) {}

showSuccess(): void {
  this.toaster.success('::BookCreatedSuccessfully', '::Success');
}

showError(error: string): void {
  this.toaster.error(error, '::Error');
}
```

## Lazy Loading Modules
```typescript
// app-routing.module.ts
const routes: Routes = [
  {
    path: 'books',
    loadChildren: () => import('./book/book.module').then(m => m.BookModule)
  }
];
```

## Theme & Styling
- Use Bootstrap classes
- ABP provides theme variables via CSS custom properties
- Component-specific styles in `.component.scss`
