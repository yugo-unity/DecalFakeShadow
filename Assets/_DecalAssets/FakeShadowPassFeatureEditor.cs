using UnityEngine;

namespace UTJ {
    /// <summary>
    /// intのフィールドをRendering Layerのビット番号 (1つだけ、-1は未設定) として表示する
    /// NOTE: RenderingLayerMaskのフィールドはマスク入力になり、複数のビットを選べてしまう
    /// </summary>
    public class RenderingLayerIndexAttribute : PropertyAttribute { }

#if UNITY_EDITOR
    /// <summary>
    /// 定義済みのRendering Layerの名前から1つを選ぶポップアップ (未定義になったビットは未設定として表示する)
    /// </summary>
    [UnityEditor.CustomPropertyDrawer(typeof(RenderingLayerIndexAttribute))]
    internal class RenderingLayerIndexDrawer : UnityEditor.PropertyDrawer {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label) {
            if (property.propertyType != UnityEditor.SerializedPropertyType.Integer) {
                UnityEditor.EditorGUI.LabelField(position, label.text, "Use RenderingLayerIndex with int.");
                return;
            }

            var names = new System.Collections.Generic.List<string> { "None" };
            var layers = new System.Collections.Generic.List<int> { -1 };
            for (var i = 0; i < 32; ++i) {
                if (!FakeShadowManager.IsDefinedRenderingLayer(i))
                    continue;
                names.Add($"{i}: {RenderingLayerMask.RenderingLayerToName(i)}");
                layers.Add(i);
            }

            var current = Mathf.Max(0, layers.IndexOf(property.intValue));
            using (var scope = new UnityEditor.EditorGUI.PropertyScope(position, label, property)) {
                UnityEditor.EditorGUI.BeginChangeCheck();
                var selected = UnityEditor.EditorGUI.Popup(position, scope.content, current, ToContents(names));
                if (UnityEditor.EditorGUI.EndChangeCheck())
                    property.intValue = layers[selected];
            }
        }

        private static GUIContent[] ToContents(System.Collections.Generic.List<string> names) {
            var contents = new GUIContent[names.Count];
            for (var i = 0; i < contents.Length; ++i)
                contents[i] = new GUIContent(names[i]);
            return contents;
        }
    }

    /// <summary>
    /// FakeShadowPassFeatureのInspector (既定の表示にVerifyボタンを追加する)
    /// NOTE: URPのRendererのInspector (ScriptableRendererDataEditor) はFeatureをEditor.CreateEditorで描画するので、このEditorが使われる
    ///       URPのランタイムアセンブリ (URPRuntime.asmref) のinternalを参照する為、Editorフォルダではなくここに置く
    /// </summary>
    [UnityEditor.CustomEditor(typeof(FakeShadowPassFeature))]
    internal class FakeShadowPassFeatureEditor : UnityEditor.Editor {
        private static readonly GUIContent VERIFY_LABEL = new GUIContent("Verify",
            "Logs renderers that have the Shadow Caster Rendering Layer without an active shadow, and lights and decal projectors that include it.");

        public override void OnInspectorGUI() {
            this.serializedObject.Update();
            DrawPropertiesExcluding(this.serializedObject, "m_Script");
            this.serializedObject.ApplyModifiedProperties();

            UnityEditor.EditorGUILayout.Space();
            if (GUILayout.Button(VERIFY_LABEL))
                ((FakeShadowPassFeature)this.target).Verify();
        }
    }
#endif
}
