using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    public enum ToggleMode
    {
        /// <summary>トグルを無視して統合する (常に表示される)</summary>
        MergeAlwaysVisible,
        /// <summary>統合から除外して別メッシュとして残す</summary>
        KeepSeparate,
        /// <summary>トグル対象ごと削除する</summary>
        Delete,
    }

    public enum OutputMode
    {
        NewFolder,
        Overwrite,
    }

    [Serializable]
    public class PhysBoneOverride
    {
        public string key;
        public bool keep;
    }

    /// <summary>アバターごとの最適化設定。アバター名をキーに保存し、再実行時に再利用する。</summary>
    public class OptimizerSettings : ScriptableObject
    {
        public const string SettingsFolder = "Assets/MobileOptimized/Settings";

        public string avatarName;
        public PerfRank targetRank = PerfRank.Medium;
        public OutputMode outputMode = OutputMode.NewFolder;

        [Header("Steps")]
        public bool stripComponents = true;
        public bool trimPhysBones = true;
        public bool removeHiddenMesh = true;
        public bool optimizeBlendShapes = true;
        public bool mergeBones = true;
        public bool combineMeshes = true;
        public bool atlasTextures = true;
        public bool stripVertexAttributes = true;
        public bool decimate = false;

        [Header("PhysBone")]
        public List<string> hairNamePatterns = new List<string>
        {
            "hair", "髪", "kami", "bang", "ponytail", "twintail", "ahoge", "アホ毛", "もみあげ", "momiage",
        };
        public List<PhysBoneOverride> physBoneOverrides = new List<PhysBoneOverride>();
        public bool keepContacts = false;

        [Header("Hidden Mesh")]
        public string bodyRendererPath = "";
        /// <summary>素体を隠す服のレンダラーパス。空なら素体以外のすべてのメッシュを服として扱う。</summary>
        public List<string> clothingRendererPaths = new List<string>();
        public float hiddenDistance = 0.03f;
        public float hiddenRayAngle = 25f;
        public List<HumanBodyBones> hiddenProtectedBones = new List<HumanBodyBones> { HumanBodyBones.Head };

        [Header("Toggle / Mesh")]
        public bool deleteInactiveObjects = true;
        public ToggleMode toggleMode = ToggleMode.MergeAlwaysVisible;
        public string mergedMeshName = "Body";

        [Header("Atlas")]
        public int atlasSize = 1024;
        public float faceTextureBoost = 2f;
        public bool convertShaders = true;

        [Header("Decimate")]
        public int decimateTargetTriangles = 0;
        public List<HumanBodyBones> decimateProtectedBones = new List<HumanBodyBones>
        {
            HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        };

        public bool? GetOverride(string key)
        {
            foreach (var o in physBoneOverrides)
                if (o.key == key) return o.keep;
            return null;
        }

        public void SetOverride(string key, bool? keep)
        {
            physBoneOverrides.RemoveAll(o => o.key == key);
            if (keep.HasValue) physBoneOverrides.Add(new PhysBoneOverride { key = key, keep = keep.Value });
        }

        public int EffectiveDecimateTarget()
        {
            if (decimateTargetTriangles > 0) return decimateTargetTriangles;
            return Mathf.FloorToInt(MobileLimits.Get(StatCategory.Triangles, targetRank) * 0.98f);
        }

        public static OptimizerSettings LoadOrCreate(string avatarName)
        {
            var path = $"{SettingsFolder}/{PathUtil.SanitizeFileName(avatarName)}.asset";
            var settings = AssetDatabase.LoadAssetAtPath<OptimizerSettings>(path);
            if (settings != null) return settings;
            AssetSaver.EnsureFolder(SettingsFolder);
            settings = CreateInstance<OptimizerSettings>();
            settings.avatarName = avatarName;
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
