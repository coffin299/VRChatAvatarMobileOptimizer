using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Coffi.MobileOptimizer
{
    /// <summary>1 つ以上の元レンダラーから作られた、編集中のメッシュ。</summary>
    internal class MeshUnit
    {
        public string Name;
        public MeshData Data;
        public bool KeepSeparate;
        public bool Toggleable;
        public readonly List<Renderer> SourceRenderers = new List<Renderer>();
        public readonly List<string> SourcePaths = new List<string>();
        public readonly List<int> SourceRendererIds = new List<int>();
    }

    internal class OptimizerContext
    {
        public GameObject Original;
        public GameObject Root;
        public Transform RootTransform => Root.transform;
        public VRCAvatarDescriptor Descriptor;
        public Animator Animator;
        public OptimizerSettings Settings;
        public OptimizerReport Report;
        public AssetSaver Assets;

        /// <summary>複製した PhysBone → 元アバター上でのキー (ウィンドウの上書き設定用)。</summary>
        public Dictionary<Component, string> PhysBoneKeys = new Dictionary<Component, string>();
        public AnimationInfo Anim;
        public readonly HashSet<Renderer> SeparateRenderers = new HashSet<Renderer>();
        public List<MeshUnit> Units = new List<MeshUnit>();

        /// <summary>統合で移動したレンダラーの旧パス → 新パス (BlendShape アニメーション用)。</summary>
        public readonly Dictionary<string, string> RendererPathRemap = new Dictionary<string, string>();
        /// <summary>旧レンダラーの InstanceID → 書き出し後の SkinnedMeshRenderer。</summary>
        public readonly Dictionary<int, SkinnedMeshRenderer> RendererReplacement = new Dictionary<int, SkinnedMeshRenderer>();

        public int VisemeRendererId;
        public int EyelidRendererId;
        public readonly List<string> EyelidBlendShapeNames = new List<string>();
        public readonly HashSet<string> VisemeBlendShapeNames = new HashSet<string>();

        public OptimizerContext(GameObject original, GameObject root, OptimizerSettings settings, OptimizerReport report, AssetSaver assets)
        {
            Original = original;
            Root = root;
            Settings = settings;
            Report = report;
            Assets = assets;
            Descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Animator = root.GetComponent<Animator>();
        }

        public Transform HumanBone(HumanBodyBones bone)
        {
            if (Animator == null || !Animator.isHuman || bone == HumanBodyBones.LastBone) return null;
            return Animator.GetBoneTransform(bone);
        }

        public HashSet<Transform> HumanoidBones()
        {
            var set = new HashSet<Transform>();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
            {
                var t = HumanBone(b);
                if (t != null) set.Add(t);
            }
            return set;
        }

        public IEnumerable<Transform> HumanBones(IEnumerable<HumanBodyBones> bones)
        {
            foreach (var b in bones)
            {
                var t = HumanBone(b);
                if (t != null) yield return t;
            }
        }

        /// <summary>Viseme / まばたきで使う BlendShape 名を記録しておく (統合後のインデックス再設定用)。</summary>
        public void CaptureDescriptorInfo()
        {
            if (Descriptor == null) return;
            var so = new SerializedObject(Descriptor);

            var viseme = so.FindProperty("VisemeSkinnedMesh")?.objectReferenceValue as SkinnedMeshRenderer;
            if (viseme != null)
            {
                VisemeRendererId = viseme.GetInstanceID();
                var names = so.FindProperty("VisemeBlendShapes");
                if (names != null)
                    for (int i = 0; i < names.arraySize; i++)
                        VisemeBlendShapeNames.Add(names.GetArrayElementAtIndex(i).stringValue);
                var mouth = so.FindProperty("MouthOpenBlendShapeName");
                if (mouth != null && !string.IsNullOrEmpty(mouth.stringValue)) VisemeBlendShapeNames.Add(mouth.stringValue);
            }

            var eyelid = so.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh")?.objectReferenceValue as SkinnedMeshRenderer;
            var indices = so.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
            if (eyelid != null && eyelid.sharedMesh != null && indices != null)
            {
                EyelidRendererId = eyelid.GetInstanceID();
                for (int i = 0; i < indices.arraySize; i++)
                {
                    int idx = indices.GetArrayElementAtIndex(i).intValue;
                    EyelidBlendShapeNames.Add(idx >= 0 && idx < eyelid.sharedMesh.blendShapeCount
                        ? eyelid.sharedMesh.GetBlendShapeName(idx)
                        : null);
                }
            }
        }
    }
}
