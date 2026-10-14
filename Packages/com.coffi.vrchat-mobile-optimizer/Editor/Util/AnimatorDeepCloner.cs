using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// AnimatorController を、ステートマシン・ステート・遷移・BlendTree・AnimationClip まで丸ごと複製する。
    /// 元のアセットは一切変更しない。
    /// </summary>
    internal class AnimatorDeepCloner
    {
        private readonly Dictionary<Object, Object> _map = new Dictionary<Object, Object>();
        private readonly List<Object> _created = new List<Object>();
        private readonly Dictionary<Object, Object> _owner = new Dictionary<Object, Object>();
        private readonly Queue<Object> _pending = new Queue<Object>();

        public IEnumerable<AnimationClip> ClonedClips => _created.OfType<AnimationClip>();

        public RuntimeAnimatorController CloneController(RuntimeAnimatorController controller)
        {
            if (controller == null) return null;
            var clone = (RuntimeAnimatorController)CloneObject(controller, null);
            while (_pending.Count > 0) RewriteReferences(_pending.Dequeue());
            return clone;
        }

        // Large FX layers form deep state/transition graphs, so cloning must not recurse.
        private Object CloneObject(Object obj, Object owner)
        {
            if (obj == null) return null;
            if (_map.TryGetValue(obj, out var existing)) return existing;
            if (!IsCloneTarget(obj)) return obj;

            var clone = Object.Instantiate(obj);
            clone.name = obj.name;
            _map[obj] = clone;
            _map[clone] = clone;
            _created.Add(clone);
            _owner[clone] = clone is RuntimeAnimatorController ? clone : owner;
            _pending.Enqueue(clone);
            return clone;
        }

        private void RewriteReferences(Object clone)
        {
            _owner.TryGetValue(clone, out var owner);
            var so = new SerializedObject(clone);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = it.hasChildren && it.propertyType != SerializedPropertyType.String;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var r = it.objectReferenceValue;
                if (r == null || !IsCloneTarget(r)) continue;
                var replaced = CloneObject(r, owner);
                if (replaced != r) it.objectReferenceValue = replaced;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool IsCloneTarget(Object o)
        {
            return o is AnimatorController
                   || o is AnimatorOverrideController
                   || o is AnimatorStateMachine
                   || o is AnimatorState
                   || o is AnimatorTransitionBase
                   || o is StateMachineBehaviour
                   || o is BlendTree
                   || o is AnimationClip;
        }

        /// <summary>複製したコントローラを個別ファイルに、それ以外をそのサブアセットとして保存する。</summary>
        public void SaveAll(AssetSaver saver, string prefix)
        {
            Object fallbackMain = null;
            foreach (var obj in _created)
            {
                if (obj is AnimatorController ac)
                {
                    saver.Save(ac, $"{prefix}_{ac.name}", "controller");
                    fallbackMain = fallbackMain ?? ac;
                }
                else if (obj is AnimatorOverrideController aoc)
                {
                    saver.Save(aoc, $"{prefix}_{aoc.name}", "overrideController");
                    fallbackMain = fallbackMain ?? aoc;
                }
            }

            foreach (var obj in _created)
            {
                if (obj is RuntimeAnimatorController) continue;
                _owner.TryGetValue(obj, out var owner);
                var main = owner != null && AssetDatabase.Contains(owner) ? owner : fallbackMain;
                if (main == null)
                {
                    saver.Save(obj, $"{prefix}_{obj.name}", obj is AnimationClip ? "anim" : "asset");
                    fallbackMain = obj;
                    continue;
                }
                if (!(obj is AnimationClip)) obj.hideFlags = HideFlags.HideInHierarchy;
                saver.AddSubAsset(obj, main);
            }
        }
    }
}
