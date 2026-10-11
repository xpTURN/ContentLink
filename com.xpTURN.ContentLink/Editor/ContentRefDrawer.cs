using System;
using UnityEditor;
using UnityEngine;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Two lines for a serialized reference: an object field that turns the picked asset into its GUID (and sprite name),
    /// and a read-only line with the catalog name it resolves to, coloured by what the catalogs and the project say.
    ///
    /// Same rules as <see cref="LinkDrawerBase"/>: no state, and the value is written only through the
    /// SerializedProperty (m_AssetGUID and m_SubObjectName), so undo, prefab overrides and multi-object editing come
    /// from the inspector. A rejected pick leaves the value alone and says why in the console. A value already in the
    /// field — one carried over from Addressables — is never rejected, only shown with its status.
    /// </summary>
    public abstract class RefDrawerBase : PropertyDrawer
    {
        const float k_ResetWidth = 55f;
        const float k_Gap = 4f;
        const float k_MinLabelWidth = 50f;
        const float k_LabelRatio = 0.35f;
        const float k_IconWidth = 18f;

        static readonly Color k_Warn = new Color(1f, 0.92f, 0.55f);
        static readonly Color k_Bad = new Color(1f, 0.5f, 0.5f);

        /// <summary>Scene references resolve against the catalogs' scene lists.</summary>
        protected abstract bool IsScene { get; }

        /// <summary>The T the field holds; null when the reference is not typed (scene references).</summary>
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
                var guidProp = property.FindPropertyRelative("m_AssetGUID");
                var subProp = property.FindPropertyRelative("m_SubObjectName");   // null for a scene reference
                if (guidProp == null)
                {
                    EditorGUI.LabelField(position, label, new GUIContent("unsupported reference"));
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
                bool mixed = property.hasMultipleDifferentValues;   // true when either child differs
                DrawObjectField(fieldRect, guidProp, subProp, assetType, mixed);
                if (GUI.Button(resetRect, "Reset")) { Write(guidProp, subProp, "", ""); GUIUtility.keyboardControl = 0; }
                DrawNameLine(nameRect, property, guidProp.stringValue, subProp != null ? subProp.stringValue : "", assetType, mixed);
            }
            finally
            {
                EditorGUI.indentLevel = indent;
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUI.EndProperty();
            }
        }

        void DrawObjectField(Rect rect, SerializedProperty guidProp, SerializedProperty subProp, Type assetType, bool mixed)
        {
            if (AtlasDrop.Handle(rect, assetType, guidProp, subProp, reference: true)) return;
            var current = mixed ? null : ContentLinkEditorUtility.ResolveRefAsset(guidProp.stringValue, subProp != null ? subProp.stringValue : "");

            EditorGUI.BeginChangeCheck();
            // allowSceneObjects must be false: the overload without it lets scene objects through. Mixed values show as
            // mixed on their own: BeginProperty set showMixedValue from the property.
            var picked = EditorGUI.ObjectField(rect, current, ObjectFieldType(assetType), false);
            if (!EditorGUI.EndChangeCheck()) return;

            if (picked == null) { Write(guidProp, subProp, "", ""); return; }
            if (ContentLinkEditorUtility.TryGetRefForAsset(picked, out var guid, out var sub, out var reason))
                Write(guidProp, subProp, guid, sub);
            else
                Debug.LogWarning($"[ContentLink] {reason}");   // the value is left as it was; that is the refusal
        }

        static void Write(SerializedProperty guidProp, SerializedProperty subProp, string guid, string sub)
        {
            guidProp.stringValue = guid;
            if (subProp != null) subProp.stringValue = sub;
        }

        void DrawNameLine(Rect rect, SerializedProperty property, string guid, string sub, Type assetType, bool mixed)
        {
            var iconRect = new Rect(rect.x - k_IconWidth, rect.y, k_IconWidth, rect.height);
            if (GUI.Button(iconRect, EditorGUIUtility.IconContent("Linked"), GUI.skin.label))
            {
                property.isExpanded = !property.isExpanded;   // per field: the editor keeps it like a foldout's state
                GUIUtility.keyboardControl = 0;   // a selected name keeps drawing the text it was selected with
            }
            bool showGuid = property.isExpanded;

            string text, tooltip = null;
            var status = NameStatus.Ok;
            if (mixed) text = "—";
            else
            {
                status = ContentLinkEditorUtility.ValidateRef(guid, sub, assetType, IsScene, out tooltip);
                var name = status == NameStatus.Empty || showGuid ? null : ContentLinkEditorUtility.NameForRef(guid, sub, IsScene);
                text = status == NameStatus.Empty ? "none" : name ?? (sub.Length == 0 ? guid : $"{guid}[{sub}]");
            }

            var background = GUI.backgroundColor;
            if (status == NameStatus.TypeMismatch || status == NameStatus.Missing) GUI.backgroundColor = k_Bad;
            else if (status == NameStatus.NoCatalog || status == NameStatus.NotInCatalog) GUI.backgroundColor = k_Warn;

            EditorGUI.SelectableLabel(rect, text, EditorStyles.textField);   // read-only but copyable: the name follows the asset, it is not typed
            GUI.backgroundColor = background;

            if (status != NameStatus.Ok && status != NameStatus.Empty)
                GUI.Label(rect, new GUIContent(string.Empty, tooltip));
        }
    }

    [CustomPropertyDrawer(typeof(ContentRef<>))]   // a struct has no subclasses, so useForChildren is not needed
    public sealed class ContentRefDrawer : RefDrawerBase
    {
        protected override bool IsScene => false;

        protected override Type AssetTypeOf(SerializedProperty property)
        {
            var type = ContentLinkEditorUtility.AssetTypeOfRef(fieldInfo);
            if (type != null) return type;
            // fieldInfo can be null in nested / managed-reference contexts; the value still knows its own type.
            try { return ContentLinkEditorUtility.AssetTypeOfRefType(property.boxedValue?.GetType()); }
            catch { return null; }
        }

        protected override Type ObjectFieldType(Type assetType) => ContentLinkEditorUtility.ObjectFieldTypeFor(assetType);
    }

    /// <summary>Same two lines as <see cref="ContentRefDrawer"/>, against the catalogs' scene entries.</summary>
    [CustomPropertyDrawer(typeof(ContentSceneRef))]
    public sealed class ContentSceneRefDrawer : RefDrawerBase
    {
        protected override bool IsScene => true;
        protected override Type AssetTypeOf(SerializedProperty property) => null;
        protected override Type ObjectFieldType(Type assetType) => typeof(SceneAsset);
    }
}
