using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace INVELON.Editor
{
    /// <summary>Unity-agnostic description of an installed package, used by the exporter.</summary>
    public struct ExportPackage
    {
        public string Id;          // UPM package name, e.g. "com.unity.timeline"
        public string Version;
        public string DisplayName;
        public string GitUrl;      // non-null only for git packages (URL only, no "name@" prefix)
    }

    /// <summary>
    /// Builds a *.dependencies.json snapshot (schema v2.x) from the currently installed
    /// packages. Uses a StringBuilder so this assembly needs no JSON library.
    /// </summary>
    public static class ExportJsonBuilder
    {
        public static string Build(
            TemplateManifest existingManifest,
            string unityVersion,
            IEnumerable<ExportPackage> packages,
            ISet<string> openUpmScopes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": \"{SchemaVersionPolicy.CurrentVersion}\",");
            sb.AppendLine($"  \"templateName\": \"{J(existingManifest?.templateName ?? "My Template")}\",");
            sb.AppendLine($"  \"unityVersion\": \"{J(unityVersion)}\",");
            sb.AppendLine($"  \"renderPipeline\": \"{J(existingManifest?.renderPipeline ?? "")}\",");
            sb.AppendLine($"  \"menuGroup\": \"{J(existingManifest?.menuGroup ?? "")}\",");
            sb.AppendLine("  \"packages\": [");

            // Built-in Unity modules are implicit and never belong in a manifest.
            List<ExportPackage> list = packages
                .Where(p => !string.IsNullOrEmpty(p.Id) &&
                            !p.Id.StartsWith("com.unity.modules.", StringComparison.Ordinal))
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < list.Count; i++)
            {
                ExportPackage pkg   = list[i];
                bool          isGit = !string.IsNullOrEmpty(pkg.GitUrl);
                bool          isUpm = !isGit && openUpmScopes != null && openUpmScopes.Contains(pkg.Id);
                string        source = isGit ? PackageSourceIds.Git
                                             : (isUpm ? PackageSourceIds.OpenUpm : PackageSourceIds.Registry);
                bool          isLast = i == list.Count - 1;

                sb.AppendLine("    {");
                sb.AppendLine($"      \"id\": \"{J(pkg.Id)}\",");
                sb.AppendLine($"      \"version\": \"{J(pkg.Version)}\",");
                sb.AppendLine($"      \"source\": \"{source}\",");

                if (isGit)
                    sb.AppendLine($"      \"url\": \"{J(pkg.GitUrl)}\",");
                else if (isUpm)
                    sb.AppendLine("      \"url\": \"https://package.openupm.com\",");

                sb.AppendLine($"      \"displayName\": \"{J(pkg.DisplayName)}\",");
                sb.AppendLine("      \"optional\": false");
                sb.Append("    }");
                sb.AppendLine(isLast ? "" : ",");
            }

            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Escapes a string for safe embedding as a JSON string value.</summary>
        public static string J(string s) =>
            (s ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "");
    }
}
