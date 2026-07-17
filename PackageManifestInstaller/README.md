# INVELON Package Manifest Installer

Editor window that auto-discovers `*.dependencies.json` template manifests anywhere in the project and installs their packages with one click. Built for INVELON project templates, but fully generic.

**Menu:** `INVELON > Package Manager > Dependency Installer`

> This README is the authoritative documentation. It supersedes `Editor/PackageManifestInstaller_Docs.pdf`.

## Why it exists

Project templates (VR, AR, etc.) need a known set of UPM packages. When a template is linked into a fresh project, half its scripts won't compile until those packages exist. The installer lives in its own assembly (`INVELON.PackageManagement.Editor`) with **zero external references**, so it compiles and works even when everything else is broken — open the window, click *Install pending*, done.

## How it works

Every file ending in `.dependencies.json` (schema v2.x) found in the project is shown as a tab. Each tab lists the template's packages with their required version, installed version, and status. Packages install sequentially; the queue **survives domain reloads** (installing a package with scripts triggers a recompile), so batch installs always run to completion. The progress bar is cancelable — the in-flight package finishes, the rest are skipped.

## Supported sources

| `source` | Behavior |
|---|---|
| `registry` | Installed as `id@version`. Semantic version check (`>=` required, pre-release suffixes stripped). |
| `git` | Installed from `url` (with optional `#tag`). Presence check only. |
| `openupm` | The scoped registry is added to `Packages/manifest.json` first. If a registry with the same URL already exists, the scope is appended to it — duplicate registry blocks are never created. |
| `tarball` | The `.tgz` must be placed in `Packages/` by the developer (the row shows a warning until it is). The installer registers a `file:./` reference in `manifest.json` and removes any stale entry from `packages-lock.json`. |
| `assetstore` | Cannot be installed automatically. Detected via `assetFolderPath` under `Assets/`, or marked manually with *Mark OK* (stored in `EditorPrefs`). With `assetStoreId` set, an *Open Page* button links to the Asset Store. |

## Authoring manifests

Use the `dependencies-json-generator` Claude skill (in `skills/`), or write by hand following `Editor/PackageManifest.schema.json`. Minimal example:

```json
{
  "schemaVersion": "2.0",
  "templateName": "My Template",
  "packages": [
    {
      "id": "com.unity.timeline",
      "version": "1.8.7",
      "source": "registry",
      "displayName": "Timeline"
    }
  ]
}
```

Rules of thumb: the file must live inside `Assets/` (or a folder linked into it) so Unity indexes it as a TextAsset; `optional: true` moves an entry to the *Optional* section and excludes it from *Install pending*; add `"$schema": "./Editor/PackageManifest.schema.json"` for IDE autocompletion when the file sits next to this package.

### Schema versioning policy

Manifests declare `schemaVersion` as `"2.0"`. The installer accepts any `2.x` manifest: minor bumps are additive (new optional fields, ignored by older installers) and never break loading; a major bump (`3.0`) is a breaking change and is rejected with a clear error. `menuGroup` is informational metadata reserved for future use.

## Exporting

*Export current state → JSON* snapshots the currently installed packages (git and OpenUPM sources are detected automatically, built-in modules excluded) into a new `*.dependencies.json` via a save dialog. Saving inside the project makes it appear as a new tab; saving outside reveals it in the file explorer.

## Installation of this package

Two supported ways to get the tool into a project: link/copy this folder into `Assets/` (the classic XLINK template workflow), or add it as a UPM package — it ships a `package.json` (`com.invelon.package-manifest-installer`), so it can be installed from a git URL or as a local tarball.

## Project structure

```
package.json                          UPM package definition
Editor/
  PackageManifestInstaller.cs         EditorWindow (GUI + install queue orchestration)
  PackageManifestModel.cs             Schema data model, version policy, version comparison
  UpmManifestJson.cs                  Structure-aware string editing of manifest.json / packages-lock.json
  ExportJsonBuilder.cs                Builds exported *.dependencies.json snapshots
  InstallerSupport.cs                 Skin-aware colors, install-queue persistence, manifest loading
  InstallerStrings.cs                 All UI strings (single place to change language)
  PackageManifest.schema.json         JSON Schema for *.dependencies.json (v2.x)
Tests/Editor/                         Edit-mode tests (Window > General > Test Runner)
skills/dependencies-json-generator/   Claude skill for authoring manifests
```

Design constraints to preserve when contributing: no references to other assemblies or packages (the whole point is compiling when the project is broken — this also rules out Newtonsoft JSON, which the tool might itself need to install); all JSON edits to Unity's files go through `UpmManifestJson`, which is pure and covered by tests; all user-facing text goes through `InstallerStrings`; colors go through `InstallerColors` (dark/light skin aware).

## Running the tests

Open **Window > General > Test Runner**, select **EditMode**, and run `INVELON.PackageManagement.Editor.Tests`. The tests cover the JSON surgery (scoped registries, tarball registration, lock-file cleanup), version comparison, schema policy, and export generation.
