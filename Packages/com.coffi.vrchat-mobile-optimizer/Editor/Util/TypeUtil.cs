using System;
using System.Collections.Generic;

namespace Coffi.MobileOptimizer
{
    internal static class TypeUtil
    {
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        public static Type FindType(string fullName)
        {
            if (Cache.TryGetValue(fullName, out var cached)) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = asm.GetType(fullName, false);
                if (found != null) break;
            }
            Cache[fullName] = found;
            return found;
        }

        /// <summary>型自身または基底型のいずれかの名前が baseName と一致するか。</summary>
        public static bool HasBaseNamed(Type type, string baseName)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.Name == baseName) return true;
            return false;
        }

        public static bool IsConstraint(Type type)
        {
            return typeof(UnityEngine.Animations.IConstraint).IsAssignableFrom(type) || HasBaseNamed(type, "VRCConstraintBase");
        }

        public static bool IsContact(Type type)
        {
            return HasBaseNamed(type, "ContactBase");
        }
    }
}
