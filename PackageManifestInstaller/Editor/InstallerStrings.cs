namespace INVELON.Editor
{
    /// <summary>
    /// Every user-facing string of the installer, centralized so the UI language
    /// can be changed in one place.
    /// </summary>
    internal static class InstallerStrings
    {
        public const string LogPrefix = "[PackageManifestInstaller] ";

        // Window / header
        public const string WindowTitle       = "Dependency Installer";
        public const string HeaderTitle       = "Dependency Installer";
        public const string RefreshButton     = "↺ Refresh";
        public const string RefreshTooltip    = "Re-discover manifests and re-check installed packages";
        public const string LocateButton      = "Locate";
        public const string LocateTooltip     = "Ping the active manifest file in the Project window";
        public const string TemplatesDetected = "{0} template(s) detected   ·   *.dependencies.json";

        // Discovery
        public const string NoManifestsFound =
            "No *.dependencies.json files were found in the project.\n" +
            "Create one following PackageManifest.schema.json (see the package README),\n" +
            "or make sure your template folder is correctly linked into Assets/.";
        public const string RetryDiscovery = "Retry discovery";
        public const string FileLabel      = "File: {0}";

        // Manifest load errors / warnings
        public const string EmptyFile          = "{0} is empty.";
        public const string ParseFailed        = "Could not deserialize {0}.";
        public const string ParseException     = "Error parsing {0}:\n{1}";
        public const string NoPackages         = "{0} contains no packages.";
        public const string SchemaIncompatible =
            "Schema version '{0}' is not supported. This installer supports {1}.x.\n" +
            "Update the schemaVersion field in {2}.";
        public const string SchemaNewerMinor =
            "This manifest declares schema version {0} (newer than the known {1}). " +
            "It will load normally; unknown fields are ignored.";

        // Table
        public const string ManifestInfo    = "Unity {0}  ·  {1}  ·  {2} package(s)";
        public const string SectionRequired = "Required";
        public const string SectionOptional = "Optional";
        public const string ColPackage      = "Package";
        public const string ColRequired     = "Required";
        public const string ColInstalled    = "Installed";
        public const string ColStatus       = "Status";

        // Status badges
        public const string StatusOk       = "✓ OK";
        public const string StatusOutdated = "⚠ Outdated";
        public const string StatusMissing  = "✗ Missing";
        public const string StatusError    = "✗ Error";
        public const string StatusLoading  = "Loading…";

        // Row buttons / notes
        public const string InstallButton   = "Install";
        public const string MarkOkButton    = "Mark OK";
        public const string MarkOkTooltip   = "Manually mark this Asset Store package as installed";
        public const string UnmarkButton    = "Unmark";
        public const string UnmarkTooltip   = "Clear the manual 'installed' mark";
        public const string OpenPageButton  = "Open Page";
        public const string OpenPageTooltip = "Open this asset's page on the Unity Asset Store";
        public const string TarballMissingNote =
            "  ⚠  Place '{0}' in the project's Packages/ folder to enable installation.";
        public const string AssetStoreDefaultNote =
            "Log in to the Unity account that owns the asset and download {0} {1} " +
            "from Package Manager → My Assets.";

        // Footer
        public const string ExportButton       = "Export current state → JSON";
        public const string ExportTooltip      = "Save a *.dependencies.json snapshot of the currently installed packages";
        public const string ExportDialogTitle  = "Export dependencies snapshot";
        public const string PendingCount       = "{0} required package(s) pending";
        public const string InstallingCount    = "Installing… ({0}/{1})";
        public const string InstallPendingButton = "Install pending";

        // Install progress
        public const string ProgressTitle      = "INVELON — Installing dependencies";
        public const string ProgressInstalling = "Installing {0}… ({1}/{2})";

        // Logs
        public const string LogInstallError      = LogPrefix + "Error installing {0}: {1}";
        public const string LogUnknownError      = "Unknown error.";
        public const string LogListError         = "Unknown error while listing packages.";
        public const string LogManifestNotFound  = LogPrefix + "Packages/manifest.json not found.";
        public const string LogTgzNotFound       = LogPrefix + "File '{0}' not found in Packages/. Copy it there before installing.";
        public const string LogRegistryAdded     = LogPrefix + "Scoped registry '{0}' configured for '{1}'.";
        public const string LogRegistryError     = LogPrefix + "Error updating manifest.json: {0}";
        public const string LogTarballRegistered = LogPrefix + "Tarball '{0}' registered in manifest.json as '{1}'.";
        public const string LogTarballError      = LogPrefix + "Error registering tarball '{0}': {1}";
        public const string LogLockCleaned       = LogPrefix + "Stale entry for '{0}' removed from packages-lock.json.";
        public const string LogExported          = LogPrefix + "Exported to: {0}";
        public const string LogExportFailed      = LogPrefix + "Export failed: {0}";
        public const string LogResumedQueue      = LogPrefix + "Resumed pending install queue after domain reload ({0} package(s) remaining).";
        public const string LogInstallCancelled  = LogPrefix + "Installation cancelled by user; {0} package(s) skipped.";
        public const string LogAssetStoreWarning =
            LogPrefix + "'{0}' is an Asset Store package and cannot be installed automatically. " +
            "Download it from Package Manager → My Assets.";
    }
}
