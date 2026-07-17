using System.Collections.Generic;
using NUnit.Framework;

namespace INVELON.Editor.Tests
{
    public class VersionUtilTests
    {
        [TestCase("1.8.7", "1.8.7", true)]
        [TestCase("1.8.8", "1.8.7", true)]
        [TestCase("2.0.0", "1.9.9", true)]
        [TestCase("1.8.6", "1.8.7", false)]
        [TestCase("1.5.9-pre.1", "1.5.9", true)]   // pre-release suffix stripped
        [TestCase("1.5.8-pre.1", "1.5.9", false)]
        [TestCase("3.3", "3.3.1", false)]
        public void Satisfies_ComparesSemanticVersions(string installed, string required, bool expected)
        {
            Assert.AreEqual(expected, VersionUtil.Satisfies(installed, required));
        }

        [Test]
        public void TryParse_HandlesSingleComponent()
        {
            Assert.IsTrue(VersionUtil.TryParse("5", out var v));
            Assert.AreEqual(5, v.Major);
        }

        [Test]
        public void TryParse_RejectsGarbage()
        {
            Assert.IsFalse(VersionUtil.TryParse("not-a-version", out _));
            Assert.IsFalse(VersionUtil.TryParse("", out _));
            Assert.IsFalse(VersionUtil.TryParse(null, out _));
        }
    }

    public class SchemaVersionPolicyTests
    {
        [TestCase("2.0", SchemaCompatibility.Compatible)]
        [TestCase("2", SchemaCompatibility.Compatible)]
        [TestCase("2.1", SchemaCompatibility.CompatibleNewerMinor)]
        [TestCase("2.99", SchemaCompatibility.CompatibleNewerMinor)]
        [TestCase("3.0", SchemaCompatibility.Incompatible)]
        [TestCase("1.0", SchemaCompatibility.Incompatible)]
        [TestCase("", SchemaCompatibility.Incompatible)]
        [TestCase(null, SchemaCompatibility.Incompatible)]
        [TestCase("abc", SchemaCompatibility.Incompatible)]
        public void Check_AppliesMajorMinorPolicy(string declared, SchemaCompatibility expected)
        {
            Assert.AreEqual(expected, SchemaVersionPolicy.Check(declared));
        }
    }

    public class ExportJsonBuilderTests
    {
        private static List<ExportPackage> SamplePackages() => new List<ExportPackage>
        {
            new ExportPackage { Id = "com.unity.timeline", Version = "1.8.7", DisplayName = "Timeline" },
            new ExportPackage { Id = "com.unity.modules.ui", Version = "1.0.0", DisplayName = "UI" }, // filtered
            new ExportPackage
            {
                Id = "com.org.gitpkg", Version = "1.0.0", DisplayName = "Git Pkg",
                GitUrl = "https://github.com/Org/Repo.git#1.0.0"
            },
            new ExportPackage { Id = "com.eflatun.scenereference", Version = "5.0.0", DisplayName = "SceneRef" }
        };

        [Test]
        public void Build_FiltersBuiltInModules()
        {
            string json = ExportJsonBuilder.Build(null, "6000.0.32f1", SamplePackages(), null);
            StringAssert.DoesNotContain("com.unity.modules.ui", json);
            StringAssert.Contains("com.unity.timeline", json);
        }

        [Test]
        public void Build_MarksGitPackagesWithUrl()
        {
            string json = ExportJsonBuilder.Build(null, "6000.0.32f1", SamplePackages(), null);
            StringAssert.Contains("\"source\": \"git\"", json);
            StringAssert.Contains("https://github.com/Org/Repo.git#1.0.0", json);
        }

        [Test]
        public void Build_MarksOpenUpmPackagesFromScopes()
        {
            var scopes = new HashSet<string> { "com.eflatun.scenereference" };
            string json = ExportJsonBuilder.Build(null, "6000.0.32f1", SamplePackages(), scopes);
            StringAssert.Contains("\"source\": \"openupm\"", json);
            StringAssert.Contains("https://package.openupm.com", json);
        }

        [Test]
        public void Build_UsesCurrentSchemaVersionAndTemplateInfo()
        {
            var manifest = new TemplateManifest { templateName = "My VR Template", renderPipeline = "URP" };
            string json = ExportJsonBuilder.Build(manifest, "6000.0.32f1", SamplePackages(), null);
            StringAssert.Contains($"\"schemaVersion\": \"{SchemaVersionPolicy.CurrentVersion}\"", json);
            StringAssert.Contains("\"templateName\": \"My VR Template\"", json);
            StringAssert.Contains("\"unityVersion\": \"6000.0.32f1\"", json);
        }

        [Test]
        public void Build_EscapesSpecialCharacters()
        {
            var packages = new List<ExportPackage>
            {
                new ExportPackage { Id = "com.x.y", Version = "1.0.0", DisplayName = "Say \"Hi\"\\Bye" }
            };
            string json = ExportJsonBuilder.Build(null, "6000.0", packages, null);
            StringAssert.Contains("Say \\\"Hi\\\"\\\\Bye", json);
        }

        [Test]
        public void J_EscapesBackslashesQuotesAndNewlines()
        {
            Assert.AreEqual("a\\\\b", ExportJsonBuilder.J("a\\b"));
            Assert.AreEqual("a\\\"b", ExportJsonBuilder.J("a\"b"));
            Assert.AreEqual("a\\nb", ExportJsonBuilder.J("a\nb"));
            Assert.AreEqual("", ExportJsonBuilder.J(null));
        }
    }
}
