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

        // ───────────── references (ContentRef) ─────────────

        void AssertDrawnByRefDrawer(string path)
            => Assert.That(EditorGUI.GetPropertyHeight(Find(path)), Is.EqualTo(TwoLines).Within(0.01f),
                $"'{path}' is not drawn by the reference drawer");

        [Test]
        public void K4_RefDrawer_CoversTheGenericStruct_CollectionElements_AndScene()
        {
            AssertDrawnByRefDrawer("refOne");                   // ContentRef<Sprite>: found through the open generic, no useForChildren
            AssertDrawnByRefDrawer("refMany.Array.data[0]");
            AssertDrawnByRefDrawer("refList.Array.data[0]");
            AssertDrawnByRefDrawer("refScene");
        }

        [Test]
        public void K5_GuidAndSubNameWrittenThroughSerializedProperty_ReachTheReference()
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            foreach (var path in new[] { "refOne", "refMany.Array.data[1]", "refList.Array.data[0]" })
            {
                var property = Find(path);
                property.FindPropertyRelative("m_AssetGUID").stringValue = guid;
                property.FindPropertyRelative("m_SubObjectName").stringValue = "s0";
            }
            var scene = Find("refScene");
            scene.FindPropertyRelative("m_AssetGUID").stringValue = guid;
            Assert.That(scene.FindPropertyRelative("m_SubObjectName"), Is.Null, "a scene reference has no sub-asset name");
            m_Object.ApplyModifiedProperties();

            Assert.That(m_Fixture.refOne, Is.EqualTo(new ContentRef<Sprite>(guid, "s0")));
            Assert.That(m_Fixture.refMany[1], Is.EqualTo(new ContentRef<Sprite>(guid, "s0")));
            Assert.That(m_Fixture.refList[0], Is.EqualTo(new ContentRef<GameObject>(guid, "s0")));
            Assert.That(m_Fixture.refScene, Is.EqualTo(new ContentSceneRef(guid)));
            Assert.That(m_Fixture.refMany[0].IsSet, Is.False, "untouched elements stay unset");
        }

        [Test]
        public void K6_RefFieldType_FollowsTheReferenceType()
        {
            var t = typeof(DrawerFixture);
            Assert.That(ContentLinkEditorUtility.AssetTypeOfRef(t.GetField("refOne")), Is.EqualTo(typeof(Sprite)));
            Assert.That(ContentLinkEditorUtility.AssetTypeOfRef(t.GetField("refMany")), Is.EqualTo(typeof(Sprite)), "array element type");
            Assert.That(ContentLinkEditorUtility.AssetTypeOfRef(t.GetField("refList")), Is.EqualTo(typeof(GameObject)), "List<> element type");
            Assert.That(ContentLinkEditorUtility.AssetTypeOfRef(t.GetField("refScene")), Is.Null, "a scene reference is not typed");
            Assert.That(ContentLinkEditorUtility.AssetTypeOfRef(t.GetField("one")), Is.Null, "a link is not a reference");
        }

        [Test]
        public void K7_TheParentProperty_ReportsMixedValues_WhenOnlyTheSubNameDiffers()
        {
            // The drawer reads hasMultipleDifferentValues from the struct property, not from its two children.
            var other = ScriptableObject.CreateInstance<DrawerFixture>();
            try
            {
                m_Fixture.refOne = new ContentRef<Sprite>("0123456789abcdef0123456789abcdef", "a");
                other.refOne = new ContentRef<Sprite>("0123456789abcdef0123456789abcdef", "b");
                using var both = new SerializedObject(new Object[] { m_Fixture, other });
                Assert.That(both.FindProperty("refOne").hasMultipleDifferentValues, Is.True);
                other.refOne = m_Fixture.refOne;
                using var same = new SerializedObject(new Object[] { m_Fixture, other });
                Assert.That(same.FindProperty("refOne").hasMultipleDifferentValues, Is.False);
            }
            finally { Object.DestroyImmediate(other); }
        }

        // ───────────── links with a sprite name ─────────────

        [Test]
        public void K8_LinkSpriteName_WrittenThroughSerializedProperty_ReachesTheLink_AndASceneLinkHasNone()
        {
            foreach (var path in new[] { "one", "many.Array.data[1]", "list.Array.data[0]", "direct" })
            {
                var property = Find(path);
                Assert.That(property.FindPropertyRelative("m_SubObjectName"), Is.Not.Null, $"'{path}' has no m_SubObjectName");
                property.FindPropertyRelative("m_Name").stringValue = "Icons/sheet";
                property.FindPropertyRelative("m_SubObjectName").stringValue = "s0";
            }
            Assert.That(Find("scene").FindPropertyRelative("m_SubObjectName"), Is.Null, "a scene link has no sprite name: the drawer keeps its one-field line");
            m_Object.ApplyModifiedProperties();

            Assert.That((m_Fixture.one.Name, m_Fixture.one.SubObjectName), Is.EqualTo(("Icons/sheet", "s0")));
            Assert.That((m_Fixture.many[1].Name, m_Fixture.many[1].SubObjectName), Is.EqualTo(("Icons/sheet", "s0")));
            Assert.That((m_Fixture.list[0].Name, m_Fixture.list[0].SubObjectName), Is.EqualTo(("Icons/sheet", "s0")));
            Assert.That((m_Fixture.direct.Name, m_Fixture.direct.SubObjectName), Is.EqualTo(("Icons/sheet", "s0")));
            Assert.That(m_Fixture.many[0].SubObjectName, Is.Empty, "untouched elements stay empty");
            AssertDrawnByLinkDrawer("one");   // still two lines with the sprite field on the second
        }

        [Test]
        public void K9_TheLinkProperty_ReportsMixedValues_WhenOnlyTheSpriteNameDiffers()
        {
            var other = ScriptableObject.CreateInstance<DrawerFixture>();
            try
            {
                m_Fixture.one.SetName("Icons/sheet", "s0");
                other.one.SetName("Icons/sheet", "s1");
                using var both = new SerializedObject(new Object[] { m_Fixture, other });
                Assert.That(both.FindProperty("one").hasMultipleDifferentValues, Is.True);
                Assert.That(both.FindProperty("one.m_Name").hasMultipleDifferentValues, Is.False);
            }
            finally { Object.DestroyImmediate(other); }
        }
    }
}
