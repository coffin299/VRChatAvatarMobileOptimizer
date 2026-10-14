using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Coffi.MobileOptimizer
{
    /// <summary>髪以外の PhysBone、Mobile 非対応コンポーネント、非表示オブジェクトなどを削除する。</summary>
    internal static class ComponentStripper
    {
        /// <summary>EditorOnly とスクリプト欠落は常に削除する。</summary>
        public static void RemoveEditorOnly(OptimizerContext ctx)
        {
            var root = ctx.RootTransform;
            foreach (var t in root.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (t == null || t == root) continue;
                if (t.CompareTag("EditorOnly"))
                {
                    ctx.Report.AddRemoved("EditorOnly", t.name);
                    Object.DestroyImmediate(t.gameObject);
                }
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (removed > 0) ctx.Report.AddRemoved("スクリプト欠落", $"{t.name} ({removed})");
            }
        }

        /// <summary>非表示・トグル対象のレンダラーを設定に従って削除または「別メッシュとして残す」に振り分ける。</summary>
        public static void HandleRenderers(OptimizerContext ctx)
        {
            var s = ctx.Settings;
            var root = ctx.RootTransform;
            foreach (var r in ctx.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                bool visible = r.gameObject.activeInHierarchy && r.enabled;
                bool toggleable = ctx.Anim.IsToggled(r.transform, root);
                var path = PathUtil.GetPath(r.transform, root);

                bool remove;
                bool separate = false;
                if (!visible)
                {
                    if (!toggleable) remove = true;
                    else if (s.toggleMode == ToggleMode.Delete) remove = true;
                    else if (s.toggleMode == ToggleMode.KeepSeparate || !s.deleteInactiveObjects) { remove = false; separate = true; }
                    else remove = true;
                }
                else if (toggleable)
                {
                    remove = s.toggleMode == ToggleMode.Delete;
                    separate = s.toggleMode == ToggleMode.KeepSeparate;
                    if (s.toggleMode == ToggleMode.MergeAlwaysVisible)
                        ctx.Report.Warn($"トグル対象 '{path}' は統合されるため常に表示されます");
                }
                else remove = false;

                if (remove)
                {
                    ctx.Report.AddRemoved("非表示 / トグルのオブジェクト", path);
                    var mf = r.GetComponent<MeshFilter>();
                    Object.DestroyImmediate(r);
                    if (mf != null) Object.DestroyImmediate(mf);
                }
                else if (separate)
                {
                    ctx.SeparateRenderers.Add(r);
                }
            }
        }

        public static void Run(OptimizerContext ctx)
        {
            StripPhysBones(ctx);
            RemoveUnusedColliders(ctx);
            if (!ctx.Settings.keepContacts) RemoveContacts(ctx);
            ConvertUnityConstraints(ctx);
            RemoveDisallowedComponents(ctx);
        }

        public static bool ShouldKeepPhysBone(OptimizerContext ctx, VRCPhysBoneBase pb)
        {
            if (!ctx.PhysBoneKeys.TryGetValue(pb, out var key)) key = PhysBoneUtil.GetKey(pb, ctx.RootTransform);
            return ctx.Settings.GetOverride(key) ?? PhysBoneUtil.IsHairCandidate(pb, ctx.RootTransform, ctx.Settings.hairNamePatterns);
        }

        private static void StripPhysBones(OptimizerContext ctx)
        {
            foreach (var pb in ctx.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (ShouldKeepPhysBone(ctx, pb)) continue;
                ctx.Report.AddRemoved("PhysBone", PhysBoneUtil.GetKey(pb, ctx.RootTransform));
                Object.DestroyImmediate(pb);
            }
        }

        public static void RemoveUnusedColliders(OptimizerContext ctx)
        {
            var used = new HashSet<VRCPhysBoneColliderBase>(ctx.Root.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .SelectMany(pb => pb.colliders ?? new List<VRCPhysBoneColliderBase>())
                .Where(c => c != null));
            foreach (var c in ctx.Root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
            {
                if (used.Contains(c)) continue;
                ctx.Report.AddRemoved("PhysBone Collider", PathUtil.GetPath(c.transform, ctx.RootTransform));
                Object.DestroyImmediate(c);
            }
        }

        private static void RemoveContacts(OptimizerContext ctx)
        {
            foreach (var c in ctx.Root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !TypeUtil.IsContact(c.GetType())) continue;
                ctx.Report.AddRemoved("Contact", $"{PathUtil.GetPath(c.transform, ctx.RootTransform)} ({c.GetType().Name})");
                Object.DestroyImmediate(c);
            }
        }

        /// <summary>Unity Constraint を SDK の変換機能で VRC Constraint にする。変換できなければ削除する。</summary>
        private static void ConvertUnityConstraints(OptimizerContext ctx)
        {
            var constraints = ctx.Root.GetComponentsInChildren<Component>(true)
                .Where(c => c is IConstraint)
                .ToList();
            if (constraints.Count == 0) return;

            if (TryConvertWithSdk(ctx, constraints))
            {
                ctx.Report.Info($"Unity Constraint {constraints.Count} 個を VRC Constraint に変換しました");
                constraints = constraints.Where(c => c != null).ToList();
            }
            foreach (var c in constraints)
            {
                if (c == null) continue;
                ctx.Report.AddRemoved("Unity Constraint", $"{PathUtil.GetPath(c.transform, ctx.RootTransform)} ({c.GetType().Name})");
                Object.DestroyImmediate(c);
            }
        }

        private static bool TryConvertWithSdk(OptimizerContext ctx, List<Component> constraints)
        {
            MethodInfo method = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("VRC")) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                method = types
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .FirstOrDefault(m => m.Name == "ConvertUnityConstraintsToVrChatConstraints");
                if (method != null) break;
            }
            if (method == null) return false;

            var args = new List<object>();
            foreach (var p in method.GetParameters())
            {
                var pt = p.ParameterType;
                if (pt.IsArray && (pt.GetElementType() == typeof(IConstraint) || pt.GetElementType().IsAssignableFrom(typeof(Component))))
                {
                    var arr = Array.CreateInstance(pt.GetElementType(), constraints.Count);
                    for (int i = 0; i < constraints.Count; i++) arr.SetValue(constraints[i], i);
                    args.Add(arr);
                }
                else if (pt.IsAssignableFrom(typeof(VRCAvatarDescriptor))) args.Add(ctx.Descriptor);
                // 元のアニメーションクリップを書き換えないよう、アニメーション変換は行わない
                else if (pt == typeof(bool)) args.Add(false);
                else return false;
            }
            try
            {
                method.Invoke(null, args.ToArray());
                return true;
            }
            catch (Exception e)
            {
                ctx.Report.Warn($"Unity Constraint の変換に失敗しました: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        private static void RemoveDisallowedComponents(OptimizerContext ctx)
        {
            var targets = ctx.Root.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && IsDisallowed(c))
                .OrderBy(DestroyOrder)
                .ToList();
            foreach (var c in targets)
            {
                if (c == null) continue;
                var label = $"{PathUtil.GetPath(c.transform, ctx.RootTransform)} ({c.GetType().Name})";
                Object.DestroyImmediate(c);
                if (c == null) ctx.Report.AddRemoved("Mobile 非対応コンポーネント", label);
                else ctx.Report.Warn($"{label} を削除できませんでした");
            }
        }

        private static bool IsDisallowed(Component c)
        {
            if (c is Transform || c is Animator || c is SkinnedMeshRenderer || c is MeshRenderer || c is MeshFilter) return false;
            if (c is Cloth || c is Light || c is AudioSource || c is AudioListener || c is AudioReverbZone
                || c is AudioLowPassFilter || c is AudioHighPassFilter || c is AudioEchoFilter
                || c is AudioDistortionFilter || c is AudioReverbFilter || c is AudioChorusFilter
                || c is ParticleSystem || c is ParticleSystemRenderer || c is ParticleSystemForceField
                || c is TrailRenderer || c is LineRenderer || c is Camera || c is Rigidbody || c is Joint
                || c is Collider || c is Projector || c is ReflectionProbe || c is LightProbeGroup || c is LensFlare)
                return true;
            if (c.GetType().Name == "FlareLayer") return true;
            if (c is MonoBehaviour)
            {
                var ns = c.GetType().Namespace ?? "";
                return !ns.StartsWith("VRC");
            }
            return false;
        }

        private static int DestroyOrder(Component c)
        {
            if (c is MonoBehaviour || c is Joint || c is ParticleSystem || c is Cloth) return 0;
            if (c is ParticleSystemRenderer || c is Rigidbody) return 2;
            return 1;
        }
    }
}
