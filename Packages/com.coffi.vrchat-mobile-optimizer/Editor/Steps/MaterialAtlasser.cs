using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// 全マテリアルのメインテクスチャ × メインカラーを 1 枚のアトラスに焼き込み、
    /// VRChat/Mobile/Toon Lit のマテリアル 1 つにまとめる。
    /// </summary>
    internal static class MaterialAtlasser
    {
        private static readonly string[] MainTexProps = { "_MainTex", "_BaseMap", "_BaseColorMap" };
        private static readonly string[] ColorProps = { "_Color", "_BaseColor" };
        private const int Padding = 4;

        private class Region
        {
            public Material Material;
            public Texture Texture;
            public Color Color = Color.white;
            public Vector2 Scale = Vector2.one;
            public Vector2 Offset = Vector2.zero;
            public Vector2 UvMin = new Vector2(float.MaxValue, float.MaxValue);
            public Vector2 UvMax = new Vector2(float.MinValue, float.MinValue);
            public float Area;
            public int FaceTriangles;
            public int Triangles;
            public float DesiredW, DesiredH;
            public int W, H, X, Y;

            public Vector2 UvSize => UvMax - UvMin;
        }

        public static void Run(OptimizerContext ctx)
        {
            var s = ctx.Settings;
            var regions = new Dictionary<int, Region>();
            var head = ctx.HumanBone(HumanBodyBones.Head);

            foreach (var unit in ctx.Units) SplitSharedVertices(unit.Data);

            foreach (var unit in ctx.Units)
            {
                var d = unit.Data;
                var headFlags = head != null ? d.BoneFlagsUnder(new[] { head }) : new bool[d.Bones.Count];
                foreach (var sm in d.SubMeshes)
                {
                    var region = GetRegion(regions, sm.Material);
                    for (int i = 0; i + 2 < sm.Indices.Count; i += 3)
                    {
                        int a = sm.Indices[i], b = sm.Indices[i + 1], c = sm.Indices[i + 2];
                        foreach (var v in new[] { a, b, c })
                        {
                            var uv = TransformedUV(d, v, region);
                            region.UvMin = Vector2.Min(region.UvMin, uv);
                            region.UvMax = Vector2.Max(region.UvMax, uv);
                        }
                        region.Area += Vector3.Cross(d.Vertices[b] - d.Vertices[a], d.Vertices[c] - d.Vertices[a]).magnitude * 0.5f;
                        region.Triangles++;
                        if (headFlags[d.DominantBone(a)]) region.FaceTriangles++;
                    }
                }
            }
            if (regions.Count == 0) return;

            foreach (var r in regions.Values)
            {
                if (r.Triangles == 0) { r.UvMin = Vector2.zero; r.UvMax = Vector2.one; }
                var size = r.UvSize;
                if (size.x < 1e-4f) { r.UvMin.x -= 5e-4f; r.UvMax.x += 5e-4f; }
                if (size.y < 1e-4f) { r.UvMin.y -= 5e-4f; r.UvMax.y += 5e-4f; }
                if (r.UvSize.x > 2.01f || r.UvSize.y > 2.01f)
                    ctx.Report.Warn($"マテリアル '{Name(r.Material)}' の UV が大きくタイリングしているため、アトラス上の解像度が下がります");
                if (r.Material != null && (r.Material.renderQueue > 2450 || r.Material.shader.name.ToLowerInvariant().Contains("trans") || r.Material.shader.name.ToLowerInvariant().Contains("cutout")))
                    ctx.Report.Warn($"マテリアル '{Name(r.Material)}' は透過 / カットアウトですが、Toon Lit では不透明になります");
            }

            int atlasSize = Mathf.ClosestPowerOfTwo(Mathf.Clamp(s.atlasSize, 256, 4096));
            float memLimit = MobileLimits.Get(StatCategory.TextureMemoryMB, s.targetRank);
            while (atlasSize > 256 && MobileRankCalculator.EstimateAtlasBytes(atlasSize) / (1024f * 1024f) > memLimit)
                atlasSize /= 2;

            var list = regions.Values.ToList();
            ComputeDesiredSizes(list, atlasSize, s.faceTextureBoost);
            float factor = 1f;
            while (!Pack(list, atlasSize, factor))
            {
                factor *= 0.9f;
                if (factor < 0.01f)
                {
                    ctx.Report.Warn("アトラスへの配置に失敗しました");
                    return;
                }
            }

            var atlasTex = Render(list, atlasSize);
            var name = ctx.Root.name + "_Atlas";
            var atlas = ctx.Assets.SavePng(atlasTex, name, importer => ConfigureImporter(importer, atlasSize));
            Object.DestroyImmediate(atlasTex);

            var material = new Material(MobileShader()) { name = ctx.Root.name + "_Mobile" };
            material.mainTexture = atlas;
            ctx.Assets.Save(material, material.name, "mat");

            foreach (var unit in ctx.Units)
            {
                var d = unit.Data;
                if (d.UVs[0] == null)
                {
                    d.UVs[0] = Enumerable.Repeat(Vector4.zero, d.VertexCount).ToList();
                    d.UVDimensions[0] = 2;
                }
                var done = new bool[d.VertexCount];
                foreach (var sm in d.SubMeshes)
                {
                    var r = GetRegion(regions, sm.Material);
                    foreach (var v in sm.Indices)
                    {
                        if (done[v]) continue;
                        done[v] = true;
                        var uv = TransformedUV(d, v, r);
                        var t = new Vector2((uv.x - r.UvMin.x) / r.UvSize.x, (uv.y - r.UvMin.y) / r.UvSize.y);
                        float innerW = r.W - Padding * 2, innerH = r.H - Padding * 2;
                        var old = d.UVs[0][v];
                        d.UVs[0][v] = new Vector4(
                            (r.X + Padding + t.x * innerW) / atlasSize,
                            (r.Y + Padding + t.y * innerH) / atlasSize,
                            old.z, old.w);
                    }
                }
                var merged = new SubMesh { Material = material };
                foreach (var sm in d.SubMeshes) merged.Indices.AddRange(sm.Indices);
                d.SubMeshes = new List<SubMesh> { merged };
            }
            ctx.Report.Info($"マテリアル {regions.Count} 個を {atlasSize}x{atlasSize} のアトラス 1 枚にまとめました");
        }

        /// <summary>アトラス化しない場合も、マテリアルを Mobile シェーダーに変換したコピーにする。</summary>
        public static void ConvertOnly(OptimizerContext ctx)
        {
            var map = new Dictionary<int, Material>();
            var shader = MobileShader();
            foreach (var unit in ctx.Units)
                foreach (var sm in unit.Data.SubMeshes)
                {
                    if (sm.Material == null) continue;
                    int id = sm.Material.GetInstanceID();
                    if (!map.TryGetValue(id, out var converted))
                    {
                        var src = sm.Material;
                        converted = new Material(shader) { name = src.name + "_Mobile" };
                        var prop = MainTexProps.FirstOrDefault(p => src.HasProperty(p));
                        if (prop != null)
                        {
                            converted.mainTexture = src.GetTexture(prop);
                            converted.mainTextureScale = src.GetTextureScale(prop);
                            converted.mainTextureOffset = src.GetTextureOffset(prop);
                        }
                        ctx.Assets.Save(converted, converted.name, "mat");
                        map[id] = converted;
                    }
                    sm.Material = converted;
                }
        }

        public static Shader MobileShader()
        {
            foreach (var name in new[] { "VRChat/Mobile/Toon Lit", "VRChat/Mobile/Diffuse", "Unlit/Texture" })
            {
                var shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            return null;
        }

        private static string Name(Material m) => m != null ? m.name : "(なし)";

        private static Region GetRegion(Dictionary<int, Region> regions, Material m)
        {
            int key = m != null ? m.GetInstanceID() : 0;
            if (regions.TryGetValue(key, out var r)) return r;
            r = new Region { Material = m };
            if (m != null)
            {
                var texProp = MainTexProps.FirstOrDefault(p => m.HasProperty(p));
                if (texProp != null)
                {
                    r.Texture = m.GetTexture(texProp);
                    r.Scale = m.GetTextureScale(texProp);
                    r.Offset = m.GetTextureOffset(texProp);
                }
                var colorProp = ColorProps.FirstOrDefault(p => m.HasProperty(p));
                if (colorProp != null) r.Color = m.GetColor(colorProp);
            }
            regions[key] = r;
            return r;
        }

        private static Vector2 TransformedUV(MeshData d, int v, Region r)
        {
            var raw = d.UVs[0] != null ? (Vector2)d.UVs[0][v] : Vector2.zero;
            return new Vector2(raw.x * r.Scale.x + r.Offset.x, raw.y * r.Scale.y + r.Offset.y);
        }

        /// <summary>1 つの頂点を複数マテリアルで共有している場合は複製し、頂点ごとに UV を書き換えられるようにする。</summary>
        private static void SplitSharedVertices(MeshData d)
        {
            var owner = Enumerable.Repeat(-1, d.VertexCount).ToList();
            var dup = new Dictionary<(int, int), int>();
            for (int s = 0; s < d.SubMeshes.Count; s++)
            {
                var idx = d.SubMeshes[s].Indices;
                for (int i = 0; i < idx.Count; i++)
                {
                    int v = idx[i];
                    if (owner[v] == -1) owner[v] = s;
                    else if (owner[v] != s)
                    {
                        if (!dup.TryGetValue((v, s), out int nv))
                        {
                            nv = d.DuplicateVertex(v);
                            owner.Add(s);
                            dup[(v, s)] = nv;
                        }
                        idx[i] = nv;
                    }
                }
            }
        }

        private static void ComputeDesiredSizes(List<Region> regions, int atlasSize, float faceBoost)
        {
            float usable = atlasSize * (float)atlasSize * 0.85f;
            float total = regions.Sum(r => Weight(r));
            foreach (var r in regions)
            {
                float area = usable * Weight(r) / Mathf.Max(total, 1e-12f);
                var size = r.UvSize;
                float srcW = r.Texture != null ? r.Texture.width * size.x : 16;
                float srcH = r.Texture != null ? r.Texture.height * size.y : 16;
                float aspect = Mathf.Clamp(srcW / Mathf.Max(srcH, 1e-3f), 1f / 16, 16);
                float w = Mathf.Sqrt(area * aspect);
                float h = Mathf.Sqrt(area / aspect);
                float cap = Mathf.Min(srcW / w, srcH / h, 1f);
                r.DesiredW = Mathf.Max(4, w * cap);
                r.DesiredH = Mathf.Max(4, h * cap);
            }

            float Weight(Region r)
            {
                bool face = r.Triangles > 0 && r.FaceTriangles * 2 > r.Triangles;
                return Mathf.Max(r.Area, 1e-8f) * (face ? faceBoost : 1f);
            }
        }

        private static bool Pack(List<Region> regions, int atlasSize, float factor)
        {
            foreach (var r in regions)
            {
                r.W = Mathf.Max(1, Mathf.RoundToInt(r.DesiredW * factor)) + Padding * 2;
                r.H = Mathf.Max(1, Mathf.RoundToInt(r.DesiredH * factor)) + Padding * 2;
            }
            int x = 0, y = 0, shelf = 0;
            foreach (var r in regions.OrderByDescending(r => r.H))
            {
                if (r.W > atlasSize) return false;
                if (x + r.W > atlasSize)
                {
                    x = 0;
                    y += shelf;
                    shelf = 0;
                }
                if (y + r.H > atlasSize) return false;
                r.X = x;
                r.Y = y;
                x += r.W;
                shelf = Mathf.Max(shelf, r.H);
            }
            return true;
        }

        private static Texture2D Render(List<Region> regions, int atlasSize)
        {
            var shader = Shader.Find("Hidden/Coffi/MobileOptimizer/AtlasBake");
            var mat = new Material(shader);
            var atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false, false);
            var fill = Enumerable.Repeat(new Color32(128, 128, 128, 255), atlasSize * atlasSize).ToArray();
            atlas.SetPixels32(fill);

            var prev = RenderTexture.active;
            foreach (var r in regions)
            {
                float innerW = r.W - Padding * 2, innerH = r.H - Padding * 2;
                var size = r.UvSize;
                var min = r.UvMin - new Vector2(size.x * Padding / innerW, size.y * Padding / innerH);
                var full = new Vector2(size.x * r.W / innerW, size.y * r.H / innerH);
                mat.SetColor("_Color", r.Color);
                mat.SetVector("_UVRect", new Vector4(min.x, min.y, full.x, full.y));

                var rt = RenderTexture.GetTemporary(r.W, r.H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(r.Texture != null ? r.Texture : Texture2D.whiteTexture, rt, mat);
                RenderTexture.active = rt;
                atlas.ReadPixels(new Rect(0, 0, r.W, r.H), r.X, r.Y);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            atlas.Apply();
            Object.DestroyImmediate(mat);
            return atlas;
        }

        private static void ConfigureImporter(TextureImporter importer, int size)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.maxTextureSize = size;
            importer.isReadable = false;
            foreach (var platform in new[] { "Android", "iPhone" })
            {
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = platform,
                    overridden = true,
                    maxTextureSize = size,
                    format = TextureImporterFormat.ASTC_6x6,
                    compressionQuality = 50,
                });
            }
        }
    }
}
