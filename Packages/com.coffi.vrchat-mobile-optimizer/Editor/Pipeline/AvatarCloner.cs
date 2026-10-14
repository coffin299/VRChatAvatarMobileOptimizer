using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace Coffi.MobileOptimizer
{
    /// <summary>元アバターを複製し、NDMF (Modular Avatar など) を適用して通常の階層にする。</summary>
    internal static class AvatarCloner
    {
        public static GameObject Clone(GameObject original, OptimizerReport report, out Dictionary<Component, string> physBoneKeys)
        {
            var clone = Object.Instantiate(original, original.transform.parent);
            clone.name = original.name + "_Mobile";
            clone.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
            clone.SetActive(true);

            // 元アバター側のキーを、構造が同一のうちに対応付けておく
            physBoneKeys = new Dictionary<Component, string>();
            var originalPbs = original.GetComponentsInChildren<VRCPhysBoneBase>(true);
            var clonePbs = clone.GetComponentsInChildren<VRCPhysBoneBase>(true);
            if (originalPbs.Length == clonePbs.Length)
                for (int i = 0; i < clonePbs.Length; i++)
                    physBoneKeys[clonePbs[i]] = PhysBoneUtil.GetKey(originalPbs[i], original.transform);

            try
            {
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Warn($"NDMF の適用中にエラーが発生しました: {e.Message}");
            }

            CopyBlueprintId(original, clone);
            return clone;
        }

        /// <summary>Android 版として同じアバターにアップロードできるよう Blueprint ID を引き継ぐ。</summary>
        private static void CopyBlueprintId(GameObject original, GameObject clone)
        {
            var type = TypeUtil.FindType("VRC.Core.PipelineManager");
            if (type == null) return;
            var src = original.GetComponent(type);
            var dst = clone.GetComponent(type);
            if (src == null || dst == null) return;
            var srcProp = new SerializedObject(src).FindProperty("blueprintId");
            var dstSo = new SerializedObject(dst);
            var dstProp = dstSo.FindProperty("blueprintId");
            if (srcProp == null || dstProp == null) return;
            dstProp.stringValue = srcProp.stringValue;
            dstSo.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
