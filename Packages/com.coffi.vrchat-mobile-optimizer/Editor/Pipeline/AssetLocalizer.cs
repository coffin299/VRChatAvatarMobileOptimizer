using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// 複製アバターが参照するアセットを出力フォルダへコピーする。
    /// マテリアル・メッシュは常にコピーし、NDMF の一時アセットやメモリ上だけのアセットもコピーして参照切れを防ぐ。
    /// </summary>
    internal static class AssetLocalizer
    {
        public static void Run(OptimizerContext ctx, AnimatorDeepCloner cloner)
        {
            var cache = new Dictionary<Object, Object>();
            foreach (var component in ctx.Root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                var so = new SerializedObject(component);
                if (LocalizeReferences(so, ctx, cache, cloner)) so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool LocalizeReferences(SerializedObject so, OptimizerContext ctx, Dictionary<Object, Object> cache, AnimatorDeepCloner cloner)
        {
            bool changed = false;
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = it.hasChildren && it.propertyType != SerializedPropertyType.String;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var r = it.objectReferenceValue;
                if (r == null) continue;
                var localized = Localize(r, ctx, cache, cloner);
                if (localized != r)
                {
                    it.objectReferenceValue = localized;
                    changed = true;
                }
            }
            return changed;
        }

        private static Object Localize(Object obj, OptimizerContext ctx, Dictionary<Object, Object> cache, AnimatorDeepCloner cloner)
        {
            if (obj is GameObject || obj is Component) return obj;
            if (ctx.Assets.IsInOutput(obj)) return obj;
            if (cache.TryGetValue(obj, out var cached)) return cached;

            bool mustCopy = obj is Material || obj is Mesh || AssetSaver.IsTemporary(obj);
            if (!mustCopy) return obj;

            Object copy;
            if (obj is RuntimeAnimatorController rac)
            {
                copy = cloner.CloneController(rac);
                cache[obj] = copy;
                return copy;
            }
            if (!(obj is Material || obj is Mesh || obj is Texture || obj is ScriptableObject || obj is AnimationClip || obj is AvatarMask))
                return obj;

            copy = Object.Instantiate(obj);
            copy.name = obj.name;
            cache[obj] = copy;
            var ext = obj is Material ? "mat" : obj is AnimationClip ? "anim" : obj is AvatarMask ? "mask" : "asset";
            ctx.Assets.Save(copy, obj.name, ext);

            // 中の参照 (メニューのサブメニュー、一時テクスチャなど) も同様に処理する
            var so = new SerializedObject(copy);
            if (LocalizeReferences(so, ctx, cache, cloner)) so.ApplyModifiedPropertiesWithoutUndo();
            return copy;
        }
    }
}
