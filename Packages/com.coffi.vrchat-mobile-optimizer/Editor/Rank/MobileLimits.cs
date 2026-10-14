using System.Collections.Generic;

namespace Coffi.MobileOptimizer
{
    public enum PerfRank
    {
        Excellent = 0,
        Good = 1,
        Medium = 2,
        Poor = 3,
        VeryPoor = 4,
    }

    public enum StatCategory
    {
        Triangles,
        TextureMemoryMB,
        SkinnedMeshes,
        BasicMeshes,
        MaterialSlots,
        Animators,
        Bones,
        PhysBoneComponents,
        PhysBoneTransforms,
        PhysBoneColliders,
        PhysBoneCollisionChecks,
        Contacts,
        Constraints,
        ParticleSystems,
        TrailRenderers,
        LineRenderers,
    }

    /// <summary>
    /// Mobile (Android / iOS / Quest) の各ランク上限値。
    /// https://creators.vrchat.com/avatars/avatar-performance-ranking-system/#mobile-limits
    /// </summary>
    public static class MobileLimits
    {
        // Excellent, Good, Medium, Poor の順。これを超えると次のランクになる。
        private static readonly Dictionary<StatCategory, float[]> Table = new Dictionary<StatCategory, float[]>
        {
            { StatCategory.Triangles, new float[] { 7500, 10000, 15000, 20000 } },
            { StatCategory.TextureMemoryMB, new float[] { 10, 18, 25, 40 } },
            { StatCategory.SkinnedMeshes, new float[] { 1, 1, 2, 2 } },
            { StatCategory.BasicMeshes, new float[] { 1, 1, 2, 2 } },
            { StatCategory.MaterialSlots, new float[] { 1, 1, 2, 4 } },
            { StatCategory.Animators, new float[] { 1, 1, 1, 2 } },
            { StatCategory.Bones, new float[] { 75, 90, 150, 150 } },
            { StatCategory.PhysBoneComponents, new float[] { 0, 4, 6, 8 } },
            { StatCategory.PhysBoneTransforms, new float[] { 0, 16, 32, 64 } },
            { StatCategory.PhysBoneColliders, new float[] { 0, 4, 8, 16 } },
            { StatCategory.PhysBoneCollisionChecks, new float[] { 0, 16, 32, 64 } },
            { StatCategory.Contacts, new float[] { 2, 4, 8, 16 } },
            { StatCategory.Constraints, new float[] { 30, 60, 120, 150 } },
            { StatCategory.ParticleSystems, new float[] { 0, 0, 0, 2 } },
            { StatCategory.TrailRenderers, new float[] { 0, 0, 0, 1 } },
            { StatCategory.LineRenderers, new float[] { 0, 0, 0, 1 } },
        };

        private static readonly Dictionary<StatCategory, string> Names = new Dictionary<StatCategory, string>
        {
            { StatCategory.Triangles, "ポリゴン数" },
            { StatCategory.TextureMemoryMB, "テクスチャメモリ (MB)" },
            { StatCategory.SkinnedMeshes, "Skinned Mesh" },
            { StatCategory.BasicMeshes, "Basic Mesh" },
            { StatCategory.MaterialSlots, "マテリアルスロット" },
            { StatCategory.Animators, "Animator" },
            { StatCategory.Bones, "ボーン" },
            { StatCategory.PhysBoneComponents, "PhysBone コンポーネント" },
            { StatCategory.PhysBoneTransforms, "PhysBone Transform" },
            { StatCategory.PhysBoneColliders, "PhysBone Collider" },
            { StatCategory.PhysBoneCollisionChecks, "PhysBone 衝突チェック" },
            { StatCategory.Contacts, "Contact" },
            { StatCategory.Constraints, "Constraint" },
            { StatCategory.ParticleSystems, "Particle System" },
            { StatCategory.TrailRenderers, "Trail Renderer" },
            { StatCategory.LineRenderers, "Line Renderer" },
        };

        /// <summary>超えると、すべての PhysBone / Contact / Constraint が無効化されるハード上限。</summary>
        public static readonly StatCategory[] HardLimitCategories =
        {
            StatCategory.PhysBoneComponents, StatCategory.PhysBoneTransforms, StatCategory.PhysBoneColliders,
            StatCategory.PhysBoneCollisionChecks, StatCategory.Contacts, StatCategory.Constraints,
        };

        public static float Get(StatCategory category, PerfRank rank)
        {
            var values = Table[category];
            int i = rank >= PerfRank.VeryPoor ? 3 : (int)rank;
            return values[i];
        }

        public static PerfRank Rate(StatCategory category, float value)
        {
            var values = Table[category];
            for (int i = 0; i < values.Length; i++)
                if (value <= values[i]) return (PerfRank)i;
            return PerfRank.VeryPoor;
        }

        public static string DisplayName(StatCategory category) => Names[category];

        public static string DisplayName(PerfRank rank)
        {
            switch (rank)
            {
                case PerfRank.Excellent: return "Excellent";
                case PerfRank.Good: return "Good";
                case PerfRank.Medium: return "Medium";
                case PerfRank.Poor: return "Poor";
                default: return "Very Poor";
            }
        }
    }
}
