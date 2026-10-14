using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Coffi.MobileOptimizer
{
    /// <summary>アバターのアニメーションが何を動かしているかの集計。</summary>
    internal class AnimationInfo
    {
        public readonly HashSet<string> ToggledPaths = new HashSet<string>();
        public readonly Dictionary<string, HashSet<string>> BlendShapes = new Dictionary<string, HashSet<string>>();
        public readonly HashSet<string> TransformPaths = new HashSet<string>();

        public bool IsToggled(Transform t, Transform root)
        {
            for (var c = t; c != null && c != root; c = c.parent)
                if (ToggledPaths.Contains(PathUtil.GetPath(c, root))) return true;
            return false;
        }

        public HashSet<string> BlendShapesOf(string path)
        {
            return path != null && BlendShapes.TryGetValue(path, out var set) ? set : new HashSet<string>();
        }
    }

    internal static class AnimationAnalyzer
    {
        public static AnimationInfo Analyze(GameObject root)
        {
            var info = new AnimationInfo();
            var clips = GetControllers(root)
                .Where(c => c != null)
                .SelectMany(c => c.animationClips)
                .Where(c => c != null)
                .Distinct();

            foreach (var clip in clips)
            {
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                        info.ToggledPaths.Add(b.path);
                    else if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName == "m_Enabled")
                        info.ToggledPaths.Add(b.path);
                    else if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape."))
                    {
                        if (!info.BlendShapes.TryGetValue(b.path, out var set))
                        {
                            set = new HashSet<string>();
                            info.BlendShapes[b.path] = set;
                        }
                        set.Add(b.propertyName.Substring("blendShape.".Length));
                    }
                    else if (b.type == typeof(Transform))
                        info.TransformPaths.Add(b.path);
                }
            }
            return info;
        }

        /// <summary>Descriptor の全レイヤーとルート Animator のコントローラ。</summary>
        public static List<RuntimeAnimatorController> GetControllers(GameObject root)
        {
            var result = new List<RuntimeAnimatorController>();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                var so = new SerializedObject(descriptor);
                foreach (var name in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    var layers = so.FindProperty(name);
                    if (layers == null) continue;
                    for (int i = 0; i < layers.arraySize; i++)
                    {
                        var c = layers.GetArrayElementAtIndex(i).FindPropertyRelative("animatorController");
                        if (c?.objectReferenceValue is RuntimeAnimatorController rac) result.Add(rac);
                    }
                }
            }
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null) result.Add(animator.runtimeAnimatorController);
            return result.Distinct().ToList();
        }
    }
}
