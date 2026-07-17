using NUnit.Framework;

namespace INVELON.Editor.Tests
{
    public class UpmManifestJsonTests
    {
        private const string BasicManifest =
@"{
  ""dependencies"": {
    ""com.unity.timeline"": ""1.8.7"",
    ""com.unity.textmeshpro"": ""3.0.6""
  }
}";

        private const string ManifestWithOpenUpm =
@"{
  ""scopedRegistries"": [
    {
      ""name"": ""package.openupm.com"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [
        ""com.eflatun.scenereference""
      ]
    }
  ],
  ""dependencies"": {
    ""com.unity.timeline"": ""1.8.7""
  }
}";

        // ── GetDependencyValue ────────────────────────────────────────────────

        [Test]
        public void GetDependencyValue_ReturnsValue()
        {
            Assert.AreEqual("1.8.7", UpmManifestJson.GetDependencyValue(BasicManifest, "com.unity.timeline"));
        }

        [Test]
        public void GetDependencyValue_ReturnsNullForMissingPackage()
        {
            Assert.IsNull(UpmManifestJson.GetDependencyValue(BasicManifest, "com.unity.burst"));
        }

        [Test]
        public void GetDependencyValue_IgnoresNestedOccurrences()
        {
            // packages-lock.json style: "com.unity.burst" only exists as a NESTED
            // sub-dependency of another package, not as a direct dependency.
            const string lockJson =
@"{
  ""dependencies"": {
    ""com.unity.collections"": {
      ""version"": ""2.1.4"",
      ""dependencies"": {
        ""com.unity.burst"": ""1.8.4""
      }
    }
  }
}";
            Assert.IsNull(UpmManifestJson.GetDependencyValue(lockJson, "com.unity.burst"));
        }

        // ── IsTarballRegistered ───────────────────────────────────────────────

        [Test]
        public void IsTarballRegistered_FalseWhenRegistryVersion()
        {
            Assert.IsFalse(UpmManifestJson.IsTarballRegistered(BasicManifest, "com.unity.timeline"));
        }

        [Test]
        public void IsTarballRegistered_TrueWhenFileRef()
        {
            string json = UpmManifestJson.SetDependency(BasicManifest, "com.x.y", "file:./com.x.y-1.0.0.tgz", out _);
            Assert.IsTrue(UpmManifestJson.IsTarballRegistered(json, "com.x.y"));
        }

        [Test]
        public void IsTarballRegistered_NotFooledByOtherFileRefs()
        {
            // Old Contains()-based check false-positived when ANY file: ref existed.
            string json = UpmManifestJson.SetDependency(BasicManifest, "com.other.pkg", "file:./other.tgz", out _);
            Assert.IsFalse(UpmManifestJson.IsTarballRegistered(json, "com.unity.timeline"));
        }

        // ── SetDependency ─────────────────────────────────────────────────────

        [Test]
        public void SetDependency_InsertsNewEntry()
        {
            string result = UpmManifestJson.SetDependency(BasicManifest, "com.x.y", "file:./x.tgz", out bool changed);
            Assert.IsTrue(changed);
            Assert.AreEqual("file:./x.tgz", UpmManifestJson.GetDependencyValue(result, "com.x.y"));
            // Existing entries untouched
            Assert.AreEqual("1.8.7", UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
        }

        [Test]
        public void SetDependency_ReplacesExistingValue()
        {
            string result = UpmManifestJson.SetDependency(BasicManifest, "com.unity.timeline", "file:./t.tgz", out bool changed);
            Assert.IsTrue(changed);
            Assert.AreEqual("file:./t.tgz", UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
        }

        [Test]
        public void SetDependency_NoChangeWhenValueAlreadyCorrect()
        {
            string result = UpmManifestJson.SetDependency(BasicManifest, "com.unity.timeline", "1.8.7", out bool changed);
            Assert.IsFalse(changed);
            Assert.AreEqual(BasicManifest, result);
        }

        [Test]
        public void SetDependency_HandlesEmptyDependenciesObject()
        {
            const string json = "{\n  \"dependencies\": {}\n}";
            string result = UpmManifestJson.SetDependency(json, "com.x.y", "1.0.0", out bool changed);
            Assert.IsTrue(changed);
            Assert.AreEqual("1.0.0", UpmManifestJson.GetDependencyValue(result, "com.x.y"));
            StringAssert.DoesNotContain(",}", Compact(result));
            StringAssert.DoesNotContain(",,", Compact(result));
        }

        [Test]
        public void SetDependency_CreatesDependenciesWhenMissing()
        {
            const string json = "{\n  \"registry\": \"https://example.com\"\n}";
            string result = UpmManifestJson.SetDependency(json, "com.x.y", "1.0.0", out bool changed);
            Assert.IsTrue(changed);
            Assert.AreEqual("1.0.0", UpmManifestJson.GetDependencyValue(result, "com.x.y"));
        }

        // ── RemoveDependencyEntry ─────────────────────────────────────────────

        [Test]
        public void RemoveDependencyEntry_RemovesStringEntry()
        {
            string result = UpmManifestJson.RemoveDependencyEntry(BasicManifest, "com.unity.timeline", out bool changed);
            Assert.IsTrue(changed);
            Assert.IsNull(UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
            Assert.AreEqual("3.0.6", UpmManifestJson.GetDependencyValue(result, "com.unity.textmeshpro"));
            AssertNoDanglingCommas(result);
        }

        [Test]
        public void RemoveDependencyEntry_RemovesLastEntryWithoutDanglingComma()
        {
            string result = UpmManifestJson.RemoveDependencyEntry(BasicManifest, "com.unity.textmeshpro", out bool changed);
            Assert.IsTrue(changed);
            Assert.AreEqual("1.8.7", UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
            AssertNoDanglingCommas(result);
        }

        [Test]
        public void RemoveDependencyEntry_RemovesObjectEntry_LockFileStyle()
        {
            const string lockJson =
@"{
  ""dependencies"": {
    ""com.unity.timeline"": {
      ""version"": ""1.8.7"",
      ""depth"": 0,
      ""dependencies"": {}
    },
    ""com.unity.burst"": {
      ""version"": ""1.8.4"",
      ""depth"": 1,
      ""dependencies"": {}
    }
  }
}";
            string result = UpmManifestJson.RemoveDependencyEntry(lockJson, "com.unity.timeline", out bool changed);
            Assert.IsTrue(changed);
            Assert.IsNull(UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
            Assert.IsNotNull(UpmManifestJson.GetDependencyValue(result, "com.unity.burst"));
            AssertNoDanglingCommas(result);
        }

        [Test]
        public void RemoveDependencyEntry_DoesNotRemoveNestedSameKey()
        {
            // The old global-IndexOf implementation could match the nested key. This
            // must only remove the DIRECT child of the top-level dependencies object.
            const string lockJson =
@"{
  ""dependencies"": {
    ""com.unity.collections"": {
      ""version"": ""2.1.4"",
      ""dependencies"": {
        ""com.unity.burst"": ""1.8.4""
      }
    }
  }
}";
            string result = UpmManifestJson.RemoveDependencyEntry(lockJson, "com.unity.burst", out bool changed);
            Assert.IsFalse(changed);
            Assert.AreEqual(lockJson, result);
        }

        // ── AddScopedRegistry ─────────────────────────────────────────────────

        [Test]
        public void AddScopedRegistry_CreatesSectionWhenMissing()
        {
            string result = UpmManifestJson.AddScopedRegistry(
                BasicManifest, "package.openupm.com", "https://package.openupm.com", "com.x.y", out bool changed);

            Assert.IsTrue(changed);
            var scopes = UpmManifestJson.GetScopesForRegistriesMatching(result, "openupm");
            Assert.IsTrue(scopes.Contains("com.x.y"));
            // dependencies untouched
            Assert.AreEqual("1.8.7", UpmManifestJson.GetDependencyValue(result, "com.unity.timeline"));
        }

        [Test]
        public void AddScopedRegistry_AppendsScopeToExistingRegistry_NoDuplicateBlocks()
        {
            string result = UpmManifestJson.AddScopedRegistry(
                ManifestWithOpenUpm, "package.openupm.com", "https://package.openupm.com", "com.new.scope", out bool changed);

            Assert.IsTrue(changed);

            var scopes = UpmManifestJson.GetScopesForRegistriesMatching(result, "openupm");
            Assert.IsTrue(scopes.Contains("com.new.scope"));
            Assert.IsTrue(scopes.Contains("com.eflatun.scenereference"));

            // The old implementation inserted a whole second registry block with the
            // same URL. There must be exactly ONE occurrence of the registry URL.
            int urlCount = CountOccurrences(result, "\"https://package.openupm.com\"");
            Assert.AreEqual(1, urlCount);
        }

        [Test]
        public void AddScopedRegistry_NoChangeWhenScopeAlreadyPresent()
        {
            string result = UpmManifestJson.AddScopedRegistry(
                ManifestWithOpenUpm, "package.openupm.com", "https://package.openupm.com",
                "com.eflatun.scenereference", out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(ManifestWithOpenUpm, result);
        }

        [Test]
        public void AddScopedRegistry_HandlesEmptyScopedRegistriesArray()
        {
            const string json = "{\n  \"scopedRegistries\": [],\n  \"dependencies\": {}\n}";
            string result = UpmManifestJson.AddScopedRegistry(
                json, "openupm", "https://package.openupm.com", "com.x.y", out bool changed);

            Assert.IsTrue(changed);
            var scopes = UpmManifestJson.GetScopesForRegistriesMatching(result, "openupm");
            Assert.IsTrue(scopes.Contains("com.x.y"));
            AssertNoDanglingCommas(result);
        }

        [Test]
        public void AddScopedRegistry_DifferentUrlCreatesSeparateRegistry()
        {
            string result = UpmManifestJson.AddScopedRegistry(
                ManifestWithOpenUpm, "other", "https://registry.example.com", "com.x.y", out bool changed);

            Assert.IsTrue(changed);
            var openUpm = UpmManifestJson.GetScopesForRegistriesMatching(result, "openupm");
            var example = UpmManifestJson.GetScopesForRegistriesMatching(result, "example.com");
            Assert.IsFalse(openUpm.Contains("com.x.y"));
            Assert.IsTrue(example.Contains("com.x.y"));
        }

        // ── GetScopesForRegistriesMatching ────────────────────────────────────

        [Test]
        public void GetScopes_ReturnsEmptyWhenNoRegistries()
        {
            Assert.IsEmpty(UpmManifestJson.GetScopesForRegistriesMatching(BasicManifest, "openupm"));
        }

        [Test]
        public void GetScopes_ReturnsOnlyMatchingRegistryScopes()
        {
            const string json =
@"{
  ""scopedRegistries"": [
    {
      ""name"": ""openupm"",
      ""url"": ""https://package.openupm.com"",
      ""scopes"": [ ""com.a.b"", ""com.c.d"" ]
    },
    {
      ""name"": ""corp"",
      ""url"": ""https://npm.corp.com"",
      ""scopes"": [ ""com.corp.tools"" ]
    }
  ],
  ""dependencies"": {}
}";
            var scopes = UpmManifestJson.GetScopesForRegistriesMatching(json, "openupm");
            Assert.IsTrue(scopes.Contains("com.a.b"));
            Assert.IsTrue(scopes.Contains("com.c.d"));
            Assert.IsFalse(scopes.Contains("com.corp.tools"));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Compact(string s) => s.Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");

        private static void AssertNoDanglingCommas(string json)
        {
            string compact = Compact(json);
            StringAssert.DoesNotContain(",}", compact);
            StringAssert.DoesNotContain(",]", compact);
            StringAssert.DoesNotContain("{,", compact);
            StringAssert.DoesNotContain("[,", compact);
            StringAssert.DoesNotContain(",,", compact);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
