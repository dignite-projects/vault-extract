<p align="center">
  <img src="https://raw.githubusercontent.com/dignite-projects/vault-extract/main/.github/icon.png" width="128" />
</p>

# @dignite/ng.vault-extract

Angular UI library for [Dignite Vault Extract](https://github.com/dignite-projects/vault-extract) — the channel layer that turns scans, photos, PDF images, and Office files into trustworthy structured data (Markdown + metadata) for downstream RAG platforms, business systems, and AI clients.

## Installation

```bash
npm install @dignite/ng.vault-extract
```

### Application config

```ts
// app.config.ts
import { provideExtract } from '@dignite/ng.vault-extract/config';

export const appConfig: ApplicationConfig = {
  providers: [
    // ...ABP core, theme, etc.
    provideExtract(),
  ],
};

// app.routes.ts
{
  path: 'documents',
  loadChildren: () => import('@dignite/ng.vault-extract/documents').then(m => m.DOCUMENTS_ROUTES),
}
```

`provideExtract()` adds the Documents menu. `DOCUMENTS_ROUTES` registers every field type the document pages use (the `@dignite/ng.flex-fields` built-ins, CKEditor, and Vault Extract's own Tags) on its own route, together with a route-level `FieldTypeResolver`, so the host calls neither `provideFlexFields()` nor any `provide…FieldType()`, and a lazy-loaded `DOCUMENTS_ROUTES` keeps flex-fields and the CKEditor adapter out of the initial bundle. A field type the host registers itself, at the root or on a route wrapping `DOCUMENTS_ROUTES`, is not visible on these pages: the route's own registration replaces its parents'. The set of field types is decided by the server-side registry, and each one the server offers is registered here.

### Required global styles

The document field editor is built on `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor`, which fetch two third-party stylesheets **from your host** at runtime, by fixed file name, instead of compiling them into the package. An Angular library cannot add entries to a consuming application's `angular.json`, so add these to the `styles` array of your build target:

```jsonc
{ "input": "node_modules/ng-zorro-antd/select/style/index.min.css", "inject": false, "bundleName": "ng-zorro-antd-select" },
{ "input": "node_modules/ckeditor5/dist/ckeditor5.css", "inject": false, "bundleName": "ckeditor5" }
```

`inject: false` is required: an injected entry is emitted under a content hash in a production build, and the fixed-name fetch then 404s. Without the entries the build still succeeds, but CKEditor fields render as bare text with no border or toolbar, `Select` / `Tags` fields render unstyled, and the console logs `[@dignite/ng.flex-fields] Could not load "…css"`. See the *Styles* sections of the `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor` READMEs.

## Documentation

See the [project repository](https://github.com/dignite-projects/vault-extract) for full documentation.
