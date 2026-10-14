using System.Collections.Generic;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>レンダラーを読み取り、ルート空間に正規化した MeshData を作る。</summary>
    internal static class MeshUnitBuilder
    {
        public static List<MeshUnit> Build(OptimizerContext ctx)
        {
            var units = new List<MeshUnit>();
            var root = ctx.RootTransform;
            foreach (var r in ctx.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                var data = FromRenderer(r, root, ctx.Report);
                if (data == null || data.VertexCount == 0) continue;
                var unit = new MeshUnit
                {
                    Name = r.name,
                    Data = data,
                    KeepSeparate = ctx.SeparateRenderers.Contains(r),
                    Toggleable = ctx.Anim != null && ctx.Anim.IsToggled(r.transform, root),
                };
                unit.SourceRenderers.Add(r);
                unit.SourcePaths.Add(PathUtil.GetPath(r.transform, root));
                unit.SourceRendererIds.Add(r.GetInstanceID());
                units.Add(unit);
            }
            return units;
        }

        public static MeshData FromRenderer(Renderer r, Transform root, OptimizerReport report)
        {
            Mesh mesh = null;
            Transform[] bones = null;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                mesh = smr.sharedMesh;
                bones = smr.bones;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh == null) return null;

            int n = mesh.vertexCount;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var colors = mesh.colors;
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            var materials = r.sharedMaterials;

            var d = new MeshData();
            var rootW2L = root.worldToLocalMatrix;
            var rigidMat = rootW2L * r.transform.localToWorldMatrix;
            bool skinned = smr != null && bones != null && bones.Length > 0 && weights.Length == n;

            Matrix4x4[] boneMats = null;
            int[] boneRemap = null;
            if (skinned)
            {
                boneMats = new Matrix4x4[bones.Length];
                boneRemap = new int[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    var b = bones[i] != null ? bones[i] : r.transform;
                    var bp = i < bindposes.Length ? bindposes[i] : Matrix4x4.identity;
                    boneMats[i] = rootW2L * b.localToWorldMatrix * bp;
                    boneRemap[i] = d.GetOrAddBone(b);
                }
            }
            else
            {
                d.Bones.Add(r.transform);
            }

            var skinMats = new Matrix4x4[n];
            bool hasNormals = normals.Length == n;
            bool hasTangents = tangents.Length == n;
            if (hasTangents) d.Tangents = new List<Vector4>(n);

            for (int v = 0; v < n; v++)
            {
                Matrix4x4 m;
                if (skinned)
                {
                    var w = weights[v];
                    m = new Matrix4x4();
                    float sum = 0;
                    sum += Accumulate(ref m, boneMats, w.boneIndex0, w.weight0);
                    sum += Accumulate(ref m, boneMats, w.boneIndex1, w.weight1);
                    sum += Accumulate(ref m, boneMats, w.boneIndex2, w.weight2);
                    sum += Accumulate(ref m, boneMats, w.boneIndex3, w.weight3);
                    if (sum < 1e-6f) m = rigidMat;
                    d.Weights.Add(MeshData.RemapWeight(w, boneRemap));
                }
                else
                {
                    m = rigidMat;
                    d.Weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1 });
                }
                skinMats[v] = m;
                d.Vertices.Add(m.MultiplyPoint3x4(verts[v]));
                d.Normals.Add(hasNormals ? m.MultiplyVector(normals[v]).normalized : Vector3.up);
                if (hasTangents)
                {
                    var t = m.MultiplyVector(tangents[v]).normalized;
                    d.Tangents.Add(new Vector4(t.x, t.y, t.z, tangents[v].w));
                }
            }

            if (colors.Length == n) d.Colors = new List<Color>(colors);

            for (int ch = 0; ch < 8; ch++)
            {
                var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch;
                if (!mesh.HasVertexAttribute(attr)) continue;
                var list = new List<Vector4>(n);
                mesh.GetUVs(ch, list);
                if (list.Count != n) continue;
                d.UVs[ch] = list;
                d.UVDimensions[ch] = mesh.GetVertexAttributeDimension(attr);
            }

            bool flip = !skinned && rigidMat.determinant < 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                {
                    report.Warn($"{r.name}: サブメッシュ {s} は三角形以外のトポロジーのため除外しました");
                    continue;
                }
                var sub = new SubMesh { Material = s < materials.Length ? materials[s] : null };
                sub.Indices.AddRange(mesh.GetTriangles(s));
                if (flip)
                    for (int i = 0; i < sub.Indices.Count; i += 3)
                        (sub.Indices[i + 1], sub.Indices[i + 2]) = (sub.Indices[i + 2], sub.Indices[i + 1]);
                d.SubMeshes.Add(sub);
            }
            if (materials.Length > mesh.subMeshCount)
                report.Warn($"{r.name}: マテリアル数がサブメッシュ数より多い (マルチパス) ため、余分なマテリアルは無視しました");

            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var shape = new BlendShape
                {
                    Name = mesh.GetBlendShapeName(i),
                    Weight = smr != null ? smr.GetBlendShapeWeight(i) : 0,
                };
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(i); f++)
                {
                    mesh.GetBlendShapeFrameVertices(i, f, dv, dn, dt);
                    var frame = new BlendShapeFrame
                    {
                        Weight = mesh.GetBlendShapeFrameWeight(i, f),
                        DeltaVertices = new List<Vector3>(n),
                        DeltaNormals = new List<Vector3>(n),
                        DeltaTangents = hasTangents ? new List<Vector3>(n) : null,
                    };
                    for (int v = 0; v < n; v++)
                    {
                        frame.DeltaVertices.Add(skinMats[v].MultiplyVector(dv[v]));
                        frame.DeltaNormals.Add(skinMats[v].MultiplyVector(dn[v]));
                        if (hasTangents) frame.DeltaTangents.Add(skinMats[v].MultiplyVector(dt[v]));
                    }
                    shape.Frames.Add(frame);
                }
                d.BlendShapes.Add(shape);
            }
            return d;
        }

        private static float Accumulate(ref Matrix4x4 acc, Matrix4x4[] mats, int index, float weight)
        {
            if (weight <= 0 || index < 0 || index >= mats.Length) return 0;
            var src = mats[index];
            for (int k = 0; k < 16; k++) acc[k] += src[k] * weight;
            return weight;
        }
    }
}
