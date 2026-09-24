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
import { provideExtract } from '@dignite/ng.vault-extract/config';

export const appConfig: ApplicationConfig = {
  providers: [
    // ...ABP core, theme, etc.
    provideExtract(),
  ],
};
```

`provideExtract()` adds the Documents menu and registers every field type the document field editor uses (the `@dignite/ng.flex-fields` built-ins, CKEditor, and Vault Extract's own Tags), so the host does not call `provideFlexFields()` or any `provide…FieldType()` itself. It must stay in the application config, not a lazy-loaded route: the field-type registry is read once, when it is first injected. Routes are wired separately, via `loadChildren: () => import('@dignite/ng.vault-extract/documents').then(m => m.DOCUMENTS_ROUTES)`.

### Required global styles

The document field editor is built on `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor`, which fetch two third-party stylesheets **from your host** at runtime, by fixed file name, instead of compiling them into the package. An Angular library cannot add entries to a consuming application's `angular.json`, so add these to the `styles` array of your build target:

```jsonc
{ "input": "node_modules/ng-zorro-antd/select/style/index.min.css", "inject": false, "bundleName": "ng-zorro-antd-select" },
{ "input": "node_modules/ckeditor5/dist/ckeditor5.css", "inject": false, "bundleName": "ckeditor5" }
```

`inject: false` is required: an injected entry is emitted under a content hash in a production build, and the fixed-name fetch then 404s. Without the entries the build still succeeds, but CKEditor fields render as bare text with no border or toolbar, `Select` / `Tags` fields render unstyled, and the console logs `[@dignite/ng.flex-fields] Could not load "…css"`. See the *Styles* sections of the `@dignite/ng.flex-fields` and `@dignite/ng.flex-fields-ckeditor` READMEs.

## Documentation

See the [project repository](https://github.com/dignite-projects/vault-extract) for full documentation.
