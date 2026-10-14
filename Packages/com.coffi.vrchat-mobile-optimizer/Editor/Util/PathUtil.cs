using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    internal static class PathUtil
    {
        /// <summary>root からの相対パス。root 自身は ""、root 配下でなければ null。</summary>
        /// <summary>アバタールート自体が非アクティブでも、ルート以下だけでアクティブかを判定する。</summary>
        public static bool IsActiveUnder(Transform t, Transform root)
        {
            for (var c = t; c != null && c != root; c = c.parent)
                if (!c.gameObject.activeSelf) return false;
            return true;
        }

        public static string GetPath(Transform t, Transform root)
        {
            if (t == null) return null;
            if (t == root) return "";
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            if (cur == null) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static Transform Find(Transform root, string path)
        {
            return string.IsNullOrEmpty(path) ? root : root.Find(path);
        }

        public static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            for (var c = t; c != null; c = c.parent)
                if (c == ancestor) return true;
            return false;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('/', '_').Replace('\\', '_');
        }
    }
}
