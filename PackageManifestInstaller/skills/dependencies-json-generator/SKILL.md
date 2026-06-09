---
name: dependencies-json-generator
description: Generate or update *.dependencies.json files for the INVELON PackageManifestInstaller tool (schema v2.0). Use when a developer needs to create a new template manifest, add/remove/update packages in an existing one, or validate a manifest. Also triggers on mentions of PackageManifestInstaller, dependencies.json, or Unity package manifest.
---

# dependencies-json-generator

Generate or update `*.dependencies.json` files for the **INVELON PackageManifestInstaller** tool (schema v2.0).

Use this skill whenever a developer needs to:
- Create a new `*.dependencies.json` manifest for a Unity project template
- Add, remove, or update packages in an existing manifest
- Validate an existing manifest against the schema

---

## Step 0 — Detect mode and pre-populate from the live project

Before asking the user anything, do the following silently:

1. **Scan the workspace** for any files matching `*.dependencies.json`. If found, read them all — these are the current manifests.
2. **Look for `Packages/manifest.json`** in the workspace (try the mounted folder, its parent, and common Unity project root paths). If found, read it and extract the `dependencies` map — this gives you the packages currently installed in the project, which is a great starting point for a new manifest.
3. **Look for `packages-lock.json`** alongside manifest.json. It may contain resolved versions.

Then decide:

- **Update mode**: One or more `*.dependencies.json` files already exist → show the user what you found and ask what they want to change.
- **Create mode**: No `*.dependencies.json` found, but `Packages/manifest.json` exists → bootstrap a new manifest from the installed packages (see §Pre-populate below), then confirm with the user.
- **Create from scratch**: Nothing found → ask the user for template info and packages.

---

## Step 1 — Gather root-level info (Create mode only)

Ask (or infer from existing manifests):

| Field | Question to ask | Notes |
|---|---|---|
| `templateName` | "What should this template be called?" | Shown as the tab label in the installer |
| `unityVersion` | "Which Unity version?" | E.g. `6000.3`. Informational. |
| `renderPipeline` | "Which render pipeline — URP, HDRP, or Built-in?" | Informational. |
| `menuGroup` | "Do you want a shortcut in the Unity menu bar? If so, what path?" | E.g. `INVELON/My Template`. Optional — omit if not needed. |

---

## Step 2 — Build the package list

### Pre-populate from `Packages/manifest.json`

If you found a `manifest.json`, extract its `dependencies` object. For each entry:
- Parse the key as `id` and the value as a clue for `source` and `url`:
  - Value starts with `https://` or ends with `.git` → `source: "git"`, `url` = value
  - Value starts with `file:` → `source: "tarball"`, extract `tgzFileName` from the path
  - Key appears in a `scopedRegistries` block whose URL contains `openupm` → `source: "openupm"`
  - Otherwise → `source: "registry"`
- Set `version` from the value (strip any `file:` prefix, `#tag` suffix, etc.)
- Attempt to infer `displayName` from the package id (split on `.`, title-case the last segment). The user can correct it.
- Exclude built-in Unity modules (`com.unity.modules.*`) — they are never listed in a manifest.

Present the pre-populated list to the user and say: *"I found these packages already installed. You can remove any you don't need, add new ones, or adjust fields — just tell me what to change."*

### For each package entry, you need:

**Required fields (all sources):**
- `id` — UPM package identifier, e.g. `com.unity.timeline`
- `version` — minimum required semver, e.g. `1.8.7`
- `source` — one of: `registry` | `git` | `openupm` | `tarball` | `assetstore`
- `displayName` — human-readable name shown in the installer table

**Conditional fields by source:**

| `source` | Extra required fields | Extra optional fields |
|---|---|---|
| `registry` | — | `optional`, `installNote` |
| `git` | `url` — full Git URL + optional `#tag` or `#branch` | `optional`, `installNote` |
| `openupm` | `url` = `"https://package.openupm.com"` | `scopeName` (registry display name in manifest.json), `optional`, `installNote` |
| `tarball` | `tgzFileName` — filename of the `.tgz` in `Packages/` | `optional`, `installNote` |
| `assetstore` | `assetStoreId` (numeric product ID, e.g. `"32416"`), `assetFolderPath` (relative path under `Assets/` used to auto-detect install, e.g. `"Demigiant/DOTweenPro"`), `installNote` (see convention below) | `optional` |

**`optional` field:** Set to `true` to move the entry to the "Optional" section in the installer and exclude it from "Install All". Defaults to `false` — omit it entirely when false.

---

## Step 3 — Source type decision tree

When the user describes a package and you need to determine `source`:

```
Is it from the Unity Asset Store (paid asset, My Assets)?
  → source: "assetstore"

Does it have a Git URL (github.com, gitlab.com, etc.)?
  → source: "git"

Is it from OpenUPM (openupm.com)?
  → source: "openupm"

Is it a local .tgz file dropped into the project?
  → source: "tarball"

Is it a standard Unity Registry package (com.unity.* or similar)?
  → source: "registry"
```

