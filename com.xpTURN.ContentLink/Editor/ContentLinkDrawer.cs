using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Two lines for a serialized link: an object field that converts the picked asset to its catalog name,
    /// and the name itself, coloured by what the catalogs say about it.
    ///
    /// The drawer holds no state. Only <c>m_Name</c> (and <c>m_SubObjectName</c>, for a link that has one) is written, and
    /// only through the SerializedProperty, so undo, prefab override display and multi-object editing come from the
    /// inspector rather than from here. A rejected pick leaves the value alone and says why in the console.
    /// </summary>
    public abstract class LinkDrawerBase : PropertyDrawer
    {
        const float k_ResetWidth = 55f;
        const float k_Gap = 4f;
        const float k_MinLabelWidth = 50f;
        const float k_LabelRatio = 0.35f;

        static readonly Color k_Warn = new Color(1f, 0.92f, 0.55f);
        static readonly Color k_Bad = new Color(1f, 0.5f, 0.5f);

        /// <summary>Scene links look in the catalog's scene list instead of its asset entries.</summary>
        protected abstract bool IsScene { get; }

        /// <summary>The T the field holds; null when the link is not typed (scene links).</summary>
        protected abstract Type AssetTypeOf(SerializedProperty property);

        /// <summary>What the object field accepts.</summary>
        protected abstract Type ObjectFieldType(Type assetType);

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUIUtility.singleLineHeight * 2 + EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // The returned label is what carries the prefab override state and its context menu.
            label = EditorGUI.BeginProperty(position, label, property);
            float labelWidth = EditorGUIUtility.labelWidth;
            int indent = EditorGUI.indentLevel;
            try
            {
                var nameProp = property.FindPropertyRelative("m_Name");
                var subProp = property.FindPropertyRelative("m_SubObjectName");   // null for a scene link
                if (nameProp == null)
                {
                    EditorGUI.LabelField(position, label, new GUIContent("unsupported link"));
                    return;
                }

                float line = EditorGUIUtility.singleLineHeight;
                var first = new Rect(position.x, position.y, position.width, line);

                EditorGUIUtility.labelWidth = Mathf.Max(k_MinLabelWidth, position.width * k_LabelRatio);
                var body = EditorGUI.PrefixLabel(first, GUIUtility.GetControlID(FocusType.Passive), label);
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUI.indentLevel = 0;

                var fieldRect = new Rect(body.x, body.y, Mathf.Max(0f, body.width - k_ResetWidth - k_Gap), line);
                var resetRect = new Rect(body.xMax - k_ResetWidth, body.y, k_ResetWidth, line);
                var nameRect = new Rect(body.x, body.y + line + EditorGUIUtility.standardVerticalSpacing, body.width, line);

                var assetType = AssetTypeOf(property);
                // A link with a sprite name is mixed when either child is; a scene link keeps its name-only check.
                bool mixed = subProp != null ? property.hasMultipleDifferentValues : nameProp.hasMultipleDifferentValues;
                DrawObjectField(fieldRect, nameProp, subProp, assetType, mixed);
                if (GUI.Button(resetRect, "Reset"))
                {
                    Write(nameProp, subProp, string.Empty, string.Empty);
                    GUIUtility.keyboardControl = 0;   // a focused text field keeps drawing the text it was edited with
                }
                DrawNameField(nameRect, nameProp, subProp, assetType, mixed);
            }
            finally
            {
                EditorGUI.indentLevel = indent;
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUI.EndProperty();
            }
        }

        void DrawObjectField(Rect rect, SerializedProperty nameProp, SerializedProperty subProp, Type assetType, bool mixed)
        {
            var current = mixed ? null
                : subProp != null ? ContentLinkEditorUtility.ResolveAsset(nameProp.stringValue, subProp.stringValue)
                : ContentLinkEditorUtility.ResolveAsset(nameProp.stringValue);

            EditorGUI.BeginChangeCheck();
            // allowSceneObjects must be false: the overload without it lets scene objects through.
            var picked = EditorGUI.ObjectField(rect, current, ObjectFieldType(assetType), false);
            if (!EditorGUI.EndChangeCheck()) return;

            if (picked == null) { Write(nameProp, subProp, string.Empty, string.Empty); return; }
            string name, sub = null, reason;
            bool ok = subProp != null
                ? ContentLinkEditorUtility.TryGetLinkForAsset(picked, out name, out sub, out reason)
                : ContentLinkEditorUtility.TryGetNameForAsset(picked, out name, out reason);
            if (!ok)
            {
                Debug.LogWarning($"[ContentLink] {reason}");   // the value is left as it was; that is the refusal
                return;
            }
            Write(nameProp, subProp, name, sub);
            GUIUtility.keyboardControl = 0;
        }

        /// <summary>Writes only the children whose value changes, so a pick that keeps the sprite name leaves no override on it.</summary>
        static void Write(SerializedProperty nameProp, SerializedProperty subProp, string name, string sub)
        {
            if (nameProp.hasMultipleDifferentValues || nameProp.stringValue != name) nameProp.stringValue = name;
            if (subProp != null && (subProp.hasMultipleDifferentValues || subProp.stringValue != sub)) subProp.stringValue = sub;
        }

        void DrawNameField(Rect rect, SerializedProperty nameProp, SerializedProperty subProp, Type assetType, bool mixed)
        {
            var status = mixed ? NameStatus.Ok
                : subProp != null ? ContentLinkEditorUtility.Validate(nameProp.stringValue, subProp.stringValue, assetType)
                : ContentLinkEditorUtility.Validate(nameProp.stringValue, assetType, IsScene);

            var background = GUI.backgroundColor;
            if (status == NameStatus.TypeMismatch) GUI.backgroundColor = k_Bad;
            else if (status == NameStatus.NoCatalog || status == NameStatus.NotInCatalog) GUI.backgroundColor = k_Warn;

            string shown;
            if (subProp == null)
            {
                // This overload does BeginProperty, the change check and the write itself — do not write the name again.
                EditorGUI.DelayedTextField(rect, nameProp, GUIContent.none);
                shown = nameProp.stringValue;
            }
            else
            {
                // One field in the catalog's own form, "Icons/sheet[s0]"; a typed name is split against the catalogs, not parsed.
                // Mixed values show as mixed on their own: BeginProperty set showMixedValue from the property.
                shown = mixed ? string.Empty : ContentLinkEditorUtility.LinkName(nameProp.stringValue, subProp.stringValue);
                EditorGUI.BeginChangeCheck();
                var typed = EditorGUI.DelayedTextField(rect, shown);
                if (EditorGUI.EndChangeCheck() && (mixed || typed != shown))
                {
                    ContentLinkEditorUtility.SplitLinkName(typed, out var name, out var sub);
                    Write(nameProp, subProp, name, sub);
                }
            }
            GUI.backgroundColor = background;

            if (status != NameStatus.Ok && status != NameStatus.Empty)
                GUI.Label(rect, new GUIContent(string.Empty, ContentLinkEditorUtility.Describe(status, shown)));
        }
    }

    [CustomPropertyDrawer(typeof(ContentLink<>), true)]   // useForChildren is required for ContentLinkSprite and friends
    public sealed class ContentLinkDrawer : LinkDrawerBase
    {
        protected override bool IsScene => false;

        protected override Type AssetTypeOf(SerializedProperty property)
        {
            var type = ContentLinkEditorUtility.AssetTypeOf(fieldInfo);
            if (type != null) return type;
            // fieldInfo can be null in nested / managed-reference contexts; the instance still knows its own type.
            try { return ContentLinkEditorUtility.AssetTypeOfLinkType(property.boxedValue?.GetType()); }
            catch { return null; }
        }

        protected override Type ObjectFieldType(Type assetType) => ContentLinkEditorUtility.ObjectFieldTypeFor(assetType);
    }
}
