using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Dependencies outside the source root. Directory D holds two prefabs (Prefabs/OutsideA, Prefabs/OutsideB) whose
    /// renderer uses Assets/TestShared/Materials/Outside.mat, which in turn uses Assets/TestShared/Textures/outside.png —
    /// neither is under the source root, so neither gets a catalog name. The engine follows the references from the
    /// catalog and puts them into the directory anyway.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("OutsideDependency")]
    public class OutsideDependencyTests
    {
        int m_DirD = -1;

        [OneTimeSetUp]
        public void RegisterD()
        {
            Assert.That(Directory.Exists(TestContent.Dir("D")), Is.True,
                $"content directory D is missing at '{TestContent.Dir("D")}' — run the EditMode test TestContentBuildTests (or Window/ContentLink/Tests/Build Test Content) first");
            TestUtil.SetSimulation(false);   // only the built directory may answer
            m_DirD = ContentService.RegisterDirectory(TestContent.Dir("D"));
        }

        [OneTimeTearDown]
        public void UnregisterD()
        {
            if (m_DirD < 0) return;
            bool ok = ContentService.UnregisterDirectory(m_DirD);
            if (!ok) ContentService.UnregisterDirectory(m_DirD, force: true);
            m_DirD = -1;
            Assert.That(ok, Is.True, "directory D still had live assets at fixture end (leak)");
        }

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        [Test, Timeout(10000)]
        public void D1_OnlyAssetsUnderTheSourceRoot_GetNames()
        {
            Assert.That(ContentService.Contains("Prefabs/OutsideA"), Is.True);
            Assert.That(ContentService.Contains("Prefabs/OutsideB"), Is.True);
            foreach (var name in new[] { "Materials/Outside", "Textures/outside", "Outside", "outside", "TestShared/Materials/Outside" })
                Assert.That(ContentService.Contains(name), Is.False, $"'{name}' is outside the source root and must have no name");
        }

        [Test, Timeout(10000)]
        public async Task D2_PrefabBringsItsMaterialAndTexture_FromTheBuiltDirectory()
        {
            var scope = ContentScope.Root.CreateChild("D2");
            try
            {
                var prefab = await scope.LoadAsync<GameObject>("Prefabs/OutsideA");
                Assert.That(prefab, Is.Not.Null, "GameObject Prefabs/OutsideA");

                var material = prefab.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(material, Is.Not.Null, "the material outside the source root did not come with the prefab");
                Assert.That(material.name, Is.EqualTo("Outside"));
                Assert.That(material.shader, Is.Not.Null, "the material's shader");

                var texture = material.GetTexture("_BaseMap") as Texture2D;
                Assert.That(texture, Is.Not.Null, "the texture outside the source root did not come with the material");
                Assert.That(texture.name, Is.EqualTo("outside"));
                Assert.That(texture.width, Is.EqualTo(8));
                Assert.That(texture.height, Is.EqualTo(8));
                Assert.That(prefab.GetComponent<MeshFilter>().sharedMesh, Is.Not.Null, "the built-in cube mesh");
#if UNITY_EDITOR
                Assert.That(UnityEditor.AssetDatabase.GetAssetPath(material), Is.Empty, "the material must come from the built directory, not from the project");
                Assert.That(UnityEditor.AssetDatabase.GetAssetPath(texture), Is.Empty, "the texture must come from the built directory, not from the project");
#endif
            }
            finally { scope.Dispose(); }
        }

        [Test, Timeout(10000)]
        public async Task D3_TwoPrefabs_ShareOneCopyOfTheDependency()
        {
            var scope = ContentScope.Root.CreateChild("D3");
            try
            {
                var a = await scope.LoadAsync<GameObject>("Prefabs/OutsideA");
                var b = await scope.LoadAsync<GameObject>("Prefabs/OutsideB");
                var ma = a.GetComponent<MeshRenderer>().sharedMaterial;
                var mb = b.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(ma, Is.Not.Null);
                Assert.That(mb, Is.SameAs(ma), "both prefabs must see the same material object — one copy per directory");
                Assert.That(scope.Count, Is.EqualTo(2), "the scope holds the two named prefabs only");
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(2), "dependencies carry no lease of their own");
            }
            finally { scope.Dispose(); }
        }

        [Test, Timeout(10000)]
        public async Task D4_DependencyOutlivesOneOwner_AndIsUnloadedWithTheLast()
        {
            var first = ContentScope.Root.CreateChild("D4a");
            var second = ContentScope.Root.CreateChild("D4b");
            Material material;
            try
            {
                var a = await first.LoadAsync<GameObject>("Prefabs/OutsideA");
                await second.LoadAsync<GameObject>("Prefabs/OutsideB");
                material = a.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(material, Is.Not.Null);
                var texture = material.GetTexture("_BaseMap");

                first.Dispose();
                ContentService.Collect();
                for (int i = 0; i < 5; ++i) await TestUtil.NextFrame();
                Assert.That(material != null, Is.True, "OutsideB still uses the material; releasing OutsideA must not unload it");
                Assert.That(texture != null, Is.True, "OutsideB still uses the texture");

                second.Dispose();
                ContentService.Collect();
                bool gone = await TestUtil.WaitUntil(() => material == null && texture == null);
                Debug.Log($"[ContentLink.Tests] outside dependency unloaded with its last owner: {gone}");
                if (TestEnv.ReleaseObservable)
                    Assert.That(gone, Is.True, "the material and texture are still alive after every prefab that used them was released");
            }
            finally { first.Dispose(); second.Dispose(); }
        }
    }
}
