using System;
using System.Collections.Generic;

namespace INVELON.Editor
{
    // ──────────────────────────────────────────────────────────────────────────
    //  Schema data model (matches PackageManifest.schema.json v2.x).
    //  Uses [Serializable] + public fields for JsonUtility compatibility.
    //  This file has no Unity dependencies so the model is unit-testable.
    // ──────────────────────────────────────────────────────────────────────────

    [Serializable]
    public class TemplateManifest
    {
        public string schemaVersion;
        public string templateName;
        public string unityVersion;
        public string renderPipeline;
        public string menuGroup;   // Informational only (reserved; no menu shortcut is registered).
        public List<PackageEntry> packages = new List<PackageEntry>();
    }

    [Serializable]
    public class PackageEntry
    {
        public string id;
        public string version;
        public string source;          // see PackageSourceIds
        public string url;
        public string scopeName;
        public string tgzFileName;     // used when source == "tarball"
        public string assetStoreId;    // used when source == "assetstore" (numeric product ID)
        public string assetFolderPath; // used when source == "assetstore": relative path under Assets/ to detect install
        public string displayName;
        public bool   optional;
        public string installNote;
    }

    /// <summary>String constants for the "source" field.</summary>
    public static class PackageSourceIds
    {
        public const string Registry   = "registry";
        public const string Git        = "git";
        public const string OpenUpm    = "openupm";
        public const string Tarball    = "tarball";
        public const string AssetStore = "assetstore";
    }

    public enum PackageStatus { Loading, Installed, VersionLow, Missing, Error }

    public class PackageRow
    {
        public PackageEntry  Entry;
        public PackageStatus Status;
        public string        InstalledVersion;
        public string        ErrorMessage;
    }

    /// <summary>One discovered *.dependencies.json file and its parsed rows.</summary>
    public class TemplateTab
    {
        public string           ManifestPath;
        public string           AssetPath;
        public TemplateManifest Manifest;
        public string           LoadError;
        public string           LoadWarning;
        public List<PackageRow> Rows = new List<PackageRow>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Schema version policy
    // ──────────────────────────────────────────────────────────────────────────

    public enum SchemaCompatibility
    {
        /// <summary>Same major, known (or older) minor — fully supported.</summary>
        Compatible,
        /// <summary>Same major, newer minor — loads fine, unknown fields are ignored.</summary>
        CompatibleNewerMinor,
        /// <summary>Different major (or unparsable) — rejected.</summary>
        Incompatible
    }

    /// <summary>
    /// Versioning policy for *.dependencies.json manifests:
    /// the installer accepts any "2.x" manifest. Minor bumps are additive
    /// (new optional fields); a major bump means a breaking change.
    /// </summary>
    public static class SchemaVersionPolicy
    {
        public const int    SupportedMajor = 2;
        public const int    KnownMinor     = 0;
        public const string CurrentVersion = "2.0";

        public static SchemaCompatibility Check(string declared)
        {
            if (string.IsNullOrWhiteSpace(declared))
                return SchemaCompatibility.Incompatible;

            string[] parts = declared.Trim().Split('.');
            if (!int.TryParse(parts[0], out int major) || major != SupportedMajor)
                return SchemaCompatibility.Incompatible;

            int minor = 0;
            if (parts.Length > 1)
                int.TryParse(parts[1], out minor);

            return minor > KnownMinor
                ? SchemaCompatibility.CompatibleNewerMinor
                : SchemaCompatibility.Compatible;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Version comparison
    // ──────────────────────────────────────────────────────────────────────────

    public static class VersionUtil
    {
        /// <summary>Parses a version string, stripping pre-release suffixes (e.g. "1.5.9-pre.1" → 1.5.9).</summary>
        public static bool TryParse(string raw, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            int    dashIdx = raw.IndexOf('-');
            string clean   = dashIdx >= 0 ? raw.Substring(0, dashIdx) : raw;

            // System.Version requires at least "major.minor"
            if (clean.IndexOf('.') < 0) clean += ".0";

            return Version.TryParse(clean, out version);
        }

        /// <summary>True when <paramref name="installed"/> satisfies (is >=) <paramref name="required"/>.</summary>
        public static bool Satisfies(string installed, string required)
        {
            if (TryParse(installed, out Version i) && TryParse(required, out Version r))
                return i >= r;

            // Fallback for non-semantic strings (git hashes, etc.)
            return string.Compare(installed, required, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
