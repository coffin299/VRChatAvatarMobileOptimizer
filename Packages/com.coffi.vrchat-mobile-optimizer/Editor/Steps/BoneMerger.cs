using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// 動かないボーン (Humanoid でも PhysBone でもなく、アニメーション・Constraint などから参照もされない) のウェイトを
    /// 最も近い「残す」祖先ボーンへ移す。MA でマージされた服のボーンや、削除した PhysBone 配下のボーンが対象。
    /// 頂点はルート空間に正規化済みなので、インデックスの付け替えだけで見た目は変わらない。
    /// </summary>
    internal static class BoneMerger
    {
        public static void Run(OptimizerContext ctx)
        {
            var keep = ComputeKeepSet(ctx, false);
            var root = ctx.RootTransform;
            foreach (var unit in ctx.Units)
            {
                int before = unit.Data.Bones.Count;
                unit.Data.RemapBones(b => NearestKept(b, keep, root));
                int after = unit.Data.Bones.Count;
                if (after < before) ctx.Report.AddRemoved("ボーン (ウェイト統合)", $"{unit.Name}: {before} → {after}");
            }
        }

        private static Transform NearestKept(Transform bone, HashSet<Transform> keep, Transform root)
        {
            if (bone == null) return root;
            if (!PathUtil.IsDescendantOf(bone, root)) return bone;
            for (var c = bone; c != null; c = c.parent)
                if (c == root || keep.Contains(c)) return c;
            return root;
        }

        /// <summary>
        /// 残す必要のある Transform。includeRenderers=true のときは、書き出し後のレンダラーが使うボーンも含める。
        /// </summary>
        public static HashSet<Transform> ComputeKeepSet(OptimizerContext ctx, bool includeRenderers)
        {
            var root = ctx.RootTransform;
            var keep = new HashSet<Transform> { root };
            keep.UnionWith(ctx.HumanoidBones());

            foreach (var pb in ctx.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                keep.UnionWith(PhysBoneUtil.GetAffectedTransforms(pb));

            foreach (var path in ctx.Anim.TransformPaths)
            {
                var t = PathUtil.Find(root, path);
                if (t != null) keep.Add(t);
            }

            foreach (var c in ctx.Root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is MeshFilter || c is VRCPhysBoneBase) continue;
                if (c is Renderer r)
                {
                    if (!includeRenderers) continue;
                    keep.Add(r.transform);
                    if (r is SkinnedMeshRenderer smr)
                    {
                        keep.UnionWith(smr.bones.Where(b => b != null));
                        if (smr.rootBone != null) keep.Add(smr.rootBone);
                    }
                    if (r.probeAnchor != null) keep.Add(r.probeAnchor);
                    continue;
                }
                keep.Add(c.transform);
                CollectReferencedTransforms(c, keep);
            }
            return keep;
        }

        private static void CollectReferencedTransforms(Component c, HashSet<Transform> into)
        {
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = it.hasChildren && it.propertyType != SerializedPropertyType.String;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var r = it.objectReferenceValue;
                if (r is Transform t) into.Add(t);
                else if (r is GameObject go) into.Add(go.transform);
            }
        }
    }

    /// <summary>書き出し後、どこからも使われなくなった Transform (ボーン・空の GameObject) を削除する。</summary>
    internal static class TransformCleaner
    {
        public static void Run(OptimizerContext ctx)
        {
            var root = ctx.RootTransform;
            var needed = BoneMerger.ComputeKeepSet(ctx, true);
            foreach (var t in needed.ToList())
                for (var c = t != null ? t.parent : null; c != null && c != root; c = c.parent)
                    needed.Add(c);

            int removed = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (t == null || t == root || needed.Contains(t)) continue;
                if (t.parent != null && !needed.Contains(t.parent)) continue; // 親ごと消える
                removed += t.GetComponentsInChildren<Transform>(true).Length;
                Object.DestroyImmediate(t.gameObject);
            }
            if (removed > 0) ctx.Report.AddRemoved("Transform (未使用ボーン・空オブジェクト)", $"{removed} 個");
        }
    }
}
