using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>編集済みの MeshUnit を Mesh アセットと SkinnedMeshRenderer として書き出す。</summary>
    internal static class MeshWriter
    {
        public static void Write(OptimizerContext ctx)
        {
            var root = ctx.RootTransform;
            var hips = ctx.HumanBone(HumanBodyBones.Hips);
            if (hips == null) hips = root;
            var anchor = ctx.HumanBone(HumanBodyBones.Chest);
            if (anchor == null) anchor = hips;

            // 書き出し先が別 GameObject になるユニットの元レンダラーを先に消す
            foreach (var unit in ctx.Units)
            {
                if (IsInPlace(unit)) continue;
                foreach (var r in unit.SourceRenderers) RemoveRenderer(r);
            }

            foreach (var unit in ctx.Units)
            {
                SkinnedMeshRenderer smr;
                if (IsInPlace(unit))
                {
                    smr = ReplaceWithSkinned(unit.SourceRenderers[0]);
                }
                else
                {
                    // 元の "Body" などはレンダラーを外した抜け殻なので名前を譲ってもらう
                    var existing = root.Find(unit.Name);
                    if (existing != null && existing.GetComponent<Renderer>() == null) existing.name += "_old";
                    var go = new GameObject(UniqueChildName(root, unit.Name));
                    go.transform.SetParent(root, false);
                    smr = go.AddComponent<SkinnedMeshRenderer>();
                }

                var mesh = ToMesh(unit.Data, root, unit.Name);
                ctx.Assets.Save(mesh, ctx.Root.name + "_" + unit.Name);

                smr.sharedMesh = mesh;
                smr.bones = unit.Data.Bones.ToArray();
                smr.rootBone = hips;
                smr.probeAnchor = anchor;
                smr.updateWhenOffscreen = false;
                smr.sharedMaterials = unit.Data.SubMeshes.Select(s => s.Material).ToArray();
                for (int i = 0; i < unit.Data.BlendShapes.Count; i++)
                    smr.SetBlendShapeWeight(i, unit.Data.BlendShapes[i].Weight);
                smr.localBounds = ComputeBounds(unit.Data, root, hips);

                var newPath = PathUtil.GetPath(smr.transform, root);
                foreach (var p in unit.SourcePaths)
                    if (p != newPath) ctx.RendererPathRemap[p] = newPath;
                foreach (var id in unit.SourceRendererIds) ctx.RendererReplacement[id] = smr;
            }

            FixDescriptor(ctx);
        }

        private static bool IsInPlace(MeshUnit unit)
        {
            return unit.KeepSeparate && unit.SourceRenderers.Count == 1 && unit.SourceRenderers[0] != null;
        }

        // GameObject 自体はボーンとして使われている場合があるので残し、TransformCleaner に任せる
        private static void RemoveRenderer(Renderer r)
        {
            if (r == null) return;
            var mf = r.GetComponent<MeshFilter>();
            Object.DestroyImmediate(r);
            if (mf != null) Object.DestroyImmediate(mf);
        }

        private static SkinnedMeshRenderer ReplaceWithSkinned(Renderer r)
        {
            if (r is SkinnedMeshRenderer s) return s;
            var go = r.gameObject;
            bool enabled = r.enabled;
            var mf = go.GetComponent<MeshFilter>();
            Object.DestroyImmediate(r);
            if (mf != null) Object.DestroyImmediate(mf);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.enabled = enabled;
            return smr;
        }

        private static string UniqueChildName(Transform root, string name)
        {
            var candidate = name;
            int i = 1;
            while (root.Find(candidate) != null) candidate = $"{name}_{i++}";
            return candidate;
        }

        public static Mesh ToMesh(MeshData d, Transform root, string name)
        {
            var mesh = new Mesh { name = name, indexFormat = MeshData.IndexFormatFor(d.VertexCount) };
            mesh.SetVertices(d.Vertices);
            mesh.SetNormals(d.Normals);
            if (d.Tangents != null) mesh.SetTangents(d.Tangents);
            if (d.Colors != null) mesh.SetColors(d.Colors);
            for (int ch = 0; ch < 8; ch++)
            {
                var uv = d.UVs[ch];
                if (uv == null) continue;
                switch (d.UVDimensions[ch])
                {
                    case 3:
                        mesh.SetUVs(ch, uv.Select(u => (Vector3)u).ToList());
                        break;
                    case 4:
                        mesh.SetUVs(ch, uv);
                        break;
                    default:
                        mesh.SetUVs(ch, uv.Select(u => (Vector2)u).ToList());
                        break;
                }
            }
            mesh.boneWeights = d.Weights.ToArray();
            var rootL2W = root.localToWorldMatrix;
            mesh.bindposes = d.Bones.Select(b => (b != null ? b.worldToLocalMatrix : root.worldToLocalMatrix) * rootL2W).ToArray();
            mesh.subMeshCount = d.SubMeshes.Count;
            for (int i = 0; i < d.SubMeshes.Count; i++) mesh.SetTriangles(d.SubMeshes[i].Indices, i, false);
            foreach (var shape in d.BlendShapes)
                foreach (var f in shape.Frames)
                    mesh.AddBlendShapeFrame(shape.Name, f.Weight, f.DeltaVertices.ToArray(), f.DeltaNormals.ToArray(),
                        d.Tangents != null && f.DeltaTangents != null ? f.DeltaTangents.ToArray() : null);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Bounds ComputeBounds(MeshData d, Transform root, Transform rootBone)
        {
            if (d.VertexCount == 0) return new Bounds(Vector3.zero, Vector3.one);
            var m = rootBone.worldToLocalMatrix * root.localToWorldMatrix;
            var b = new Bounds(m.MultiplyPoint3x4(d.Vertices[0]), Vector3.zero);
            foreach (var v in d.Vertices) b.Encapsulate(m.MultiplyPoint3x4(v));
            b.Expand(b.size * 0.1f);
            return b;
        }

        /// <summary>Viseme / まばたきの参照を書き出し後のメッシュへ付け替える。</summary>
        private static void FixDescriptor(OptimizerContext ctx)
        {
            if (ctx.Descriptor == null) return;
            var so = new SerializedObject(ctx.Descriptor);

            if (ctx.VisemeRendererId != 0 && ctx.RendererReplacement.TryGetValue(ctx.VisemeRendererId, out var viseme))
            {
                var prop = so.FindProperty("VisemeSkinnedMesh");
                if (prop != null) prop.objectReferenceValue = viseme;
            }

            if (ctx.EyelidRendererId != 0 && ctx.RendererReplacement.TryGetValue(ctx.EyelidRendererId, out var eyelid))
            {
                var meshProp = so.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
                var indices = so.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                if (meshProp != null) meshProp.objectReferenceValue = eyelid;
                if (indices != null)
                {
                    indices.arraySize = ctx.EyelidBlendShapeNames.Count;
                    for (int i = 0; i < ctx.EyelidBlendShapeNames.Count; i++)
                    {
                        var name = ctx.EyelidBlendShapeNames[i];
                        indices.GetArrayElementAtIndex(i).intValue =
                            name == null ? -1 : eyelid.sharedMesh.GetBlendShapeIndex(name);
                    }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
