using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>処理結果のレポート表示。</summary>
    internal class ReportView
    {
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();

        public void Draw(OptimizerResult result)
        {
            if (result == null) return;
            var report = result.Report;

            if (report.Error != null)
            {
                EditorGUILayout.HelpBox($"エラーで中断しました: {report.Error}", MessageType.Error);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("出力先", result.Folder ?? "-");
                if (result.Prefab != null && GUILayout.Button("Prefab を選択", GUILayout.Width(110)))
                    EditorGUIUtility.PingObject(result.Prefab);
            }

            foreach (var w in report.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
            foreach (var i in report.Infos) EditorGUILayout.HelpBox(i, MessageType.Info);

            foreach (var kv in report.Removed)
            {
                _foldouts.TryGetValue(kv.Key, out bool open);
                open = EditorGUILayout.Foldout(open, $"{kv.Key} ({kv.Value.Count})", true);
                _foldouts[kv.Key] = open;
                if (!open) continue;
                using (new EditorGUI.IndentLevelScope())
                    foreach (var item in kv.Value)
                        EditorGUILayout.LabelField(item, EditorStyles.miniLabel);
            }
        }
    }
}
