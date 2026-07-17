using UnityEditor;
using UnityEngine;
using GlobalExpansion.Globe;

namespace GlobalExpansion.GlobeEditor
{
    /// <summary>
    /// GlobeGenerator 的自定义面板：在编辑器里直接用按钮生成 / 清除网格，
    /// 无需进入 Play 模式（也可用组件右上角 ⋮ 菜单的 Generate / Clear）。
    /// </summary>
    [CustomEditor(typeof(GlobeGenerator))]
    public class GlobeGeneratorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            GlobeGenerator generator = (GlobeGenerator)target;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate", GUILayout.Height(28)))
                {
                    generator.Generate();
                    // 编辑器下标记场景为脏，方便保存/刷新
                    if (!Application.isPlaying)
                        EditorUtility.SetDirty(generator);
                }

                if (GUILayout.Button("Clear", GUILayout.Height(28)))
                {
                    generator.Clear();
                    if (!Application.isPlaying)
                        EditorUtility.SetDirty(generator);
                }
            }

            EditorGUILayout.HelpBox(
                "Generate builds the three layers in the editor. Meshes are procedural " +
                "(not saved as assets), so re-run Generate after reopening the scene.",
                MessageType.Info);
        }
    }
}
