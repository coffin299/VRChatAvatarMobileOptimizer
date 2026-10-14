using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Coffi.MobileOptimizer
{
    public class AvatarStats
    {
        public readonly Dictionary<StatCategory, float> Values = new Dictionary<StatCategory, float>();
        public readonly List<string> Notes = new List<string>();
        public PerfRank Overall;
        public string SdkOverall;
        public readonly List<KeyValuePair<string, string>> SdkCategories = new List<KeyValuePair<string, string>>();

        public PerfRank RankOf(StatCategory c) => MobileLimits.Rate(c, Values.TryGetValue(c, out var v) ? v : 0);

        public void ComputeOverall(bool forceVeryPoor = false)
        {
            Overall = forceVeryPoor ? PerfRank.VeryPoor : PerfRank.Excellent;
            foreach (StatCategory c in Enum.GetValues(typeof(StatCategory)))
            {
                var r = RankOf(c);
                if (r > Overall) Overall = r;
            }
        }
    }

    /// <summary>Mobile 予想ランクの計算。SDK 本体の計算が呼べれば併記し、項目別は自前で計算する。</summary>
    public static class MobileRankCalculator
    {
        public static AvatarStats Calculate(GameObject root, bool includeSdk = true)
        {
            var stats = new AvatarStats();
            var v = stats.Values;
            bool nonReadable = false;

            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            int tris = 0;
            foreach (var smr in smrs)
            {
                tris += CountTriangles(smr.sharedMesh);
                if (smr.sharedMesh != null && !smr.sharedMesh.isReadable) nonReadable = true;
            }
            foreach (var mr in mrs)
            {
                var mesh = GetMesh(mr);
                tris += CountTriangles(mesh);
                if (mesh != null && !mesh.isReadable) nonReadable = true;
            }
            v[StatCategory.Triangles] = tris;
            v[StatCategory.SkinnedMeshes] = smrs.Length;
            v[StatCategory.BasicMeshes] = mrs.Length;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            v[StatCategory.MaterialSlots] = renderers
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                .Sum(r => r.sharedMaterials.Length);
            v[StatCategory.TextureMemoryMB] = TextureMemoryMB(CollectTextures(renderers.SelectMany(r => r.sharedMaterials)));
            v[StatCategory.Animators] = root.GetComponentsInChildren<Animator>(true).Length;
            v[StatCategory.Bones] = smrs.SelectMany(s => s.bones).Where(b => b != null).Distinct().Count();

            var pbs = root.GetComponentsInChildren<VRCPhysBoneBase>(true);
            v[StatCategory.PhysBoneComponents] = pbs.Length;
            v[StatCategory.PhysBoneTransforms] = pbs.Sum(pb => PhysBoneUtil.GetAffectedTransforms(pb).Count);
            v[StatCategory.PhysBoneColliders] = root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true).Length;
            v[StatCategory.PhysBoneCollisionChecks] = pbs.Sum(pb => PhysBoneUtil.CollisionChecks(pb));

            var components = root.GetComponentsInChildren<Component>(true).Where(c => c != null).ToList();
            v[StatCategory.Contacts] = components.Count(c => TypeUtil.IsContact(c.GetType()));
            v[StatCategory.Constraints] = components.Count(c => TypeUtil.IsConstraint(c.GetType()));
            v[StatCategory.ParticleSystems] = root.GetComponentsInChildren<ParticleSystem>(true).Length;
            v[StatCategory.TrailRenderers] = root.GetComponentsInChildren<TrailRenderer>(true).Length;
            v[StatCategory.LineRenderers] = root.GetComponentsInChildren<LineRenderer>(true).Length;

            int removedOnMobile = components.Count(c => c is Light || c is Cloth || c is AudioSource || c is Collider || c is Rigidbody);
            if (removedOnMobile > 0)
                stats.Notes.Add($"Mobile では無効になるコンポーネント (Light / Cloth / AudioSource / Collider / Rigidbody) が {removedOnMobile} 個あります");
            if (nonReadable)
                stats.Notes.Add("Read/Write が無効なメッシュがあるため、Very Poor 扱いになります");

            stats.ComputeOverall(nonReadable);
            if (includeSdk) TryCalculateWithSdk(root, stats);
            return stats;
        }

        public static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        public static int CountTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) count += mesh.GetIndexCount(i) / 3;
            return (int)count;
        }

        public static IEnumerable<Texture> CollectTextures(IEnumerable<Material> materials)
        {
            var set = new HashSet<Texture>();
            foreach (var m in materials)
            {
                if (m == null) continue;
                foreach (var name in m.GetTexturePropertyNames())
                {
                    var t = m.GetTexture(name);
                    if (t != null) set.Add(t);
                }
            }
            return set;
        }

        public static float TextureMemoryMB(IEnumerable<Texture> textures)
        {
            long bytes = 0;
            foreach (var t in textures) bytes += EstimateMobileBytes(t);
            return bytes / (1024f * 1024f);
        }

        /// <summary>Android ビルド時のテクスチャメモリ見積もり。</summary>
        public static long EstimateMobileBytes(Texture tex)
        {
            if (tex == null) return 0;
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                GetSourceSize(importer, tex, out int w, out int h);
                var android = importer.GetPlatformTextureSettings("Android");
                int max = android.overridden ? android.maxTextureSize : importer.maxTextureSize;
                float scale = Mathf.Min(1f, (float)max / Mathf.Max(w, h, 1));
                w = Mathf.Max(1, Mathf.RoundToInt(w * scale));
                h = Mathf.Max(1, Mathf.RoundToInt(h * scale));
                float bpp = android.overridden ? BitsPerPixel(android.format) : 3.56f;
                float mip = importer.mipmapEnabled ? 4f / 3f : 1f;
                return (long)(w * (double)h * bpp / 8 * mip);
            }
            if (tex is Texture2D) return Profiler.GetRuntimeMemorySizeLong(tex);
            return (long)tex.width * tex.height * 4;
        }

        private static void GetSourceSize(TextureImporter importer, Texture tex, out int width, out int height)
        {
            width = tex.width;
            height = tex.height;
            var method = typeof(TextureImporter).GetMethod("GetSourceTextureWidthAndHeight",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }, null);
            if (method == null) return;
            var args = new object[] { 0, 0 };
            method.Invoke(importer, args);
            width = (int)args[0];
            height = (int)args[1];
        }

        public static long EstimateAtlasBytes(int size)
        {
            return (long)(size * (double)size * BitsPerPixel(TextureImporterFormat.ASTC_6x6) / 8 * 4 / 3);
        }

        public static float BitsPerPixel(TextureImporterFormat format)
        {
            var name = format.ToString();
            if (name.StartsWith("ASTC_"))
            {
                var dims = name.Split('_').Last().Split('x');
                if (dims.Length == 2 && int.TryParse(dims[0], out int a) && int.TryParse(dims[1], out int b))
                    return 128f / (a * b);
            }
            switch (format)
            {
                case TextureImporterFormat.Automatic: return 3.56f;
                case TextureImporterFormat.RGBA32:
                case TextureImporterFormat.ARGB32:
                    return 32;
                case TextureImporterFormat.RGB24: return 24;
                case TextureImporterFormat.RGBA16:
                case TextureImporterFormat.RGB16:
                    return 16;
                case TextureImporterFormat.Alpha8:
                case TextureImporterFormat.R8:
                case TextureImporterFormat.DXT5:
                case TextureImporterFormat.BC7:
                case TextureImporterFormat.BC5:
                case TextureImporterFormat.ETC2_RGBA8:
                    return 8;
                case TextureImporterFormat.DXT1:
                case TextureImporterFormat.BC4:
                case TextureImporterFormat.ETC_RGB4:
                case TextureImporterFormat.ETC2_RGB4:
                case TextureImporterFormat.ETC2_RGB4_PUNCHTHROUGH_ALPHA:
                    return 4;
                default: return 8;
            }
        }

        /// <summary>VRChat SDK の AvatarPerformance を (内部 API のためリフレクションで) 呼び出す。</summary>
        private static void TryCalculateWithSdk(GameObject root, AvatarStats stats)
        {
            try
            {
                var statsType = TypeUtil.FindType("VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats");
                var perfType = TypeUtil.FindType("VRC.SDKBase.Validation.Performance.AvatarPerformance");
                var catType = TypeUtil.FindType("VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory");
                if (statsType == null || perfType == null || catType == null) return;

                var ctor = statsType.GetConstructor(new[] { typeof(bool) });
                if (ctor == null) return;
                var perfStats = ctor.Invoke(new object[] { true });

                var calc = perfType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CalculatePerformanceStats" && m.GetParameters().Length == 4);
                if (calc == null) return;
                calc.Invoke(null, new object[] { root.name, root, perfStats, true });

                var getRating = statsType.GetMethod("GetPerformanceRatingForCategory");
                if (getRating == null) return;
                foreach (var cat in Enum.GetValues(catType))
                {
                    var name = cat.ToString();
                    if (name == "None" || name == "AvatarPerformanceCategoryCount") continue;
                    var rating = getRating.Invoke(perfStats, new[] { cat })?.ToString();
                    if (string.IsNullOrEmpty(rating) || rating == "None") continue;
                    if (name == "Overall") stats.SdkOverall = rating;
                    else stats.SdkCategories.Add(new KeyValuePair<string, string>(name, rating));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MobileOptimizer] SDK のパフォーマンス計算を呼び出せませんでした: {e.Message}");
            }
        }

        /// <summary>実行前の見込み。元アバターと設定から、処理後のおおよその数値を推定する。</summary>
        public static AvatarStats Estimate(GameObject original, OptimizerSettings s)
        {
            var current = Calculate(original, false);
            var est = new AvatarStats();
            foreach (var kv in current.Values) est.Values[kv.Key] = kv.Value;
            var v = est.Values;
            var root = original.transform;
            var anim = AnimationAnalyzer.Analyze(original);

            // PhysBone (髪のみ残す)
            var pbs = original.GetComponentsInChildren<VRCPhysBoneBase>(true);
            var kept = s.stripComponents
                ? pbs.Where(pb => s.GetOverride(PhysBoneUtil.GetKey(pb, root)) ?? PhysBoneUtil.IsHairCandidate(pb, root, s.hairNamePatterns)).ToList()
                : pbs.ToList();
            float pbComp = kept.Count;
            float pbTrans = kept.Sum(pb => PhysBoneUtil.GetAffectedTransforms(pb).Count);
            float colliders = kept.SelectMany(pb => pb.colliders ?? new List<VRCPhysBoneColliderBase>()).Where(c => c != null).Distinct().Count();
            float checks = kept.Sum(pb => PhysBoneUtil.CollisionChecks(pb));
            if (s.trimPhysBones)
            {
                pbComp = Mathf.Min(pbComp, MobileLimits.Get(StatCategory.PhysBoneComponents, s.targetRank));
                pbTrans = Mathf.Min(pbTrans, MobileLimits.Get(StatCategory.PhysBoneTransforms, s.targetRank));
                colliders = Mathf.Min(colliders, MobileLimits.Get(StatCategory.PhysBoneColliders, s.targetRank));
                checks = Mathf.Min(checks, MobileLimits.Get(StatCategory.PhysBoneCollisionChecks, s.targetRank));
            }
            v[StatCategory.PhysBoneComponents] = pbComp;
            v[StatCategory.PhysBoneTransforms] = pbTrans;
            v[StatCategory.PhysBoneColliders] = colliders;
            v[StatCategory.PhysBoneCollisionChecks] = checks;

            // 残るレンダラー
            var renderers = original.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            var remaining = new List<Renderer>();
            int separate = 0;
            foreach (var r in renderers)
            {
                bool visible = r.gameObject.activeInHierarchy && r.enabled;
                bool toggle = anim.IsToggled(r.transform, root);
                if (!visible && !toggle) continue;
                if (toggle && s.toggleMode == ToggleMode.Delete) continue;
                if (!visible && s.deleteInactiveObjects && s.toggleMode != ToggleMode.KeepSeparate) continue;
                if (toggle && (s.toggleMode == ToggleMode.KeepSeparate || (!visible && !s.deleteInactiveObjects))) separate++;
                remaining.Add(r);
            }
            int tris = remaining.Sum(r => CountTriangles(GetMesh(r)));
            if (s.decimate) tris = Mathf.Min(tris, s.EffectiveDecimateTarget());
            v[StatCategory.Triangles] = tris;

            int meshCount = s.combineMeshes ? Mathf.Min(remaining.Count, 1 + separate) : remaining.Count;
            v[StatCategory.SkinnedMeshes] = meshCount;
            v[StatCategory.BasicMeshes] = 0;
            v[StatCategory.MaterialSlots] = s.atlasTextures
                ? meshCount
                : remaining.SelectMany(r => r.sharedMaterials).Distinct().Count();
            v[StatCategory.TextureMemoryMB] = s.atlasTextures
                ? EstimateAtlasBytes(s.atlasSize) / (1024f * 1024f)
                : TextureMemoryMB(CollectTextures(remaining.SelectMany(r => r.sharedMaterials)));

            if (s.mergeBones)
            {
                var animator = original.GetComponent<Animator>();
                int humanoid = 0;
                if (animator != null && animator.isHuman)
                    for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                        if (animator.GetBoneTransform(b) != null) humanoid++;
                v[StatCategory.Bones] = Mathf.Min(current.Values[StatCategory.Bones], humanoid + pbTrans);
            }

            if (s.stripComponents)
            {
                if (!s.keepContacts) v[StatCategory.Contacts] = 0;
                v[StatCategory.ParticleSystems] = 0;
                v[StatCategory.TrailRenderers] = 0;
                v[StatCategory.LineRenderers] = 0;
            }

            est.Notes.Add("見込み値は概算です (隠れポリゴン削除の効果は含みません)");
            est.ComputeOverall();
            return est;
        }
    }
}