If unsure, ask: *"Is this from the Unity Registry, a Git URL, OpenUPM, a local .tgz file, or the Unity Asset Store?"*

---

## Step 4 — Conventions to enforce

- **`installNote` for `assetstore`** is mandatory by convention. Use this wording:
  `"Login to the Unity account where you have the asset and download {displayName} {version} from Package Manager - My Assets."`
  Customize only if the user provides specific account or step details.

- **`tgzFileName` default**: If the user doesn't specify, default to `"{id}-{version}.tgz"` — no need to write this field if it matches the default.

- **`scopeName` for openupm**: If not provided, defaults to the `url` value. Use `"package.openupm.com"` as a sensible default.

- **`$schema` pointer**: If the file will live in the same package as the `Editor/` folder (i.e., next to it), add at the very top of the JSON:
  `"$schema": "./Editor/PackageManifest.schema.json"`
  This enables IDE autocompletion. Ask the user if they're not sure.

- **File naming**: `{kebab-or-dotted-name}.dependencies.json`. Match the convention of existing files in the project.

- **File location**: The file **must** be inside `Assets/` (or an XLINK'd folder that maps into `Assets/`) so Unity indexes it as a TextAsset. If in doubt, place it at the root of the package folder that contains the `Editor/` subfolder.

- **`schemaVersion`**: Always `"2.0"`. Never change this.

---

## Step 5 — Generate and save the file

Produce clean, readable JSON:
- 2-space indentation
- No trailing commas
- Fields in this order per entry: `id`, `version`, `source`, then source-specific fields (`url`, `scopeName`, `tgzFileName`, `assetStoreId`, `assetFolderPath`), then `displayName`, then `optional` (only if true), then `installNote` (only if present)

Save the file to the correct location in the workspace. If updating an existing file, write back to the same path. If creating new, ask the user where inside their project they want it, or use the location of an existing `*.dependencies.json` as a reference.

---

## Reference: Minimal valid file

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

## Reference: All source types in one file

```json
{
  "$schema": "./Editor/PackageManifest.schema.json",
  "schemaVersion": "2.0",
  "templateName": "Full Example Template",
  "unityVersion": "6000.3",
  "renderPipeline": "URP",
  "menuGroup": "INVELON/My Template",
  "packages": [
    {
      "id": "com.unity.render-pipelines.universal",
      "version": "17.0.3",
      "source": "registry",
      "displayName": "Universal Render Pipeline"
    },
    {
      "id": "com.unity.xr.openxr",
      "version": "1.16.1",
      "source": "git",
      "url": "https://github.com/SomeOrg/SomeRepo.git#1.16.1",
      "displayName": "OpenXR Plugin",
      "installNote": "After installing, configure Interaction Profiles in Edit > Project Settings > XR Plug-in Management > OpenXR."
    },
    {
      "id": "com.eflatun.scenereference",
      "version": "5.0.0",
      "source": "openupm",
      "url": "https://package.openupm.com",
      "scopeName": "package.openupm.com",
      "displayName": "Eflatun.SceneReference"
    },
    {
      "id": "com.xoia.basicscripts",
      "version": "0.0.22",
      "source": "tarball",
      "tgzFileName": "com.xoia.basicscripts-0.0.22.tgz",
      "displayName": "XOIA Basic Scripts",
      "installNote": "Place com.xoia.basicscripts-0.0.22.tgz inside the project's Packages/ folder to enable installation."
    },
    {
      "id": "com.demigiant.dotweenpro",
      "version": "1.0.410",
      "source": "assetstore",
      "assetStoreId": "32416",
      "assetFolderPath": "Demigiant/DOTweenPro",
      "displayName": "DOTween Pro",
      "installNote": "Login to the Unity account where you have the asset and download DOTween Pro 1.0.410 from Package Manager - My Assets."
    },
    {
      "id": "com.unity.mobile.android-logcat",
      "version": "1.4.7",
      "source": "registry",
      "displayName": "Android Logcat",
      "optional": true
    }
  ]
}
```

---

## Validation checklist before saving

- [ ] `schemaVersion` is exactly `"2.0"`
- [ ] `templateName` and `packages` are present
- [ ] Every entry has `id`, `version`, `source`, `displayName`
- [ ] `git` entries have `url`
- [ ] `openupm` entries have `url` = `"https://package.openupm.com"`
- [ ] `tarball` entries have `tgzFileName` (or it matches the `{id}-{version}.tgz` default)
- [ ] `assetstore` entries have `assetStoreId`, `assetFolderPath`, and `installNote`
- [ ] No `com.unity.modules.*` entries
- [ ] `optional` field only present when `true`
- [ ] File is inside `Assets/` or an XLINK'd folder
