# Changelog

## [2.1.0] - 2026-07-14

### Fixed
- **Install queue now survives domain reloads.** Installing a package with scripts triggers a recompile that used to wipe the queue and silently stall *Install pending* mid-batch. The queue is persisted in `SessionState` and resumes automatically.
- **No more duplicate scoped registries.** When an OpenUPM registry with the same URL already exists in `Packages/manifest.json`, the new scope is appended to it instead of inserting a second registry block.
- **Robust manifest.json / packages-lock.json edits.** Naive `Contains()`/`IndexOf()` checks replaced with a structure-aware scanner (`UpmManifestJson`): no false positives from nested keys or unrelated `file:` refs, correct handling of empty objects/arrays and trailing commas, and lock-file cleanup can no longer remove the wrong nested block.
- Export no longer writes `exported-*.dependencies.json` silently next to the manifest (which appeared as bogus new tabs) — a save dialog asks where to put it.
- Git packages now export their URL correctly (the `name@` prefix from `packageId` is stripped).

### Added
- `package.json` — the tool is now a proper UPM package (`com.invelon.package-manifest-installer`), installable from a git URL or tarball in addition to the XLINK workflow.
- Edit-mode test suite (`Tests/Editor/`) covering JSON surgery, version comparison, schema policy, and export generation.
- *Open Page* button for Asset Store entries with an `assetStoreId`.
- *Locate* button in the header to ping the active manifest asset (replaces the ping-on-every-tab-click behavior).
- Cancelable install progress bar.
- Schema version policy: any `2.x` manifest loads (newer minors show an info note); only major mismatches are rejected.
- `README.md` (supersedes the PDF) and this changelog.

### Changed
- UI language switched to English; all strings centralized in `InstallerStrings`.
- Colors are now dark/light editor skin aware (`InstallerColors`).
- Package name column is flexible-width with full package id tooltips; action buttons are in a fixed-width column so rows stay aligned.
- Code split into focused files: model/policy, JSON editing, export builder, support types, window. Pure logic classes are Unity-free and unit-testable.
- `menuGroup` is documented as informational only (no menu shortcut was ever registered).
- Discovery error message is now generic (no template-specific wording).

## [2.0.0]

- Multi-template tabs, schema v2.0, sources: registry, git, openupm, tarball, assetstore.
