using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace INVELON.Editor
{
    /// <summary>
    /// Generic multi-template Package Manager installer.
    /// Auto-discovers every *.dependencies.json file in the project (schema v2.0).
    /// Each discovered manifest appears as a tab in the window.
    /// Lives in its own .asmdef with no external references so it compiles
    /// even when other assemblies have errors due to missing packages.
    /// Access via INVELON > Package Manager > Dependency Installer.
    /// </summary>
    public class PackageManifestInstaller : EditorWindow
    {
        // ──────────────────────────────────────────────────────────────────────────
        //  Schema data model  (matches PackageManifest.schema.json  v2.0)
        //  Uses [Serializable] + public fields for JsonUtility compatibility.
        // ──────────────────────────────────────────────────────────────────────────

        [Serializable]
        private class TemplateManifest
        {
            public string            schemaVersion;
            public string            templateName;
            public string            unityVersion;
            public string            renderPipeline;
            public string            menuGroup;
            public List<PackageEntry> packages = new List<PackageEntry>();
        }

        [Serializable]
        private class PackageEntry
        {
            public string id;
            public string version;
            public string source;       // "registry" | "git" | "openupm"
            public string url;
            public string scopeName;
            public string displayName;
            public bool   optional;
            public string installNote;
        }

        private enum PackageStatus { Loading, Installed, VersionLow, Missing, Error }

        private class PackageRow
        {
            public PackageEntry  Entry;
            public PackageStatus Status;
            public string        InstalledVersion;
            public string        ErrorMessage;
        }

        /// <summary>One discovered *.dependencies.json file and its parsed rows.</summary>
        private class TemplateTab
        {
            public string            ManifestPath;
            public string            AssetPath;
            public TemplateManifest  Manifest;
            public string            LoadError;
            public List<PackageRow>  Rows = new List<PackageRow>();
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Constants
        // ──────────────────────────────────────────────────────────────────────────

        private const string SupportedSchemaVersion = "2.0";
        private const string MenuPath               = "INVELON/Package Manager/Dependency Installer";

        private static readonly Color ColorOk      = new Color(0.35f, 0.80f, 0.35f);
        private static readonly Color ColorWarn    = new Color(0.95f, 0.75f, 0.20f);
        private static readonly Color ColorError   = new Color(0.90f, 0.35f, 0.35f);
        private static readonly Color ColorNeutral = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color ColorInstall = new Color(0.25f, 0.55f, 0.90f);
        private static readonly Color ColorExport  = new Color(0.45f, 0.45f, 0.65f);

        // ──────────────────────────────────────────────────────────────────────────
        //  State
        // ──────────────────────────────────────────────────────────────────────────

        private List<TemplateTab>  _tabs         = new List<TemplateTab>();
        private int                _activeTabIdx = 0;
        private string             _discoveryError;

        private ListRequest        _listRequest;
        private AddRequest         _addRequest;
        private Queue<PackageRow>  _installQueue = new Queue<PackageRow>();
        private bool               _isInstalling;
        private int                _totalToInstall;
        private int                _installedCount;

        private Vector2            _scroll;

        // ──────────────────────────────────────────────────────────────────────────
        //  Menu item
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>Opens the Package Manifest Installer window.</summary>
        [MenuItem(MenuPath)]
        public static void OpenWindow()
        {
            var w = GetWindow<PackageManifestInstaller>("Dependency Installer");
            w.minSize = new Vector2(560, 460);
            w.Show();
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ──────────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            DiscoverAndLoadManifests();
            StartListRequest();
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollListRequest;
            EditorApplication.update -= PollAddRequest;
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Discovery + manifest loading
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>Finds every *.dependencies.json TextAsset in the project and loads it.</summary>
        private void DiscoverAndLoadManifests()
        {
            _discoveryError = null;
            _tabs.Clear();
            _activeTabIdx = 0;

            string[] guids = AssetDatabase.FindAssets("t:TextAsset");
            List<string> matchingPaths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".dependencies.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .ToList();

            if (matchingPaths.Count == 0)
            {
                _discoveryError = "No se encontraron archivos *.dependencies.json en el proyecto.\n" +
                                  "Asegurate de que el XLINK de _VRTemplate esta correctamente aplicado,\n" +
                                  "o crea un nuevo archivo siguiendo PackageManifest.schema.json.";
                return;
            }

            foreach (string assetPath in matchingPaths)
                _tabs.Add(LoadTab(assetPath));
        }

        private TemplateTab LoadTab(string assetPath)
        {
            var tab = new TemplateTab { AssetPath = assetPath };
            tab.ManifestPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", assetPath));

            try
            {
                string json = File.ReadAllText(tab.ManifestPath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    tab.LoadError = $"{Path.GetFileName(assetPath)} esta vacio.";
                    return tab;
                }

                var manifest = JsonUtility.FromJson<TemplateManifest>(json);

                if (manifest == null)
                {
                    tab.LoadError = $"No se pudo deserializar {Path.GetFileName(assetPath)}.";
                    return tab;
                }

                if (manifest.schemaVersion != SupportedSchemaVersion)
                {
                    tab.LoadError = $"Schema version '{manifest.schemaVersion}' no compatible. " +
                                    $"Se requiere '{SupportedSchemaVersion}'.\n" +
                                    $"Actualiza el campo schemaVersion en {Path.GetFileName(assetPath)}.";
                    return tab;
                }

                if (manifest.packages == null || manifest.packages.Count == 0)
                {
                    tab.LoadError = $"{Path.GetFileName(assetPath)} no contiene paquetes.";
                    return tab;
                }

                tab.Manifest = manifest;

                foreach (PackageEntry entry in manifest.packages)
                    tab.Rows.Add(new PackageRow { Entry = entry, Status = PackageStatus.Loading });
            }
            catch (Exception ex)
            {
                tab.LoadError = $"Error al parsear {Path.GetFileName(assetPath)}:\n{ex.Message}";
            }

            return tab;
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Package listing
        // ──────────────────────────────────────────────────────────────────────────

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
                string errorMsg = _listRequest.Error?.message ?? "Error desconocido al listar paquetes.";
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

        private void UpdateRowStatuses(List<PackageRow> rows, Dictionary<string, string> installedMap)
        {
            foreach (PackageRow row in rows)
            {
                row.ErrorMessage = null;

                if (!installedMap.TryGetValue(row.Entry.id, out string installedVersion))
                {
                    row.Status           = PackageStatus.Missing;
                    row.InstalledVersion = "—";
                    continue;
                }

                row.InstalledVersion = installedVersion;

                // Git and openupm: presence check only, no version comparison
                if (row.Entry.source != "registry")
                {
                    row.Status = PackageStatus.Installed;
                    continue;
                }

                // Registry: semantic version comparison
                if (TryParseVersion(installedVersion, out Version installed) &&
                    TryParseVersion(row.Entry.version,  out Version required))
                {
                    row.Status = installed >= required ? PackageStatus.Installed : PackageStatus.VersionLow;
                }
                else
                {
                    int cmp = string.Compare(installedVersion, row.Entry.version, StringComparison.OrdinalIgnoreCase);
                    row.Status = cmp >= 0 ? PackageStatus.Installed : PackageStatus.VersionLow;
                }
            }
        }

        /// <summary>Parses a version string, stripping pre-release suffixes (e.g. "1.5.9-pre.1" → 1.5.9).</summary>
        private static bool TryParseVersion(string raw, out Version version)
        {
            int    dashIdx = raw.IndexOf('-');
            string clean   = dashIdx >= 0 ? raw.Substring(0, dashIdx) : raw;
            return Version.TryParse(clean, out version);
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  GUI — top level
        // ──────────────────────────────────────────────────────────────────────────

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

        // ── Tool header ───────────────────────────────────────────────────────────

        private void DrawToolHeader()
        {
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "Dependency Installer",
                    new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledGroupScope(_isInstalling || _listRequest != null))
                {
                    if (GUILayout.Button("↺ Refrescar", EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        DiscoverAndLoadManifests();
                        StartListRequest();
                    }
                }
            }

            EditorGUILayout.LabelField(
                $"{_tabs.Count} plantilla(s) detectada(s)   ·   *.dependencies.json",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
        }

        // ── Tab bar ───────────────────────────────────────────────────────────────

        private void DrawTabs()
        {
            string[] labels = _tabs
                .Select(t => t.Manifest?.templateName ?? Path.GetFileName(t.AssetPath))
                .ToArray();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    bool   isActive = i == _activeTabIdx;
                    bool   hasError = !string.IsNullOrEmpty(_tabs[i].LoadError);
                    string label    = hasError ? $"⚠ {labels[i]}" : labels[i];

                    GUIStyle style = isActive
                        ? new GUIStyle(EditorStyles.toolbarButton) { fontStyle = FontStyle.Bold }
                        : EditorStyles.toolbarButton;

                    if (GUILayout.Toggle(isActive, label, style, GUILayout.MinWidth(120)))
                        _activeTabIdx = i;

                    if (Event.current.type == EventType.MouseDown &&
                        GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        PingTabAsset(_tabs[i]);
                        Event.current.Use();
                    }
                }

                GUILayout.FlexibleSpace();
            }
        }

        // ── Tab error ─────────────────────────────────────────────────────────────

        private void DrawTabError(TemplateTab tab)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(tab.LoadError, MessageType.Error);
            EditorGUILayout.LabelField($"Archivo: {tab.AssetPath}", EditorStyles.miniLabel);
        }

        // ── Discovery error ───────────────────────────────────────────────────────

        private void DrawDiscoveryError()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(_discoveryError, MessageType.Warning);
            EditorGUILayout.Space(4);

            if (GUILayout.Button("Reintentar descubrimiento"))
            {
                DiscoverAndLoadManifests();
                StartListRequest();
            }
        }

        // ── Package table ─────────────────────────────────────────────────────────

        private void DrawPackageTable(TemplateTab tab)
        {
            EditorGUILayout.Space(4);

            if (!string.IsNullOrEmpty(tab.Manifest.unityVersion) ||
                !string.IsNullOrEmpty(tab.Manifest.renderPipeline))
            {
                EditorGUILayout.LabelField(
                    $"Unity {tab.Manifest.unityVersion}  ·  {tab.Manifest.renderPipeline}  " +
                    $"·  {tab.Manifest.packages.Count} paquete(s)",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);

            List<PackageRow> required = tab.Rows.Where(r => !r.Entry.optional).ToList();
            List<PackageRow> optional = tab.Rows.Where(r =>  r.Entry.optional).ToList();

            DrawPackageSection("Requeridos", required);

            if (optional.Count > 0)
            {
                EditorGUILayout.Space(6);
                DrawPackageSection("Opcionales", optional);
            }
        }

        private void DrawPackageSection(string sectionLabel, List<PackageRow> rows)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField(sectionLabel, EditorStyles.toolbarButton, GUILayout.Width(90));
                EditorGUILayout.LabelField("Paquete",    EditorStyles.toolbarButton, GUILayout.Width(185));
                EditorGUILayout.LabelField("Requerida",  EditorStyles.toolbarButton, GUILayout.Width(68));
                EditorGUILayout.LabelField("Instalada",  EditorStyles.toolbarButton, GUILayout.Width(75));
                EditorGUILayout.LabelField("Estado",     EditorStyles.toolbarButton, GUILayout.Width(90));
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("",           EditorStyles.toolbarButton, GUILayout.Width(80));
            }

            foreach (PackageRow row in rows)
                DrawPackageRow(row);
        }

        private void DrawPackageRow(PackageRow row)
        {
            bool isLoading = row.Status == PackageStatus.Loading || _listRequest != null;

            using (new EditorGUILayout.VerticalScope())
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    DrawSourceBadge(row.Entry.source);

                    EditorGUILayout.LabelField(
                        new GUIContent(row.Entry.displayName ?? row.Entry.id, row.Entry.id),
                        GUILayout.Width(185));

                    EditorGUILayout.LabelField(row.Entry.version, GUILayout.Width(68));

                    EditorGUILayout.LabelField(
                        isLoading ? "\u2026" : (row.InstalledVersion ?? "—"),
                        GUILayout.Width(75));

                    DrawStatusBadge(row.Status, isLoading);

                    GUILayout.FlexibleSpace();

                    bool canInstall = !isLoading && !_isInstalling &&
                                      row.Status != PackageStatus.Installed;

                    using (new EditorGUI.DisabledGroupScope(!canInstall))
                    {
                        Color oldBg = GUI.backgroundColor;
                        GUI.backgroundColor = canInstall ? ColorInstall : ColorNeutral;

                        if (GUILayout.Button("Instalar", GUILayout.Width(78)))
                            InstallSingle(row);

                        GUI.backgroundColor = oldBg;
                    }
                }

                if (!string.IsNullOrEmpty(row.Entry.installNote))
                {
                    EditorGUILayout.LabelField(
                        $"  \u24d8  {row.Entry.installNote}",
                        new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });
                }

                if (row.Status == PackageStatus.Error && !string.IsNullOrEmpty(row.ErrorMessage))
                    EditorGUILayout.HelpBox(row.ErrorMessage, MessageType.Error);
            }
        }

        private void DrawSourceBadge(string source)
        {
            string label;
            Color  color;

            switch (source)
            {
                case "git":
                    label = "GIT";
                    color = new Color(0.30f, 0.65f, 0.90f);
                    break;
                case "openupm":
                    label = "UPM";
                    color = new Color(0.65f, 0.45f, 0.90f);
                    break;
                default:
                    label = "REG";
                    color = new Color(0.50f, 0.65f, 0.50f);
                    break;
            }

            Color oldContent = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(label,
                new GUIStyle(EditorStyles.boldLabel) { fontSize = 9, alignment = TextAnchor.MiddleCenter },
                GUILayout.Width(28));
            GUI.contentColor = oldContent;
        }

        private void DrawStatusBadge(PackageStatus status, bool isLoading)
        {
            string label;
            Color  color;

            if (isLoading)
            {
                label = "Cargando\u2026";
                color = ColorNeutral;
            }
            else
            {
                switch (status)
                {
                    case PackageStatus.Installed:  label = "\u2713 OK";       color = ColorOk;      break;
                    case PackageStatus.VersionLow: label = "\u26a0 Inferior"; color = ColorWarn;    break;
                    case PackageStatus.Missing:    label = "\u2717 Falta";    color = ColorError;   break;
                    case PackageStatus.Error:      label = "\u2717 Error";    color = ColorError;   break;
                    default:                       label = "\u2026";          color = ColorNeutral; break;
                }
            }

            Color oldContent = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.contentColor = oldContent;
        }

        // ── Footer ────────────────────────────────────────────────────────────────

        private void DrawFooter(TemplateTab tab)
        {
            DrawSeparator();

            bool isLoading    = _listRequest != null;
            int  pendingCount = tab.Rows.Count(r => !r.Entry.optional && r.Status != PackageStatus.Installed);

            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                Color oldBg = GUI.backgroundColor;
                GUI.backgroundColor = ColorExport;

                using (new EditorGUI.DisabledGroupScope(isLoading || _isInstalling))
                {
                    if (GUILayout.Button("Exportar estado actual \u2192 JSON",
                            EditorStyles.miniButton, GUILayout.Width(200)))
                        ExportCurrentPackages(tab);
                }

                GUI.backgroundColor = oldBg;

                GUILayout.FlexibleSpace();

                if (_isInstalling)
                {
                    EditorGUILayout.LabelField(
                        $"Instalando\u2026 ({_installedCount}/{_totalToInstall})",
                        EditorStyles.boldLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        $"{pendingCount} paquete(s) requerido(s) pendiente(s)",
                        EditorStyles.miniLabel);
                }

                bool canInstallAll = !isLoading && !_isInstalling && pendingCount > 0;

                oldBg = GUI.backgroundColor;
                GUI.backgroundColor = canInstallAll ? ColorInstall : ColorNeutral;

                using (new EditorGUI.DisabledGroupScope(!canInstallAll))
                {
                    if (GUILayout.Button("Instalar pendientes",
                            GUILayout.Height(30), GUILayout.Width(160)))
                        InstallAll(tab);
                }

                GUI.backgroundColor = oldBg;
            }

            EditorGUILayout.Space(8);
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Installation logic
        // ──────────────────────────────────────────────────────────────────────────

        private void InstallSingle(PackageRow row)
        {
            _installQueue.Clear();
            _installQueue.Enqueue(row);
            _totalToInstall = 1;
            _installedCount = 0;
            _isInstalling   = true;
            ProcessQueue();
        }

        private void InstallAll(TemplateTab tab)
        {
            _installQueue.Clear();

            foreach (PackageRow row in tab.Rows.Where(r => !r.Entry.optional && r.Status != PackageStatus.Installed))
                _installQueue.Enqueue(row);

            _totalToInstall = _installQueue.Count;
            _installedCount = 0;
            _isInstalling   = true;
            ProcessQueue();
        }

        private void ProcessQueue()
        {
            if (_installQueue.Count == 0)
            {
                _isInstalling = false;
                EditorUtility.ClearProgressBar();
                StartListRequest();
                return;
            }

            PackageRow row       = _installQueue.Peek();
            string     packageId = BuildPackageIdentifier(row.Entry);

            EditorUtility.DisplayProgressBar(
                "INVELON — Instalando dependencias",
                $"Instalando {row.Entry.displayName ?? row.Entry.id}\u2026",
                _totalToInstall > 0 ? (float)_installedCount / _totalToInstall : 0f);

            if (row.Entry.source == "openupm")
                EnsureOpenUpmScopedRegistry(row.Entry);

            _addRequest = Client.Add(packageId);
            EditorApplication.update += PollAddRequest;
        }

        private void PollAddRequest()
        {
            if (_addRequest == null || !_addRequest.IsCompleted) return;

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
                row.ErrorMessage = _addRequest.Error?.message ?? "Error desconocido.";
                Debug.LogError($"[PackageManifestInstaller] Error instalando {row.Entry.id}: {row.ErrorMessage}");
            }

            _addRequest = null;
            Repaint();
            ProcessQueue();
        }

        /// <summary>Builds the package identifier for Client.Add based on source type.</summary>
        private static string BuildPackageIdentifier(PackageEntry entry)
        {
            return entry.source == "git" ? entry.url : $"{entry.id}@{entry.version}";
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  OpenUPM scoped registry  (string-based, no external JSON library needed)
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the OpenUPM scoped registry for the given entry exists in Packages/manifest.json.
        /// Uses string manipulation instead of a JSON library so this assembly has zero external dependencies.
        /// </summary>
        private static void EnsureOpenUpmScopedRegistry(PackageEntry entry)
        {
            string manifestPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "../Packages/manifest.json"));

            if (!File.Exists(manifestPath))
            {
                Debug.LogError("[PackageManifestInstaller] Packages/manifest.json no encontrado.");
                return;
            }

            try
            {
                string content = File.ReadAllText(manifestPath);

                // Bail out if both the registry URL and the package scope are already present
                if (content.Contains($"\"{entry.url}\"") && content.Contains($"\"{entry.id}\""))
                {
                    Debug.Log($"[PackageManifestInstaller] Scoped registry para '{entry.id}' ya existe.");
                    return;
                }

                string registryName = !string.IsNullOrEmpty(entry.scopeName) ? entry.scopeName : entry.url;

                string newBlock =
                    "{\n" +
                    $"      \"name\": \"{registryName}\",\n" +
                    $"      \"url\": \"{entry.url}\",\n" +
                    $"      \"scopes\": [\n" +
                    $"        \"{entry.id}\"\n" +
                    $"      ]\n" +
                    $"    }}";

                if (content.Contains("\"scopedRegistries\""))
                {
                    // Insert at the start of the existing scopedRegistries array
                    int keyIdx     = content.IndexOf("\"scopedRegistries\"", StringComparison.Ordinal);
                    int bracketIdx = content.IndexOf('[', keyIdx);
                    content        = content.Insert(bracketIdx + 1, "\n    " + newBlock + ",");
                }
                else
                {
                    // Append a new scopedRegistries section before the root closing brace
                    int lastBrace = content.LastIndexOf('}');
                    content       = content.Insert(lastBrace,
                        $",\n  \"scopedRegistries\": [\n    {newBlock}\n  ]\n");
                }

                File.WriteAllText(manifestPath, content);
                AssetDatabase.Refresh();

                Debug.Log($"[PackageManifestInstaller] Scoped registry '{entry.url}' configurado para '{entry.id}'.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageManifestInstaller] Error al modificar manifest.json: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Export current packages
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the currently installed packages and writes a snapshot *.dependencies.json
        /// next to the active tab's manifest. Uses StringBuilder to avoid any JSON library dependency.
        /// </summary>
        private void ExportCurrentPackages(TemplateTab tab)
        {
            ListRequest exportRequest = Client.List(offlineMode: false, includeIndirectDependencies: false);

            // Capture delegate reference so it can be removed from inside the callback
            EditorApplication.CallbackFunction pollDelegate = null;
            pollDelegate = () =>
            {
                if (!exportRequest.IsCompleted) return;

                EditorApplication.update -= pollDelegate;

                if (exportRequest.Status != StatusCode.Success)
                {
                    Debug.LogError($"[PackageManifestInstaller] Export fallido: {exportRequest.Error?.message}");
                    return;
                }

                string upmManifestPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "../Packages/manifest.json"));

                HashSet<string> openUpmScopes = BuildOpenUpmScopeSet(upmManifestPath);

                string json       = BuildExportJson(tab.Manifest, exportRequest.Result, openUpmScopes);
                string outputDir  = Path.GetDirectoryName(tab.ManifestPath) ?? Application.dataPath;
                string outputName = $"exported-{DateTime.Now:yyyy-MM-dd-HHmm}.dependencies.json";
                string outputPath = Path.Combine(outputDir, outputName);

                File.WriteAllText(outputPath, json);
                AssetDatabase.Refresh();

                Debug.Log($"[PackageManifestInstaller] Exportado a: {outputPath}");

                DiscoverAndLoadManifests();
                StartListRequest();
                Repaint();
            };

            EditorApplication.update += pollDelegate;
        }

        /// <summary>
        /// Builds the export JSON using a StringBuilder.
        /// Detects git packages via PackageSource and openupm via scoped registry inspection.
        /// </summary>
        private static string BuildExportJson(
            TemplateManifest  existingManifest,
            PackageCollection packages,
            HashSet<string>   openUpmScopes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": \"{SupportedSchemaVersion}\",");
            sb.AppendLine($"  \"templateName\": \"{J(existingManifest?.templateName ?? "My Template")}\",");
            sb.AppendLine($"  \"unityVersion\": \"{J(Application.unityVersion)}\",");
            sb.AppendLine($"  \"renderPipeline\": \"{J(existingManifest?.renderPipeline ?? "")}\",");
            sb.AppendLine($"  \"menuGroup\": \"{J(existingManifest?.menuGroup ?? "")}\",");
            sb.AppendLine("  \"packages\": [");

            List<UnityEditor.PackageManager.PackageInfo> pkgList = packages
                .Where(p => !p.name.StartsWith("com.unity.modules.", StringComparison.Ordinal))
                .OrderBy(p => p.name)
                .ToList();

            for (int i = 0; i < pkgList.Count; i++)
            {
                UnityEditor.PackageManager.PackageInfo pkg = pkgList[i];
                bool        isGit  = pkg.source == PackageSource.Git;
                bool        isUpm  = openUpmScopes.Contains(pkg.name);
                string      source = isGit ? "git" : (isUpm ? "openupm" : "registry");
                bool        isLast = i == pkgList.Count - 1;

                sb.AppendLine("    {");
                sb.AppendLine($"      \"id\": \"{J(pkg.name)}\",");
                sb.AppendLine($"      \"version\": \"{J(pkg.version)}\",");
                sb.AppendLine($"      \"source\": \"{source}\",");

                if (isGit && !string.IsNullOrEmpty(pkg.packageId))
                    sb.AppendLine($"      \"url\": \"{J(pkg.packageId)}\",");
                else if (isUpm)
                    sb.AppendLine($"      \"url\": \"https://package.openupm.com\",");

                sb.AppendLine($"      \"displayName\": \"{J(pkg.displayName)}\",");
                sb.AppendLine($"      \"optional\": false");
                sb.Append("    }");
                sb.AppendLine(isLast ? "" : ",");
            }

            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Returns all package IDs registered under an openupm scoped registry in manifest.json.</summary>
        private static HashSet<string> BuildOpenUpmScopeSet(string manifestPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(manifestPath)) return result;

            try
            {
                string content = File.ReadAllText(manifestPath);
                if (!content.Contains("openupm")) return result;

                // Find each registry block whose URL contains "openupm" and extract its scopes
                var urlMatches = Regex.Matches(content,
                    @"""url""\s*:\s*""([^""]*openupm[^""]*)""");

                foreach (Match urlMatch in urlMatches)
                {
                    // Walk backward to find the opening { of this registry object
                    int blockStart = content.LastIndexOf('{', urlMatch.Index);
                    if (blockStart < 0) continue;

                    // Walk forward counting braces to find the matching }
                    int depth = 0, blockEnd = blockStart;
                    for (int i = blockStart; i < content.Length; i++)
                    {
                        if      (content[i] == '{') depth++;
                        else if (content[i] == '}') { depth--; if (depth == 0) { blockEnd = i; break; } }
                    }

                    string block = content.Substring(blockStart, blockEnd - blockStart + 1);

                    // Extract quoted package IDs (com.xxx.yyy pattern) from the scopes list
                    foreach (Match scope in Regex.Matches(block, @"""(com\.[a-z][a-zA-Z0-9._-]+)"""))
                        result.Add(scope.Groups[1].Value);
                }
            }
            catch { /* silently skip malformed manifest */ }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>Escapes a string for safe embedding as a JSON string value.</summary>
        private static string J(string s) =>
            (s ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n",  "\\n")
            .Replace("\r",  "");

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
            EditorGUI.DrawRect(r, new Color(0.30f, 0.30f, 0.30f, 0.50f));
            EditorGUILayout.Space(4);
        }
    }
}
