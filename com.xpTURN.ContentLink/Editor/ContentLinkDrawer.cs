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
    /// The drawer holds no state. Only <c>m_Name</c> is written, and only through the SerializedProperty, so undo,
    /// prefab override display and multi-object editing come from the inspector rather than from here.
    /// A rejected pick leaves the name alone and says why in the console.
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
                DrawObjectField(fieldRect, nameProp, assetType);
                if (GUI.Button(resetRect, "Reset")) nameProp.stringValue = string.Empty;
                DrawNameField(nameRect, nameProp, assetType);
            }
            finally
            {
                EditorGUI.indentLevel = indent;
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUI.EndProperty();
            }
        }

        void DrawObjectField(Rect rect, SerializedProperty nameProp, Type assetType)
        {
            var current = nameProp.hasMultipleDifferentValues ? null : ContentLinkEditorUtility.ResolveAsset(nameProp.stringValue);

            EditorGUI.BeginChangeCheck();
            // allowSceneObjects must be false: the overload without it lets scene objects through.
            var picked = EditorGUI.ObjectField(rect, current, ObjectFieldType(assetType), false);
            if (!EditorGUI.EndChangeCheck()) return;

            if (picked == null) { nameProp.stringValue = string.Empty; return; }
            if (ContentLinkEditorUtility.TryGetNameForAsset(picked, out var name, out var reason))
                nameProp.stringValue = name;
            else
                Debug.LogWarning($"[ContentLink] {reason}");   // the name is left as it was; that is the refusal
        }

        void DrawNameField(Rect rect, SerializedProperty nameProp, Type assetType)
        {
            var status = nameProp.hasMultipleDifferentValues
                ? NameStatus.Ok
                : ContentLinkEditorUtility.Validate(nameProp.stringValue, assetType, IsScene);

            var background = GUI.backgroundColor;
            if (status == NameStatus.TypeMismatch) GUI.backgroundColor = k_Bad;
            else if (status == NameStatus.NoCatalog || status == NameStatus.NotInCatalog) GUI.backgroundColor = k_Warn;

            // This overload does BeginProperty, the change check and the write itself — do not write the name again.
            EditorGUI.DelayedTextField(rect, nameProp, GUIContent.none);
            GUI.backgroundColor = background;

            if (status != NameStatus.Ok && status != NameStatus.Empty)
                GUI.Label(rect, new GUIContent(string.Empty, ContentLinkEditorUtility.Describe(status, nameProp.stringValue)));
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
