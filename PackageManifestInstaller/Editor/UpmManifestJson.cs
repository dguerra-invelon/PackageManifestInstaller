using System;
using System.Collections.Generic;

namespace INVELON.Editor
{
    /// <summary>
    /// String-based editing of UPM JSON files (Packages/manifest.json, packages-lock.json).
    ///
    /// Intentionally has NO Unity or external JSON dependencies so this assembly compiles
    /// even when the rest of the project is broken (the whole point of the installer).
    /// All methods are side-effect free: they take JSON text in and return modified text.
    /// The parsing is structure-aware (string-escape and depth aware), so it never matches
    /// keys nested inside other objects — unlike naive Contains()/IndexOf() checks.
    /// </summary>
    public static class UpmManifestJson
    {
        // ──────────────────────────────────────────────────────────────────────
        //  Queries
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the raw string value of dependencies[packageId] in manifest.json,
        /// or null when the package is not a direct dependency. Nested occurrences of
        /// the same id (e.g. inside packages-lock sub-dependencies) are ignored.
        /// </summary>
        public static string GetDependencyValue(string json, string packageId)
        {
            if (!TryGetRootMember(json, "dependencies", out _, out int vStart, out int vEnd))
                return null;
            if (json[vStart] != '{')
                return null;
            if (!TryFindDirectMember(json, vStart, vEnd, packageId, out _, out int mStart, out int mEnd))
                return null;
            return ExtractString(json, mStart, mEnd);
        }

