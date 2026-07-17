using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace INVELON.Editor
{
    /// <summary>
    /// Generic multi-template Package Manager installer.
    /// Auto-discovers every *.dependencies.json file in the project (schema v2.x).
    /// Each discovered manifest appears as a tab in the window.
    /// Lives in its own .asmdef with no external references so it compiles
    /// even when other assemblies have errors due to missing packages.
    /// Access via INVELON > Package Manager > Dependency Installer.
    ///
    /// The install queue survives domain reloads (see InstallQueueState), so
    /// "Install pending" completes even when installed packages trigger recompiles.
    /// </summary>
    public class PackageManifestInstaller : EditorWindow
    {
        private const string MenuPath = "INVELON/Package Manager/Dependency Installer";

        // ── State ─────────────────────────────────────────────────────────────

        private List<TemplateTab> _tabs = new List<TemplateTab>();
        private int               _activeTabIdx;
        private string            _discoveryError;

        private ListRequest       _listRequest;
        private AddRequest        _addRequest;
        private readonly Queue<PackageRow> _installQueue = new Queue<PackageRow>();
        private bool              _isInstalling;
        private bool              _cancelRequested;
        private int               _totalToInstall;
        private int               _installedCount;
        private string            _installTabAssetPath;

        private Vector2           _scroll;

        // ── Menu item ─────────────────────────────────────────────────────────

        /// <summary>Opens the Package Manifest Installer window.</summary>
        [MenuItem(MenuPath)]
        public static void OpenWindow()
        {
            var w = GetWindow<PackageManifestInstaller>(InstallerStrings.WindowTitle);
            w.minSize = new Vector2(560, 460);
            w.Show();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            DiscoverAndLoadManifests();

            // If a batch install was interrupted by a domain reload, resume it.
            // ProcessQueue refreshes package statuses when the queue drains.
            if (!TryResumePendingInstall())
                StartListRequest();
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollListRequest;
            EditorApplication.update -= PollAddRequest;
        }

        private void OnDestroy()
        {
            // Window closed (not a domain reload): don't leave a stuck modal progress
            // bar behind. Any pending queue stays in SessionState and resumes the next
            // time the window is opened.
            if (_isInstalling)
                EditorUtility.ClearProgressBar();
        }

        // ── Discovery + manifest loading ──────────────────────────────────────

        private void DiscoverAndLoadManifests()
        {
            _discoveryError = null;
            _tabs.Clear();
            _activeTabIdx = 0;

            string[] guids = AssetDatabase.FindAssets("t:TextAsset");
            List<string> matchingPaths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".dependencies.json", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchingPaths.Count == 0)
            {
                _discoveryError = InstallerStrings.NoManifestsFound;
                return;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string assetPath in matchingPaths)
                _tabs.Add(ManifestLoader.Load(assetPath, projectRoot));
        }

        // ── Package listing ───────────────────────────────────────────────────

        private void StartListRequest()
        {
            if (_tabs.Count == 0) return;

            foreach (TemplateTab tab in _tabs)
                foreach (PackageRow row in tab.Rows)
                    row.Status = PackageStatus.Loading;

            _listRequest = Client.List(offlineMode: false, includeIndirectDependencies: true);
            EditorApplication.update += PollListRequest;
        }

        private void PollListRequest()
        {
            if (_listRequest == null || !_listRequest.IsCompleted) return;

            EditorApplication.update -= PollListRequest;

            if (_listRequest.Status == StatusCode.Success)
            {
                Dictionary<string, string> installedMap = _listRequest.Result
                    .ToDictionary(p => p.name, p => p.version);

                foreach (TemplateTab tab in _tabs)
                    if (tab.Manifest != null)
                        UpdateRowStatuses(tab.Rows, installedMap);
            }
            else
            {
                string errorMsg = _listRequest.Error?.message ?? InstallerStrings.LogListError;
                foreach (TemplateTab tab in _tabs)
                    foreach (PackageRow row in tab.Rows)
                    {
                        row.Status       = PackageStatus.Error;
                        row.ErrorMessage = errorMsg;
                    }
            }

            _listRequest = null;
            Repaint();
        }

        private static void UpdateRowStatuses(List<PackageRow> rows, Dictionary<string, string> installedMap)
        {
            foreach (PackageRow row in rows)
            {
                row.ErrorMessage = null;

                // Asset Store packages are not tracked by UPM — use folder/EditorPrefs detection
                if (row.Entry.source == PackageSourceIds.AssetStore)
                {
                    if (IsAssetStorePackageInstalled(row.Entry))
                    {
                        row.Status           = PackageStatus.Installed;
                        row.InstalledVersion = row.Entry.version;
                    }
                    else
                    {
                        row.Status           = PackageStatus.Missing;
                        row.InstalledVersion = "—";
                    }
                    continue;
                }

                if (!installedMap.TryGetValue(row.Entry.id, out string installedVersion))
                {
                    row.Status           = PackageStatus.Missing;
                    row.InstalledVersion = "—";
                    continue;
                }

                row.InstalledVersion = installedVersion;

                // Git, openupm and tarball: presence check only, no version comparison
                if (row.Entry.source != PackageSourceIds.Registry)
                {
                    row.Status = PackageStatus.Installed;
                    continue;
                }

                // Registry: semantic version comparison
                row.Status = VersionUtil.Satisfies(installedVersion, row.Entry.version)
                    ? PackageStatus.Installed
                    : PackageStatus.VersionLow;
            }
        }

        // ── GUI — top level ───────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawToolHeader();
            DrawSeparator();

            if (!string.IsNullOrEmpty(_discoveryError))
            {
                DrawDiscoveryError();
                return;
            }

            if (_tabs.Count == 0) return;

            DrawTabs();
            DrawSeparator();

            TemplateTab active = _tabs[_activeTabIdx];

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (!string.IsNullOrEmpty(active.LoadError))
                DrawTabError(active);
            else if (active.Manifest != null)
                DrawPackageTable(active);

            EditorGUILayout.EndScrollView();

            if (active.Manifest != null && string.IsNullOrEmpty(active.LoadError))
                DrawFooter(active);
        }

        // ── Tool header ───────────────────────────────────────────────────────

        private void DrawToolHeader()
        {
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    InstallerStrings.HeaderTitle,
                    new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });

                GUILayout.FlexibleSpace();

                if (_tabs.Count > 0 &&
                    GUILayout.Button(
                        new GUIContent(InstallerStrings.LocateButton, InstallerStrings.LocateTooltip),
                        EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    PingTabAsset(_tabs[_activeTabIdx]);
                }

                using (new EditorGUI.DisabledGroupScope(_isInstalling || _listRequest != null))
                {
                    if (GUILayout.Button(
                            new GUIContent(InstallerStrings.RefreshButton, InstallerStrings.RefreshTooltip),
                            EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        DiscoverAndLoadManifests();
                        StartListRequest();
                    }
                }
            }

            EditorGUILayout.LabelField(
                string.Format(InstallerStrings.TemplatesDetected, _tabs.Count),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
        }

        // ── Tab bar ───────────────────────────────────────────────────────────

        private void DrawTabs()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (int i = 0; i < _tabs.Count; i++)
                {
                    TemplateTab tab      = _tabs[i];
                    bool        isActive = i == _activeTabIdx;
                    bool        hasError = !string.IsNullOrEmpty(tab.LoadError);
                    string      name     = tab.Manifest?.templateName ?? Path.GetFileName(tab.AssetPath);
                    string      label    = hasError ? $"⚠ {name}" : name;

                    GUIStyle style = isActive
                        ? new GUIStyle(EditorStyles.toolbarButton) { fontStyle = FontStyle.Bold }
                        : EditorStyles.toolbarButton;

                    if (GUILayout.Toggle(isActive, new GUIContent(label, tab.AssetPath), style, GUILayout.MinWidth(120)))
                        _activeTabIdx = i;
                }

                GUILayout.FlexibleSpace();
            }
        }

        // ── Errors ────────────────────────────────────────────────────────────

        private void DrawTabError(TemplateTab tab)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(tab.LoadError, MessageType.Error);
            EditorGUILayout.LabelField(string.Format(InstallerStrings.FileLabel, tab.AssetPath), EditorStyles.miniLabel);
        }

        private void DrawDiscoveryError()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(_discoveryError, MessageType.Warning);
            EditorGUILayout.Space(4);

            if (GUILayout.Button(InstallerStrings.RetryDiscovery))
            {
                DiscoverAndLoadManifests();
                StartListRequest();
            }
        }

        // ── Package table ─────────────────────────────────────────────────────

        private void DrawPackageTable(TemplateTab tab)
        {
            EditorGUILayout.Space(4);

            if (!string.IsNullOrEmpty(tab.LoadWarning))
                EditorGUILayout.HelpBox(tab.LoadWarning, MessageType.Info);

            if (!string.IsNullOrEmpty(tab.Manifest.unityVersion) ||
                !string.IsNullOrEmpty(tab.Manifest.renderPipeline))
            {
                EditorGUILayout.LabelField(
                    string.Format(InstallerStrings.ManifestInfo,
                        tab.Manifest.unityVersion, tab.Manifest.renderPipeline, tab.Manifest.packages.Count),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);

            List<PackageRow> required = tab.Rows.Where(r => !r.Entry.optional).ToList();
            List<PackageRow> optional = tab.Rows.Where(r =>  r.Entry.optional).ToList();

            DrawPackageSection(InstallerStrings.SectionRequired, required);

            if (optional.Count > 0)
            {
                EditorGUILayout.Space(6);
                DrawPackageSection(InstallerStrings.SectionOptional, optional);
            }
        }

        private void DrawPackageSection(string sectionLabel, List<PackageRow> rows)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField(sectionLabel, EditorStyles.toolbarButton,
                    GUILayout.MinWidth(140), GUILayout.ExpandWidth(true));
                EditorGUILayout.LabelField(InstallerStrings.ColRequired,  EditorStyles.toolbarButton, GUILayout.Width(68));
                EditorGUILayout.LabelField(InstallerStrings.ColInstalled, EditorStyles.toolbarButton, GUILayout.Width(75));
                EditorGUILayout.LabelField(InstallerStrings.ColStatus,    EditorStyles.toolbarButton, GUILayout.Width(90));
                EditorGUILayout.LabelField("", EditorStyles.toolbarButton, GUILayout.Width(166));
            }

            foreach (PackageRow row in rows)
                DrawPackageRow(row);
        }

        private void DrawPackageRow(PackageRow row)
        {
            bool isLoading = row.Status == PackageStatus.Loading || _listRequest != null;

            bool isTarball      = row.Entry.source == PackageSourceIds.Tarball;
            bool tarballMissing = isTarball && !TarballFileExists(row.Entry);
            bool isAssetStore   = row.Entry.source == PackageSourceIds.AssetStore;

            using (new EditorGUILayout.VerticalScope())
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    DrawSourceBadge(row.Entry.source);

                    // Flexible name column — full id + note shown as tooltip
                    EditorGUILayout.LabelField(
                        new GUIContent(row.Entry.displayName ?? row.Entry.id, row.Entry.id),
                        GUILayout.MinWidth(112), GUILayout.ExpandWidth(true));

                    EditorGUILayout.LabelField(row.Entry.version ?? "—", GUILayout.Width(68));

                    EditorGUILayout.LabelField(
                        isLoading ? "…" : (row.InstalledVersion ?? "—"),
                        GUILayout.Width(75));

                    DrawStatusBadge(row.Status, isLoading);

                    // Fixed-width action area keeps all columns aligned across rows
                    using (new EditorGUILayout.HorizontalScope(GUILayout.Width(166)))
                    {
                        if (isAssetStore)
                            DrawAssetStoreActions(row);
                        else
                            DrawInstallAction(row, isLoading, tarballMissing);
                    }
                }

                DrawRowNotes(row, tarballMissing, isAssetStore);

                if (row.Status == PackageStatus.Error && !string.IsNullOrEmpty(row.ErrorMessage))
                    EditorGUILayout.HelpBox(row.ErrorMessage, MessageType.Error);
            }
        }

        private void DrawAssetStoreActions(PackageRow row)
        {
            if (!string.IsNullOrEmpty(row.Entry.assetStoreId))
            {
                if (GUILayout.Button(
                        new GUIContent(InstallerStrings.OpenPageButton, InstallerStrings.OpenPageTooltip),
                        GUILayout.Width(82)))
                {
                    Application.OpenURL($"https://assetstore.unity.com/packages/slug/{row.Entry.assetStoreId}");
                }
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            bool  installed = row.Status == PackageStatus.Installed;
            Color oldBg     = GUI.backgroundColor;
            GUI.backgroundColor = installed ? InstallerColors.Neutral : InstallerColors.Ok;

            GUIContent label = installed
                ? new GUIContent(InstallerStrings.UnmarkButton, InstallerStrings.UnmarkTooltip)
                : new GUIContent(InstallerStrings.MarkOkButton, InstallerStrings.MarkOkTooltip);

            if (GUILayout.Button(label, GUILayout.Width(78)))
            {
                if (installed)
                {
                    EditorPrefs.DeleteKey(AssetStorePrefsKey(row.Entry));
                    row.Status           = PackageStatus.Missing;
                    row.InstalledVersion = "—";
                }
                else
                {
                    EditorPrefs.SetBool(AssetStorePrefsKey(row.Entry), true);
                    row.Status           = PackageStatus.Installed;
                    row.InstalledVersion = row.Entry.version;
                }
            }

            GUI.backgroundColor = oldBg;
        }

        private void DrawInstallAction(PackageRow row, bool isLoading, bool tarballMissing)
        {
            GUILayout.FlexibleSpace();

            bool canInstall = !isLoading && !_isInstalling &&
                              row.Status != PackageStatus.Installed &&
                              !tarballMissing;

            using (new EditorGUI.DisabledGroupScope(!canInstall))
            {
                Color oldBg = GUI.backgroundColor;
                GUI.backgroundColor = canInstall ? InstallerColors.Install : InstallerColors.Neutral;

                if (GUILayout.Button(InstallerStrings.InstallButton, GUILayout.Width(78)))
                    BeginInstall(_tabs[_activeTabIdx], new[] { row });

                GUI.backgroundColor = oldBg;
            }
        }

        private static void DrawRowNotes(PackageRow row, bool tarballMissing, bool isAssetStore)
        {
            if (tarballMissing)
            {
                EditorGUILayout.LabelField(
                    string.Format(InstallerStrings.TarballMissingNote, ResolveTgzFileName(row.Entry)),
                    new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = InstallerColors.Warn } });
            }
            else if (isAssetStore && row.Status != PackageStatus.Installed)
            {
                string note = !string.IsNullOrEmpty(row.Entry.installNote)
                    ? row.Entry.installNote
                    : string.Format(InstallerStrings.AssetStoreDefaultNote,
                        row.Entry.displayName ?? row.Entry.id, row.Entry.version);

                EditorGUILayout.LabelField(
                    $"  ⓘ  {note}",
                    new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = InstallerColors.Warn } });
            }
            else if (!string.IsNullOrEmpty(row.Entry.installNote))
            {
                EditorGUILayout.LabelField(
                    $"  ⓘ  {row.Entry.installNote}",
                    new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });
            }
        }

        private static void DrawSourceBadge(string source)
        {
            string label;
            Color  color;

            switch (source)
            {
                case PackageSourceIds.Git:        label = "GIT"; color = InstallerColors.SourceGit;        break;
                case PackageSourceIds.OpenUpm:    label = "UPM"; color = InstallerColors.SourceOpenUpm;    break;
                case PackageSourceIds.Tarball:    label = "TGZ"; color = InstallerColors.SourceTarball;    break;
                case PackageSourceIds.AssetStore: label = "AST"; color = InstallerColors.SourceAssetStore; break;
                default:                          label = "REG"; color = InstallerColors.SourceRegistry;   break;
            }

            Color oldContent = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(new GUIContent(label, source),
                new GUIStyle(EditorStyles.boldLabel) { fontSize = 9, alignment = TextAnchor.MiddleCenter },
                GUILayout.Width(28));
            GUI.contentColor = oldContent;
        }

        private static void DrawStatusBadge(PackageStatus status, bool isLoading)
        {
            string label;
            Color  color;

            if (isLoading)
            {
                label = InstallerStrings.StatusLoading;
                color = InstallerColors.Neutral;
            }
            else
            {
                switch (status)
                {
                    case PackageStatus.Installed:  label = InstallerStrings.StatusOk;       color = InstallerColors.Ok;      break;
                    case PackageStatus.VersionLow: label = InstallerStrings.StatusOutdated; color = InstallerColors.Warn;    break;
                    case PackageStatus.Missing:    label = InstallerStrings.StatusMissing;  color = InstallerColors.Error;   break;
                    case PackageStatus.Error:      label = InstallerStrings.StatusError;    color = InstallerColors.Error;   break;
                    default:                       label = "…";                        color = InstallerColors.Neutral; break;
                }
            }

            Color oldContent = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.contentColor = oldContent;
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter(TemplateTab tab)
        {
            DrawSeparator();

            bool isLoading    = _listRequest != null;
            int  pendingCount = tab.Rows.Count(r => !r.Entry.optional &&
                                                    r.Entry.source != PackageSourceIds.AssetStore &&
                                                    r.Status != PackageStatus.Installed);

            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                Color oldBg = GUI.backgroundColor;
                GUI.backgroundColor = InstallerColors.Export;

                using (new EditorGUI.DisabledGroupScope(isLoading || _isInstalling))
                {
                    if (GUILayout.Button(
                            new GUIContent(InstallerStrings.ExportButton, InstallerStrings.ExportTooltip),
                            EditorStyles.miniButton, GUILayout.Width(200)))
                        ExportCurrentPackages(tab);
                }

                GUI.backgroundColor = oldBg;

                GUILayout.FlexibleSpace();

                if (_isInstalling)
                {
                    EditorGUILayout.LabelField(
                        string.Format(InstallerStrings.InstallingCount, _installedCount, _totalToInstall),
                        EditorStyles.boldLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        string.Format(InstallerStrings.PendingCount, pendingCount),
                        EditorStyles.miniLabel);
                }

                bool canInstallAll = !isLoading && !_isInstalling && pendingCount > 0;

                oldBg = GUI.backgroundColor;
                GUI.backgroundColor = canInstallAll ? InstallerColors.Install : InstallerColors.Neutral;

                using (new EditorGUI.DisabledGroupScope(!canInstallAll))
                {
                    if (GUILayout.Button(InstallerStrings.InstallPendingButton,
                            GUILayout.Height(30), GUILayout.Width(160)))
                    {
                        BeginInstall(tab, tab.Rows.Where(r => !r.Entry.optional &&
                                                              r.Status != PackageStatus.Installed &&
                                                              r.Entry.source != PackageSourceIds.AssetStore));
                    }
                }

                GUI.backgroundColor = oldBg;
            }

            EditorGUILayout.Space(8);
        }

        // ── Installation logic ────────────────────────────────────────────────

        private void BeginInstall(TemplateTab tab, IEnumerable<PackageRow> rows)
        {
            _installQueue.Clear();
            foreach (PackageRow row in rows)
                _installQueue.Enqueue(row);

            if (_installQueue.Count == 0) return;

            _installTabAssetPath = tab.AssetPath;
            _totalToInstall      = _installQueue.Count;
            _installedCount      = 0;
            _isInstalling        = true;
            _cancelRequested     = false;
            ProcessQueue();
        }

        /// <summary>Resumes a batch install that was interrupted by a domain reload.</summary>
        private bool TryResumePendingInstall()
        {
            if (!InstallQueueState.TryLoad(out string tabPath, out int installed, out int total,
                                           out List<string> pendingIds))
                return false;

            TemplateTab tab = _tabs.FirstOrDefault(t => t.AssetPath == tabPath && t.Manifest != null);
            if (tab == null)
            {
                InstallQueueState.Clear();
                return false;
            }

            _installQueue.Clear();
            foreach (string id in pendingIds)
            {
                PackageRow row = tab.Rows.FirstOrDefault(r => r.Entry.id == id);
                if (row != null) _installQueue.Enqueue(row);
            }

            if (_installQueue.Count == 0)
            {
                InstallQueueState.Clear();
                return false;
            }

            _activeTabIdx        = _tabs.IndexOf(tab);
            _installTabAssetPath = tab.AssetPath;
            _installedCount      = installed;
            _totalToInstall      = total;
            _isInstalling        = true;
            _cancelRequested     = false;

            Debug.Log(string.Format(InstallerStrings.LogResumedQueue, _installQueue.Count));
            ProcessQueue();
            return true;
        }

        private void ProcessQueue()
        {
            if (_cancelRequested && _installQueue.Count > 0)
            {
                Debug.Log(string.Format(InstallerStrings.LogInstallCancelled, _installQueue.Count));
                _installQueue.Clear();
            }

            if (_installQueue.Count == 0)
            {
                FinishInstall();
                return;
            }

            // Persist BEFORE Client.Add: if the install triggers a domain reload,
            // OnEnable picks the queue back up from here.
            InstallQueueState.Save(_installTabAssetPath, _installedCount, _totalToInstall,
                                   _installQueue.Select(r => r.Entry.id));

            PackageRow row = _installQueue.Peek();
            ShowInstallProgress(row);

            if (row.Entry.source == PackageSourceIds.OpenUpm)
                EnsureOpenUpmScopedRegistry(row.Entry);

            if (row.Entry.source == PackageSourceIds.Tarball && !EnsureTarballInManifest(row.Entry))
            {
                row.Status       = PackageStatus.Error;
                row.ErrorMessage = string.Format(InstallerStrings.LogTgzNotFound, ResolveTgzFileName(row.Entry));
                _installQueue.Dequeue();
                _installedCount++;
                ProcessQueue();
                return;
            }

            _addRequest = Client.Add(BuildPackageIdentifier(row.Entry));
            EditorApplication.update += PollAddRequest;
        }

        private void PollAddRequest()
        {
            if (_addRequest == null)
            {
                EditorApplication.update -= PollAddRequest;
                return;
            }

            if (!_addRequest.IsCompleted)
            {
                // Keep the (cancelable) progress bar alive while UPM works.
                if (_installQueue.Count > 0)
                    ShowInstallProgress(_installQueue.Peek());
                return;
            }

            EditorApplication.update -= PollAddRequest;

            PackageRow row = _installQueue.Dequeue();
            _installedCount++;

            if (_addRequest.Status == StatusCode.Success)
            {
                row.Status           = PackageStatus.Installed;
                row.InstalledVersion = _addRequest.Result?.version ?? row.Entry.version;
                row.ErrorMessage     = null;
            }
            else
            {
                row.Status       = PackageStatus.Error;
                row.ErrorMessage = _addRequest.Error?.message ?? InstallerStrings.LogUnknownError;
                Debug.LogError(string.Format(InstallerStrings.LogInstallError, row.Entry.id, row.ErrorMessage));
            }

            _addRequest = null;
            Repaint();
            ProcessQueue();
        }

        private void ShowInstallProgress(PackageRow row)
        {
            float progress = _totalToInstall > 0 ? (float)_installedCount / _totalToInstall : 0f;
            bool  cancel   = EditorUtility.DisplayCancelableProgressBar(
                InstallerStrings.ProgressTitle,
                string.Format(InstallerStrings.ProgressInstalling,
                    row.Entry.displayName ?? row.Entry.id, _installedCount + 1, _totalToInstall),
                progress);

            if (cancel) _cancelRequested = true;
        }

        private void FinishInstall()
        {
            _isInstalling    = false;
            _cancelRequested = false;
            InstallQueueState.Clear();
            EditorUtility.ClearProgressBar();
            StartListRequest();
        }

        // ── Package identifiers ───────────────────────────────────────────────

        private static string BuildPackageIdentifier(PackageEntry entry)
        {
            switch (entry.source)
            {
                case PackageSourceIds.Git:
                    return entry.url;

                case PackageSourceIds.Tarball:
                {
                    string fileName    = ResolveTgzFileName(entry);
                    string packagesDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages"));
                    string fullPath    = Path.Combine(packagesDir, fileName);
                    return $"file:{fullPath.Replace('\\', '/')}";
                }

                case PackageSourceIds.AssetStore:
                    // Unreachable: the Install button is never shown for assetstore entries.
                    Debug.LogWarning(string.Format(InstallerStrings.LogAssetStoreWarning, entry.id));
                    return entry.id;

                default:
                    return $"{entry.id}@{entry.version}";
            }
        }

        // ── Asset Store detection ─────────────────────────────────────────────

        private static string AssetStorePrefsKey(PackageEntry entry) =>
            $"INVELON.PMI.AssetStoreInstalled.{entry.id}";

        /// <summary>
        /// An Asset Store package counts as installed when:
        ///   1. The dev has clicked "Mark OK" (stored in EditorPrefs), OR
        ///   2. assetFolderPath is set and the folder exists under Assets/.
        /// </summary>
        private static bool IsAssetStorePackageInstalled(PackageEntry entry)
        {
            if (EditorPrefs.GetBool(AssetStorePrefsKey(entry), false))
                return true;

            if (!string.IsNullOrEmpty(entry.assetFolderPath))
            {
                string fullPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, entry.assetFolderPath));
                return Directory.Exists(fullPath);
            }

            return false;
        }

        // ── Tarball helpers ───────────────────────────────────────────────────

        private static string ResolveTgzFileName(PackageEntry entry) =>
            !string.IsNullOrEmpty(entry.tgzFileName)
                ? entry.tgzFileName
                : $"{entry.id}-{entry.version}.tgz";

        private static bool TarballFileExists(PackageEntry entry)
        {
            string packagesDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages"));
            return File.Exists(Path.Combine(packagesDir, ResolveTgzFileName(entry)));
        }

        // ── manifest.json edits (delegated to UpmManifestJson) ────────────────

        /// <summary>
        /// Ensures the scoped registry for an openupm entry exists in Packages/manifest.json.
        /// If a registry with the same URL exists, the scope is appended to it — no
        /// duplicate registry blocks are created.
        /// </summary>
        private static void EnsureOpenUpmScopedRegistry(PackageEntry entry)
        {
            string manifestPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "../Packages/manifest.json"));

            if (!File.Exists(manifestPath))
            {
                Debug.LogError(InstallerStrings.LogManifestNotFound);
                return;
            }

            try
            {
                string content      = File.ReadAllText(manifestPath);
                string registryName = !string.IsNullOrEmpty(entry.scopeName) ? entry.scopeName : entry.url;

                string updated = UpmManifestJson.AddScopedRegistry(
                    content, registryName, entry.url, entry.id, out bool changed);

                if (changed)
                {
                    File.WriteAllText(manifestPath, updated);
                    AssetDatabase.Refresh();
                    Debug.Log(string.Format(InstallerStrings.LogRegistryAdded, entry.url, entry.id));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format(InstallerStrings.LogRegistryError, ex.Message));
            }
        }

        /// <summary>
        /// Registers a tarball package in Packages/manifest.json (file: ref) and removes
        /// any stale entry from packages-lock.json so UPM re-resolves it cleanly.
        /// Returns false when the .tgz file or manifest.json is missing.
        /// </summary>
        private static bool EnsureTarballInManifest(PackageEntry entry)
        {
            string packagesDir  = Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages"));
            string manifestPath = Path.Combine(packagesDir, "manifest.json");
            string lockPath     = Path.Combine(packagesDir, "packages-lock.json");
            string fileName     = ResolveTgzFileName(entry);
            string fileRef      = $"file:./{fileName}";

            if (!File.Exists(manifestPath))
            {
                Debug.LogError(InstallerStrings.LogManifestNotFound);
                return false;
            }

            if (!File.Exists(Path.Combine(packagesDir, fileName)))
            {
                Debug.LogError(string.Format(InstallerStrings.LogTgzNotFound, fileName));
                return false;
            }

            try
            {
                string content = File.ReadAllText(manifestPath);

                if (!UpmManifestJson.IsTarballRegistered(content, entry.id))
                {
                    content = UpmManifestJson.SetDependency(content, entry.id, fileRef, out bool changed);
                    if (changed)
                    {
                        File.WriteAllText(manifestPath, content);
                        Debug.Log(string.Format(InstallerStrings.LogTarballRegistered, fileName, fileRef));
                    }
                }

                if (File.Exists(lockPath))
                {
                    string lockContent = File.ReadAllText(lockPath);
                    string newLock     = UpmManifestJson.RemoveDependencyEntry(lockContent, entry.id, out bool lockChanged);
                    if (lockChanged)
                    {
                        File.WriteAllText(lockPath, newLock);
                        Debug.Log(string.Format(InstallerStrings.LogLockCleaned, entry.id));
                    }
                }

                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format(InstallerStrings.LogTarballError, entry.id, ex.Message));
                return false;
            }
        }

        // ── Export current packages ───────────────────────────────────────────

        /// <summary>
        /// Reads the currently installed packages and writes a snapshot *.dependencies.json.
        /// The user picks the destination via a save dialog (so exports don't silently
        /// pile up next to the manifest and appear as bogus new tabs).
        /// </summary>
        private void ExportCurrentPackages(TemplateTab tab)
        {
            string defaultDir  = Path.GetDirectoryName(tab.ManifestPath) ?? Application.dataPath;
            string defaultName = $"exported-{DateTime.Now:yyyy-MM-dd-HHmm}.dependencies";
            string outputPath  = EditorUtility.SaveFilePanel(
                InstallerStrings.ExportDialogTitle, defaultDir, defaultName, "json");

            if (string.IsNullOrEmpty(outputPath)) return;

            ListRequest exportRequest = Client.List(offlineMode: false, includeIndirectDependencies: false);

            EditorApplication.CallbackFunction pollDelegate = null;
            pollDelegate = () =>
            {
                if (!exportRequest.IsCompleted) return;

                EditorApplication.update -= pollDelegate;

                if (exportRequest.Status != StatusCode.Success)
                {
                    Debug.LogError(string.Format(InstallerStrings.LogExportFailed, exportRequest.Error?.message));
                    return;
                }

                string upmManifestPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "../Packages/manifest.json"));

                var openUpmScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(upmManifestPath))
                {
                    try
                    {
                        openUpmScopes = UpmManifestJson.GetScopesForRegistriesMatching(
                            File.ReadAllText(upmManifestPath), "openupm");
                    }
                    catch { /* malformed manifest — treat everything as registry */ }
                }

                IEnumerable<ExportPackage> exportPackages = exportRequest.Result.Select(p => new ExportPackage
                {
                    Id          = p.name,
                    Version     = p.version,
                    DisplayName = p.displayName,
                    GitUrl      = p.source == PackageSource.Git ? ExtractGitUrl(p.packageId) : null
                });

                string json = ExportJsonBuilder.Build(
                    tab.Manifest, Application.unityVersion, exportPackages, openUpmScopes);

                File.WriteAllText(outputPath, json);
                Debug.Log(string.Format(InstallerStrings.LogExported, outputPath));

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                                         .Replace('\\', '/');
                bool insideProject = outputPath.Replace('\\', '/')
                                               .StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase);

                if (insideProject)
                {
                    AssetDatabase.Refresh();
                    DiscoverAndLoadManifests();
                    StartListRequest();
                }
                else
                {
                    EditorUtility.RevealInFinder(outputPath);
                }

                Repaint();
            };

            EditorApplication.update += pollDelegate;
        }

        /// <summary>Strips the "name@" prefix from a UPM packageId, leaving the git URL.</summary>
        private static string ExtractGitUrl(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return packageId;
            int at = packageId.IndexOf('@');
            return at >= 0 ? packageId.Substring(at + 1) : packageId;
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        private static void PingTabAsset(TemplateTab tab)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(tab.AssetPath);
            if (asset == null) return;
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(4);
            Rect r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, InstallerColors.Separator);
            EditorGUILayout.Space(4);
        }
    }
}
