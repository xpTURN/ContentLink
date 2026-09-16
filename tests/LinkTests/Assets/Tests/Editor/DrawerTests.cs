using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// The property drawers, as far as a headless EditMode run can see them.
    /// OnGUI needs a GUI context, but registration and the write path do not: EditorGUI.GetPropertyHeight routes
    /// through the drawer, so a two-line height proves the drawer is attached (and that useForChildren works).
    /// </summary>
    [Category("Drawer")]
    public class DrawerTests
    {
        DrawerFixture m_Fixture;
        SerializedObject m_Object;

        [SetUp]
        public void CreateFixture()
        {
            m_Fixture = ScriptableObject.CreateInstance<DrawerFixture>();
            m_Object = new SerializedObject(m_Fixture);
        }

        [TearDown]
        public void DestroyFixture()
        {
            m_Object?.Dispose();
            if (m_Fixture != null) Object.DestroyImmediate(m_Fixture);
            ContentLinkEditorUtility.Invalidate();
        }

        static float TwoLines => EditorGUIUtility.singleLineHeight * 2 + EditorGUIUtility.standardVerticalSpacing;

        SerializedProperty Find(string path)
        {
            var property = m_Object.FindProperty(path);
            Assert.That(property, Is.Not.Null, $"no serialized property '{path}' — the field did not serialize");
            return property;
        }

        void AssertDrawnByLinkDrawer(string path)
            => Assert.That(EditorGUI.GetPropertyHeight(Find(path)), Is.EqualTo(TwoLines).Within(0.01f),
                $"'{path}' is not drawn by the link drawer");

        // ───────────── registration ─────────────

        [Test]
        public void K1_Drawer_CoversDerived_ClosedGeneric_CollectionElements_AndScene()
        {
            AssertDrawnByLinkDrawer("one");                  // ContentLinkSprite — only found through the base type
            AssertDrawnByLinkDrawer("direct");               // ContentLink<Texture2D> — the closed generic itself
            AssertDrawnByLinkDrawer("many.Array.data[0]");   // array element: fieldInfo is the array
            AssertDrawnByLinkDrawer("list.Array.data[0]");   // List<> element
            AssertDrawnByLinkDrawer("scene");                // ContentSceneLink

            Assert.That(EditorGUI.GetPropertyHeight(Find("notALink")), Is.EqualTo(EditorGUIUtility.singleLineHeight).Within(0.01f),
                "a plain int must keep the default height — otherwise the drawer is attached too widely");
        }

        // ───────────── the write path the drawer uses ─────────────

        [Test]
        public void K2_NameWrittenThroughSerializedProperty_ReachesTheLink()
        {
            foreach (var path in new[] { "one", "many.Array.data[1]", "list.Array.data[0]", "direct", "scene" })
            {
                var nameProperty = Find(path).FindPropertyRelative("m_Name");
                Assert.That(nameProperty, Is.Not.Null, $"'{path}' has no m_Name");
                nameProperty.stringValue = "Icons/single";
            }
            m_Object.ApplyModifiedProperties();

            Assert.That(m_Fixture.one.Name, Is.EqualTo("Icons/single"));
            Assert.That(m_Fixture.many[1].Name, Is.EqualTo("Icons/single"));
            Assert.That(m_Fixture.list[0].Name, Is.EqualTo("Icons/single"));
            Assert.That(m_Fixture.direct.Name, Is.EqualTo("Icons/single"));
            Assert.That(m_Fixture.scene.Name, Is.EqualTo("Icons/single"));
            Assert.That(m_Fixture.many[0].Name, Is.Empty, "untouched elements stay empty");
        }

        // ───────────── the field type the object field will use ─────────────

        [Test]
        public void K3_ObjectFieldType_FollowsTheLinkType()
        {
            var t = typeof(DrawerFixture);
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("one")), Is.EqualTo(typeof(Sprite)));
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("many")), Is.EqualTo(typeof(Sprite)), "array element type");
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("list")), Is.EqualTo(typeof(GameObject)), "List<> element type");
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("direct")), Is.EqualTo(typeof(Texture2D)));
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("scene")), Is.Null, "a scene link is not typed");
            Assert.That(ContentLinkEditorUtility.AssetTypeOf(t.GetField("notALink")), Is.Null);
        }
    }
}