        /// <summary>True when the package is registered in manifest.json as a local tarball (file: ref).</summary>
        public static bool IsTarballRegistered(string json, string packageId)
        {
            string value = GetDependencyValue(json, packageId);
            return value != null && value.StartsWith("file:", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns every scope listed under scoped registries whose URL contains
        /// <paramref name="urlSubstring"/> (case-insensitive). Used to detect openupm packages.
        /// </summary>
        public static HashSet<string> GetScopesForRegistriesMatching(string json, string urlSubstring)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!TryGetRootMember(json, "scopedRegistries", out _, out int aStart, out int aEnd) ||
                json[aStart] != '[')
                return result;

            foreach ((int eStart, int eEnd) in EnumerateArrayElements(json, aStart, aEnd))
            {
                if (json[eStart] != '{') continue;

                if (!TryFindDirectMember(json, eStart, eEnd, "url", out _, out int uS, out int uE))
                    continue;

                string url = ExtractString(json, uS, uE);
                if (url == null || url.IndexOf(urlSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!TryFindDirectMember(json, eStart, eEnd, "scopes", out _, out int sS, out int sE) ||
                    json[sS] != '[')
                    continue;

                foreach ((int scS, int scE) in EnumerateArrayElements(json, sS, sE))
                {
                    string scope = ExtractString(json, scS, scE);
                    if (!string.IsNullOrEmpty(scope)) result.Add(scope);
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Edits
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures a scoped registry with <paramref name="url"/> exists and contains
        /// <paramref name="scope"/>. If a registry with the same URL already exists, the
        /// scope is appended to it (no duplicate registry blocks are ever created).
        /// </summary>
        public static string AddScopedRegistry(string json, string registryName, string url, string scope, out bool changed)
        {
            changed = false;

            if (TryGetRootMember(json, "scopedRegistries", out _, out int aStart, out int aEnd) &&
                json[aStart] == '[')
            {
                foreach ((int eStart, int eEnd) in EnumerateArrayElements(json, aStart, aEnd))
                {
                    if (json[eStart] != '{') continue;

                    if (!TryFindDirectMember(json, eStart, eEnd, "url", out _, out int uS, out int uE))
                        continue;
                    if (!string.Equals(ExtractString(json, uS, uE), url, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Registry already exists — ensure the scope is present.
                    if (TryFindDirectMember(json, eStart, eEnd, "scopes", out _, out int sS, out int sE) &&
                        json[sS] == '[')
                    {
                        foreach ((int scS, int scE) in EnumerateArrayElements(json, sS, sE))
                            if (string.Equals(ExtractString(json, scS, scE), scope, StringComparison.OrdinalIgnoreCase))
                                return json; // scope already registered — nothing to do

                        bool emptyArr = IsBlank(json, sS + 1, sE);
                        string insert = emptyArr
                            ? $"\n        \"{scope}\"\n      "
                            : $"\n        \"{scope}\",";
                        changed = true;
                        return json.Insert(sS + 1, insert);
                    }

                    // Registry object without a scopes array — add one.
                    {
                        bool emptyObj = IsBlank(json, eStart + 1, eEnd);
                        string insert = $"\n      \"scopes\": [\n        \"{scope}\"\n      ]" +
                                        (emptyObj ? "\n    " : ",");
                        changed = true;
                        return json.Insert(eStart + 1, insert);
                    }
                }

                // No registry with this URL — insert a new one at the start of the array.
                string block    = BuildRegistryBlock(registryName, url, scope);
                bool arrayEmpty = IsBlank(json, aStart + 1, aEnd);
                string ins      = arrayEmpty ? $"\n    {block}\n  " : $"\n    {block},";
                changed = true;
                return json.Insert(aStart + 1, ins);
            }

            // No scopedRegistries section — add one at the start of the root object.
            {
                int rootOpen = json.IndexOf('{');
                if (rootOpen < 0) return json;
                int rootClose = FindMatchingBracket(json, rootOpen);
                if (rootClose < 0) return json;

                string block   = BuildRegistryBlock(registryName, url, scope);
                bool rootEmpty = IsBlank(json, rootOpen + 1, rootClose);
                string member  = $"\n  \"scopedRegistries\": [\n    {block}\n  ]";
                changed = true;
                return json.Insert(rootOpen + 1, rootEmpty ? member + "\n" : member + ",");
            }
        }

        /// <summary>
        /// Sets dependencies[packageId] = value in manifest.json, replacing an existing
        /// value or inserting a new entry. Returns the input unchanged when the value
        /// is already correct.
        /// </summary>
        public static string SetDependency(string json, string packageId, string value, out bool changed)
        {
            changed = false;

            if (TryGetRootMember(json, "dependencies", out _, out int dS, out int dE) &&
                json[dS] == '{')
            {
                int dClose = FindMatchingBracket(json, dS);

                if (TryFindDirectMember(json, dS, dClose, packageId, out _, out int vStart, out int vEnd))
                {
                    string current = ExtractString(json, vStart, vEnd);
                    if (string.Equals(current, value, StringComparison.Ordinal))
                        return json;

                    changed = true;
                    return json.Remove(vStart, vEnd - vStart + 1)
                               .Insert(vStart, $"\"{value}\"");
                }

                bool empty = IsBlank(json, dS + 1, dClose);
                string ins = $"\n    \"{packageId}\": \"{value}\"" + (empty ? "\n  " : ",");
                changed = true;
                return json.Insert(dS + 1, ins);
            }

            // No dependencies object at all — create one at the start of the root object.
            {
                int rootOpen = json.IndexOf('{');
                if (rootOpen < 0) return json;
                int rootClose = FindMatchingBracket(json, rootOpen);
                if (rootClose < 0) return json;

                bool rootEmpty = IsBlank(json, rootOpen + 1, rootClose);
                string member  = $"\n  \"dependencies\": {{\n    \"{packageId}\": \"{value}\"\n  }}";
                changed = true;
                return json.Insert(rootOpen + 1, rootEmpty ? member + "\n" : member + ",");
            }
        }

        /// <summary>
        /// Removes dependencies[packageId] (value may be a string or an object — this also
        /// works for packages-lock.json entries). Only DIRECT children of the top-level
        /// "dependencies" object are removed; nested occurrences of the same id inside
        /// other packages' sub-dependency maps are left untouched.
        /// </summary>
        public static string RemoveDependencyEntry(string json, string packageId, out bool changed)
        {
            changed = false;

            if (!TryGetRootMember(json, "dependencies", out _, out int dS, out _) || json[dS] != '{')
                return json;

            int dClose = FindMatchingBracket(json, dS);
            if (dClose < 0) return json;

            if (!TryFindDirectMember(json, dS, dClose, packageId, out int keyStart, out _, out int valueEnd))
                return json;

            int removeStart = keyStart;
            int removeEnd   = valueEnd + 1; // exclusive

            // Prefer consuming the trailing comma; if the entry is the last member,
            // consume the preceding comma instead (keeps the JSON valid either way).
            int t = removeEnd;
            while (t < json.Length && char.IsWhiteSpace(json[t])) t++;
            if (t < json.Length && json[t] == ',')
            {
                removeEnd = t + 1;
            }
            else
            {
                int p = removeStart - 1;
                while (p > 0 && char.IsWhiteSpace(json[p])) p--;
                if (p > 0 && json[p] == ',') removeStart = p;
            }

            // Tidy up leading indentation on the removed line.
            while (removeStart > 0 && (json[removeStart - 1] == ' ' || json[removeStart - 1] == '\t'))
                removeStart--;

            changed = true;
            return json.Remove(removeStart, removeEnd - removeStart);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Scanner internals (string-escape and depth aware)
        // ──────────────────────────────────────────────────────────────────────

        private static string BuildRegistryBlock(string registryName, string url, string scope) =>
            "{\n" +
            $"      \"name\": \"{registryName}\",\n" +
            $"      \"url\": \"{url}\",\n" +
            "      \"scopes\": [\n" +
            $"        \"{scope}\"\n" +
            "      ]\n" +
            "    }";

        /// <summary>Finds a direct member of the ROOT object.</summary>
        private static bool TryGetRootMember(string json, string key, out int keyStart, out int valueStart, out int valueEnd)
        {
            keyStart = valueStart = valueEnd = -1;
            if (string.IsNullOrEmpty(json)) return false;

            int rootOpen = json.IndexOf('{');
            if (rootOpen < 0) return false;
            int rootClose = FindMatchingBracket(json, rootOpen);
            if (rootClose < 0) return false;

            return TryFindDirectMember(json, rootOpen, rootClose, key, out keyStart, out valueStart, out valueEnd);
        }

        /// <summary>
        /// Finds a DIRECT member (depth 1) of the object delimited by objOpen/objClose.
        /// Skips over member values so keys nested in child objects are never matched.
        /// valueEnd is the inclusive index of the value's last character.
        /// </summary>
        private static bool TryFindDirectMember(
            string json, int objOpen, int objClose, string key,
            out int keyStart, out int valueStart, out int valueEnd)
        {
            keyStart = valueStart = valueEnd = -1;

            int i = objOpen + 1;
            while (i < objClose)
            {
                char c = json[i];
                if (c != '"') { i++; continue; }

                int strEnd = FindStringEnd(json, i);
                if (strEnd < 0 || strEnd >= objClose) return false;

                string name = json.Substring(i + 1, strEnd - i - 1);

                int k = strEnd + 1;
                while (k < objClose && char.IsWhiteSpace(json[k])) k++;
                if (k >= objClose || json[k] != ':') { i = strEnd + 1; continue; }

                int v = k + 1;
                while (v < objClose && char.IsWhiteSpace(json[v])) v++;
                if (v >= objClose) return false;

                int vEnd = FindValueEnd(json, v);
                if (vEnd < 0) return false;

                if (name == key)
                {
                    keyStart   = i;
                    valueStart = v;
                    valueEnd   = vEnd;
                    return true;
                }

                i = vEnd + 1; // jump past the whole value
            }

            return false;
        }

        /// <summary>Returns the inclusive end index of the JSON value starting at valueStart.</summary>
        private static int FindValueEnd(string json, int valueStart)
        {
            char c = json[valueStart];
            if (c == '{' || c == '[') return FindMatchingBracket(json, valueStart);
            if (c == '"')             return FindStringEnd(json, valueStart);

            // number / true / false / null — runs until , } or ]
            int i = valueStart;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']') i++;
            i--;
            while (i > valueStart && char.IsWhiteSpace(json[i])) i--;
            return i;
        }

        /// <summary>Returns the index of the closing quote of the string starting at quoteIdx.</summary>
        private static int FindStringEnd(string json, int quoteIdx)
        {
            bool esc = false;
            for (int i = quoteIdx + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (esc)        { esc = false; continue; }
                if (c == '\\')  { esc = true;  continue; }
                if (c == '"')   return i;
            }
            return -1;
        }

        /// <summary>Returns the index of the bracket matching the one at openIdx (string-aware).</summary>
        private static int FindMatchingBracket(string json, int openIdx)
        {
            char open  = json[openIdx];
            char close = open == '{' ? '}' : ']';

            int  depth = 0;
            bool inStr = false, esc = false;

            for (int i = openIdx; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc)        esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"')  inStr = false;
                    continue;
                }
                if (c == '"')  { inStr = true; continue; }
                if (c == open)  depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>Enumerates the (start, inclusive end) index pairs of each element of the array.</summary>
        private static List<(int start, int end)> EnumerateArrayElements(string json, int arrOpen, int arrClose)
        {
            var result = new List<(int, int)>();

            int i = arrOpen + 1;
            while (i < arrClose)
            {
                while (i < arrClose && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= arrClose) break;

                int end = FindValueEnd(json, i);
                if (end < 0 || end > arrClose) break;

                result.Add((i, end));
                i = end + 1;
            }

            return result;
        }

        /// <summary>Extracts a string value (without quotes) or the trimmed raw token.</summary>
        private static string ExtractString(string json, int valueStart, int valueEnd)
        {
            if (json[valueStart] == '"')
                return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
            return json.Substring(valueStart, valueEnd - valueStart + 1).Trim();
        }

        private static bool IsBlank(string json, int from, int toExclusive)
        {
            for (int i = from; i < toExclusive; i++)
                if (!char.IsWhiteSpace(json[i])) return false;
            return true;
        }
    }
}
