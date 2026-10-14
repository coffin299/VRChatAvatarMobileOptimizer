using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.Dynamics;

namespace Coffi.MobileOptimizer
{
    internal static class PhysBoneUtil
    {
        public static Transform GetRoot(VRCPhysBoneBase pb)
        {
            return pb.rootTransform != null ? pb.rootTransform : pb.transform;
        }

        /// <summary>PhysBone が動かす Transform (root を含み、ignoreTransforms の配下は除く)。</summary>
        public static List<Transform> GetAffectedTransforms(VRCPhysBoneBase pb)
        {
            var result = new List<Transform>();
            var ignore = new HashSet<Transform>((pb.ignoreTransforms ?? new List<Transform>()).Where(t => t != null));
            var stack = new Stack<Transform>();
            stack.Push(GetRoot(pb));
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t == null || ignore.Contains(t)) continue;
                result.Add(t);
                for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }
            return result;
        }

        public static int ColliderCount(VRCPhysBoneBase pb)
        {
            return pb.colliders == null ? 0 : pb.colliders.Count(c => c != null);
        }

        public static int CollisionChecks(VRCPhysBoneBase pb)
        {
            return GetAffectedTransforms(pb).Count * ColliderCount(pb);
        }

        /// <summary>ウィンドウの上書き設定に使うキー。コンポーネントのパス + 同じ GameObject 内での番号。</summary>
        public static string GetKey(VRCPhysBoneBase pb, Transform avatarRoot)
        {
            var path = PathUtil.GetPath(pb.transform, avatarRoot) ?? pb.name;
            var siblings = pb.GetComponents<VRCPhysBoneBase>();
            int index = System.Array.IndexOf(siblings, pb);
            return index > 0 ? $"{path}#{index}" : path;
        }

        public static bool IsHairCandidate(VRCPhysBoneBase pb, Transform avatarRoot, IList<string> patterns)
        {
            if (Matches(pb.gameObject.name, patterns)) return true;
            for (var t = GetRoot(pb); t != null && t != avatarRoot; t = t.parent)
                if (Matches(t.name, patterns)) return true;
            return false;
        }

        private static bool Matches(string name, IList<string> patterns)
        {
            var lower = name.ToLowerInvariant();
            foreach (var p in patterns)
                if (!string.IsNullOrEmpty(p) && lower.Contains(p.ToLowerInvariant())) return true;
            return false;
        }
    }
}
