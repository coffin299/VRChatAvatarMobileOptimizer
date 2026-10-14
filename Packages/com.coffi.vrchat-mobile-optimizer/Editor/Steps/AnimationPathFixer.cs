using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// アバターのコントローラを複製し、統合したメッシュへの BlendShape アニメーションのパスを書き換える。
    /// 削除した対象へのバインディングは取り除く。
    /// </summary>
    internal static class AnimationPathFixer
    {
        public static void Run(OptimizerContext ctx, AnimatorDeepCloner cloner)
        {
            ReplaceDescriptorControllers(ctx, cloner);
            var animator = ctx.Animator;
            if (animator != null && animator.runtimeAnimatorController != null)
                animator.runtimeAnimatorController = cloner.CloneController(animator.runtimeAnimatorController);

            int removed = 0, remapped = 0;
            foreach (var clip in cloner.ClonedClips)
                FixClip(ctx, clip, ref removed, ref remapped);

            cloner.SaveAll(ctx.Assets, ctx.Root.name);
            if (remapped > 0) ctx.Report.Info($"アニメーションのバインディング {remapped} 個を統合後のメッシュへ付け替えました");
            if (removed > 0) ctx.Report.AddRemoved("アニメーションのバインディング", $"{removed} 個 (対象が削除・統合されたもの)");
        }

        private static void ReplaceDescriptorControllers(OptimizerContext ctx, AnimatorDeepCloner cloner)
        {
            if (ctx.Descriptor == null) return;
            var so = new SerializedObject(ctx.Descriptor);
            foreach (var name in new[] { "baseAnimationLayers", "specialAnimationLayers" })
            {
                var layers = so.FindProperty(name);
                if (layers == null) continue;
                for (int i = 0; i < layers.arraySize; i++)
                {
                    var prop = layers.GetArrayElementAtIndex(i).FindPropertyRelative("animatorController");
                    if (prop?.objectReferenceValue is RuntimeAnimatorController rac)
                        prop.objectReferenceValue = cloner.CloneController(rac);
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void FixClip(OptimizerContext ctx, AnimationClip clip, ref int removed, ref int remapped)
        {
            var existing = new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(clip));
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var nb = Resolve(ctx, b, out bool remove);
                if (remove)
                {
                    AnimationUtility.SetEditorCurve(clip, b, null);
                    removed++;
                }
                else if (nb.path != b.path)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    AnimationUtility.SetEditorCurve(clip, b, null);
                    if (existing.Contains(nb))
                    {
                        removed++;
                        continue;
                    }
                    AnimationUtility.SetEditorCurve(clip, nb, curve);
                    existing.Add(nb);
                    remapped++;
                }
            }

            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                Resolve(ctx, b, out bool remove);
                if (remove)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                    removed++;
                }
            }
        }

        private static EditorCurveBinding Resolve(OptimizerContext ctx, EditorCurveBinding b, out bool remove)
        {
            remove = false;
            var nb = b;
            bool isBlendShape = b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.");
            if (isBlendShape && ctx.RendererPathRemap.TryGetValue(b.path, out var newPath)) nb.path = newPath;

            var t = PathUtil.Find(ctx.RootTransform, nb.path);
            if (t == null) { remove = true; return nb; }
            if (b.type == null) { remove = true; return nb; }
            if (b.type == typeof(GameObject)) return nb;

            var component = t.GetComponent(b.type);
            if (component == null) { remove = true; return nb; }

            if (isBlendShape)
            {
                var mesh = ((SkinnedMeshRenderer)component).sharedMesh;
                var shapeName = b.propertyName.Substring("blendShape.".Length);
                if (mesh == null || mesh.GetBlendShapeIndex(shapeName) < 0) remove = true;
            }
            else if (ctx.Settings.atlasTextures && component is Renderer
                     && (b.propertyName.StartsWith("material.") || b.propertyName.StartsWith("m_Materials")))
            {
                // アトラス化したマテリアルではプロパティ・差し替えアニメーションが意味をなさない
                remove = true;
            }
            return nb;
        }
    }
}
