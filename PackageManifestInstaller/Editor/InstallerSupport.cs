using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace INVELON.Editor
{
    // ──────────────────────────────────────────────────────────────────────────
    //  Skin-aware colors (dark "Pro" skin vs. light skin)
    // ──────────────────────────────────────────────────────────────────────────

    internal static class InstallerColors
    {
        private static bool Pro => EditorGUIUtility.isProSkin;

        public static Color Ok      => Pro ? new Color(0.35f, 0.80f, 0.35f) : new Color(0.05f, 0.45f, 0.05f);
        public static Color Warn    => Pro ? new Color(0.95f, 0.75f, 0.20f) : new Color(0.55f, 0.38f, 0.00f);
        public static Color Error   => Pro ? new Color(0.90f, 0.35f, 0.35f) : new Color(0.65f, 0.08f, 0.08f);
        public static Color Neutral => Pro ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.40f, 0.40f, 0.40f);
        public static Color Install => Pro ? new Color(0.25f, 0.55f, 0.90f) : new Color(0.20f, 0.45f, 0.80f);
        public static Color Export  => Pro ? new Color(0.45f, 0.45f, 0.65f) : new Color(0.40f, 0.40f, 0.60f);

        public static Color Separator => Pro
            ? new Color(0.30f, 0.30f, 0.30f, 0.50f)
            : new Color(0.55f, 0.55f, 0.55f, 0.50f);

        public static Color SourceGit        => Pro ? new Color(0.30f, 0.65f, 0.90f) : new Color(0.10f, 0.35f, 0.65f);
        public static Color SourceOpenUpm    => Pro ? new Color(0.65f, 0.45f, 0.90f) : new Color(0.40f, 0.20f, 0.65f);
        public static Color SourceTarball    => Pro ? new Color(0.90f, 0.60f, 0.20f) : new Color(0.60f, 0.35f, 0.00f);
        public static Color SourceAssetStore => Pro ? new Color(0.90f, 0.45f, 0.55f) : new Color(0.65f, 0.15f, 0.25f);
        public static Color SourceRegistry   => Pro ? new Color(0.50f, 0.65f, 0.50f) : new Color(0.20f, 0.40f, 0.20f);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Install queue persistence across domain reloads
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists the pending install queue across domain reloads. Installing a package
    /// with scripts triggers a recompile that wipes all EditorWindow fields, which
    /// previously stalled "Install pending" mid-batch. SessionState survives domain
    /// reloads (but not editor restarts), which is exactly the lifetime we need.
    /// </summary>
    internal static class InstallQueueState
    {
        private const string Key = "INVELON.PMI.PendingInstallQueue";

        public static void Save(string tabAssetPath, int installedCount, int totalToInstall, IEnumerable<string> pendingPackageIds)
        {
            string payload = tabAssetPath + "\n" +
                             installedCount + "|" + totalToInstall + "\n" +
                             string.Join("\n", pendingPackageIds);
            SessionState.SetString(Key, payload);
        }

        public static bool TryLoad(out string tabAssetPath, out int installedCount, out int totalToInstall, out List<string> pendingPackageIds)
        {
            tabAssetPath      = null;
            installedCount    = 0;
            totalToInstall    = 0;
            pendingPackageIds = new List<string>();

            string payload = SessionState.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(payload)) return false;

            string[] lines = payload.Split('\n');
            if (lines.Length < 3) { Clear(); return false; }

            tabAssetPath = lines[0];

            string[] counts = lines[1].Split('|');
            if (counts.Length != 2 ||
                !int.TryParse(counts[0], out installedCount) ||
                !int.TryParse(counts[1], out totalToInstall))
            {
                Clear();
                return false;
            }

            for (int i = 2; i < lines.Length; i++)
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    pendingPackageIds.Add(lines[i].Trim());

            if (pendingPackageIds.Count == 0) { Clear(); return false; }
            return true;
        }

        public static void Clear() => SessionState.EraseString(Key);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Manifest file loading
    // ──────────────────────────────────────────────────────────────────────────

    internal static class ManifestLoader
    {
        /// <summary>Loads and validates one *.dependencies.json file into a TemplateTab.</summary>
        public static TemplateTab Load(string assetPath, string projectRoot)
        {
            var tab = new TemplateTab
            {
                AssetPath    = assetPath,
                ManifestPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath))
            };

            string fileName = Path.GetFileName(assetPath);

            try
            {
                string json = File.ReadAllText(tab.ManifestPath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    tab.LoadError = string.Format(InstallerStrings.EmptyFile, fileName);
                    return tab;
                }

                var manifest = JsonUtility.FromJson<TemplateManifest>(json);

                if (manifest == null)
                {
                    tab.LoadError = string.Format(InstallerStrings.ParseFailed, fileName);
                    return tab;
                }

                switch (SchemaVersionPolicy.Check(manifest.schemaVersion))
                {
                    case SchemaCompatibility.Incompatible:
                        tab.LoadError = string.Format(
                            InstallerStrings.SchemaIncompatible,
                            manifest.schemaVersion, SchemaVersionPolicy.SupportedMajor, fileName);
                        return tab;

                    case SchemaCompatibility.CompatibleNewerMinor:
                        tab.LoadWarning = string.Format(
                            InstallerStrings.SchemaNewerMinor,
                            manifest.schemaVersion, SchemaVersionPolicy.CurrentVersion);
                        break;
                }

                if (manifest.packages == null || manifest.packages.Count == 0)
                {
                    tab.LoadError = string.Format(InstallerStrings.NoPackages, fileName);
                    return tab;
                }

                tab.Manifest = manifest;

                foreach (PackageEntry entry in manifest.packages)
                    tab.Rows.Add(new PackageRow { Entry = entry, Status = PackageStatus.Loading });
            }
            catch (Exception ex)
            {
                tab.LoadError = string.Format(InstallerStrings.ParseException, fileName, ex.Message);
            }

            return tab;
        }
    }
}
