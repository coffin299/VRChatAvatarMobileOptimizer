using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Coffi.MobileOptimizer
{
    /// <summary>残った (髪の) PhysBone を目標ランクの上限内に収める。</summary>
    internal static class PhysBoneTrimmer
    {
        private static readonly HashSet<string> SignatureSkip = new HashSet<string>
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
            "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier",
            "rootTransform", "ignoreTransforms", "colliders", "multiChildType", "foldout_transforms", "foldout_forces",
            "foldout_collision", "foldout_stretchsquish", "foldout_limits", "foldout_grabpose", "foldout_options", "foldout_gizmos",
        };

        public static void Run(OptimizerContext ctx)
        {
            var rank = ctx.Settings.targetRank;
            var maxComponents = (int)MobileLimits.Get(StatCategory.PhysBoneComponents, rank);
            var maxTransforms = (int)MobileLimits.Get(StatCategory.PhysBoneTransforms, rank);
            var maxColliders = (int)MobileLimits.Get(StatCategory.PhysBoneColliders, rank);
            var maxChecks = (int)MobileLimits.Get(StatCategory.PhysBoneCollisionChecks, rank);

            var pbs = Alive(ctx);
            if (pbs.Count > maxComponents) MergeSiblings(ctx, pbs, maxComponents);

            pbs = Alive(ctx);
            if (pbs.Count > maxComponents)
            {
                foreach (var pb in pbs.OrderBy(pb => PhysBoneUtil.GetAffectedTransforms(pb).Count).Take(pbs.Count - maxComponents).ToList())
                {
                    ctx.Report.AddRemoved("PhysBone (上限超過)", PhysBoneUtil.GetKey(pb, ctx.RootTransform));
                    Object.DestroyImmediate(pb);
                }
            }

            TrimTransforms(ctx, maxTransforms);
            TrimColliders(ctx, maxColliders, maxChecks);
            ComponentStripper.RemoveUnusedColliders(ctx);
        }

        private static List<VRCPhysBoneBase> Alive(OptimizerContext ctx)
        {
            return ctx.Root.GetComponentsInChildren<VRCPhysBoneBase>(true).ToList();
        }

        /// <summary>同じ親を持ち設定が同じ PhysBone を、親をルートにした 1 コンポーネントへまとめる。</summary>
        private static void MergeSiblings(OptimizerContext ctx, List<VRCPhysBoneBase> pbs, int maxComponents)
        {
            int count = pbs.Count;
            var groups = pbs
                .Where(pb => PhysBoneUtil.GetRoot(pb).parent != null && PhysBoneUtil.GetRoot(pb).parent != ctx.RootTransform)
                .GroupBy(pb => (PhysBoneUtil.GetRoot(pb).parent, Signature(pb)))
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in groups)
            {
                if (count <= maxComponents) break;
                var parent = group.Key.parent;
                var members = group.ToList();
                var others = pbs.Where(pb => pb != null && !members.Contains(pb));
                if (others.Any(o => PhysBoneUtil.GetAffectedTransforms(o).Contains(parent))) continue;

                var roots = new HashSet<Transform>(members.Select(pb => PhysBoneUtil.GetRoot(pb)));
                var ignores = new HashSet<Transform>(members
                    .SelectMany(pb => pb.ignoreTransforms ?? new List<Transform>())
                    .Where(t => t != null));
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (!roots.Contains(child)) ignores.Add(child);
                }
                var colliders = members
                    .SelectMany(pb => pb.colliders ?? new List<VRCPhysBoneColliderBase>())
                    .Where(c => c != null).Distinct().ToList();

                var keep = members[0];
                keep.rootTransform = parent;
                keep.ignoreTransforms = ignores.ToList();
                keep.colliders = colliders;
                var so = new SerializedObject(keep);
                var multiChild = so.FindProperty("multiChildType");
                if (multiChild != null) multiChild.enumValueIndex = 0; // Ignore: 親自体は動かさない
                so.ApplyModifiedPropertiesWithoutUndo();

                foreach (var pb in members.Skip(1))
                {
                    ctx.Report.AddRemoved("PhysBone (統合)", $"{PhysBoneUtil.GetKey(pb, ctx.RootTransform)} → {parent.name}");
                    Object.DestroyImmediate(pb);
                }
                count -= members.Count - 1;
                ctx.Report.Info($"PhysBone {members.Count} 個を '{parent.name}' をルートに統合しました (根元も揺れるようになります)");
            }
        }

        private static string Signature(VRCPhysBoneBase pb)
        {
            var sb = new StringBuilder();
            var so = new SerializedObject(pb);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                var top = it.propertyPath.Split('.')[0];
                if (SignatureSkip.Contains(top))
                {
                    enter = false;
                    continue;
                }
                enter = it.hasChildren && it.propertyType != SerializedPropertyType.String;
                sb.Append(it.propertyPath).Append('=');
                switch (it.propertyType)
                {
                    case SerializedPropertyType.Integer: sb.Append(it.longValue); break;
                    case SerializedPropertyType.Boolean: sb.Append(it.boolValue); break;
                    case SerializedPropertyType.Float: sb.Append(it.floatValue.ToString("R")); break;
                    case SerializedPropertyType.String: sb.Append(it.stringValue); break;
                    case SerializedPropertyType.Enum: sb.Append(it.enumValueIndex); break;
                    case SerializedPropertyType.Vector3: sb.Append(it.vector3Value.ToString("R")); break;
                    case SerializedPropertyType.ObjectReference: sb.Append(it.objectReferenceInstanceIDValue); break;
                    case SerializedPropertyType.AnimationCurve:
                        var curve = it.animationCurveValue;
                        if (curve != null)
                            foreach (var k in curve.keys) sb.Append(k.time).Append(':').Append(k.value).Append(',');
                        break;
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Transform 数が上限を超える間、最も大きい PhysBone の末端から ignoreTransforms に追加する。</summary>
        private static void TrimTransforms(OptimizerContext ctx, int maxTransforms)
        {
            for (int guard = 0; guard < 1000; guard++)
            {
                var infos = Alive(ctx).Select(pb => (pb, affected: PhysBoneUtil.GetAffectedTransforms(pb))).ToList();
                int total = infos.Sum(x => x.affected.Count);
                if (total <= maxTransforms || infos.Count == 0) return;

                var (target, affected) = infos.OrderByDescending(x => x.affected.Count).First();
                var root = PhysBoneUtil.GetRoot(target);
                if (affected.Count <= 1)
                {
                    ctx.Report.AddRemoved("PhysBone (上限超過)", PhysBoneUtil.GetKey(target, ctx.RootTransform));
                    Object.DestroyImmediate(target);
                    continue;
                }

                int maxDepth = affected.Max(t => Depth(t, root));
                var leaves = affected
                    .Where(t => t != root && Depth(t, root) == maxDepth)
                    .Take(total - maxTransforms)
                    .ToList();
                if (target.ignoreTransforms == null) target.ignoreTransforms = new List<Transform>();
                target.ignoreTransforms.AddRange(leaves);
                ctx.Report.AddRemoved("PhysBone 末端 Transform", $"{target.name}: {leaves.Count} 個");
            }
        }

        private static int Depth(Transform t, Transform root)
        {
            int d = 0;
            for (var c = t; c != null && c != root; c = c.parent) d++;
            return d;
        }

        private static void TrimColliders(OptimizerContext ctx, int maxColliders, int maxChecks)
        {
            for (int guard = 0; guard < 1000; guard++)
            {
                var pbs = Alive(ctx);
                int checks = pbs.Sum(pb => PhysBoneUtil.CollisionChecks(pb));
                int colliders = pbs.SelectMany(pb => pb.colliders ?? new List<VRCPhysBoneColliderBase>()).Where(c => c != null).Distinct().Count();
                if (checks <= maxChecks && colliders <= maxColliders) return;

                var target = pbs.Where(pb => PhysBoneUtil.ColliderCount(pb) > 0)
                    .OrderByDescending(pb => PhysBoneUtil.CollisionChecks(pb))
                    .FirstOrDefault();
                if (target == null) return;
                target.colliders.RemoveAll(c => c == null);
                var removed = target.colliders[target.colliders.Count - 1];
                target.colliders.RemoveAt(target.colliders.Count - 1);
                ctx.Report.AddRemoved("PhysBone の Collider 参照", $"{target.name} → {removed.name}");
            }
        }
    }
}
