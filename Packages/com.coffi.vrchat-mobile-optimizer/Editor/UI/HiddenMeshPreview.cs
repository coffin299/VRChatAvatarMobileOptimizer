using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Coffi.MobileOptimizer
{
    /// <summary>削除予定の隠れポリゴンを Scene ビューに赤く重ねて表示する (服を透過して見えるよう ZTest Always)。</summary>
    internal static class HiddenMeshPreview
    {
        private static Mesh _mesh;
        private static Material _material;

        public static bool IsActive => _mesh != null;

        public static void Show(Mesh mesh)
        {
            Clear();
            if (mesh == null) return;
            _mesh = mesh;
            if (_material == null)
            {
                _material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetColor("_Color", new Color(1f, 0.1f, 0.1f, 0.55f));
                _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _material.SetInt("_Cull", (int)CullMode.Off);
                _material.SetInt("_ZWrite", 0);
                _material.SetInt("_ZTest", (int)CompareFunction.Always);
            }
            SceneView.duringSceneGui += OnSceneGUI;
            SceneView.RepaintAll();
        }

        public static void Clear()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (_mesh != null) Object.DestroyImmediate(_mesh);
            _mesh = null;
            SceneView.RepaintAll();
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (_mesh == null || Event.current.type != EventType.Repaint) return;
            _material.SetPass(0);
            Graphics.DrawMeshNow(_mesh, Matrix4x4.identity);
        }
    }
}
