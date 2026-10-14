using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Coffi.MobileOptimizer
{
    internal class BlendShapeFrame
    {
        public float Weight;
        public List<Vector3> DeltaVertices;
        public List<Vector3> DeltaNormals;
        public List<Vector3> DeltaTangents;
    }

    internal class BlendShape
    {
        public string Name;
        public float Weight;
        public List<BlendShapeFrame> Frames = new List<BlendShapeFrame>();
    }

    internal class SubMesh
    {
        public Material Material;
        public List<int> Indices = new List<int>();
    }

    /// <summary>
    /// 編集用のメッシュ表現。頂点はすべてアバタールート空間に変換済みで、
    /// bindpose は書き出し時に bone.worldToLocal * root.localToWorld として再計算する。
    /// そのため、ボーンの付け替え・メッシュ同士の結合をインデックス操作だけで行える。
    /// </summary>
    internal class MeshData
    {
        public List<Vector3> Vertices = new List<Vector3>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<Vector4> Tangents;
        public List<Color> Colors;
        public readonly List<Vector4>[] UVs = new List<Vector4>[8];
        public readonly int[] UVDimensions = new int[8];
        public List<BoneWeight> Weights = new List<BoneWeight>();
        public List<Transform> Bones = new List<Transform>();
        public List<SubMesh> SubMeshes = new List<SubMesh>();
        public List<BlendShape> BlendShapes = new List<BlendShape>();

        public int VertexCount => Vertices.Count;

        public int TriangleCount
        {
            get
            {
                int c = 0;
                foreach (var s in SubMeshes) c += s.Indices.Count / 3;
                return c;
            }
        }

        public int GetOrAddBone(Transform bone)
        {
            int index = Bones.IndexOf(bone);
            if (index >= 0) return index;
            Bones.Add(bone);
            return Bones.Count - 1;
        }

        public BlendShape FindBlendShape(string name)
        {
            foreach (var s in BlendShapes)
                if (s.Name == name) return s;
            return null;
        }

        public int DominantBone(int vertex)
        {
            var w = Weights[vertex];
            int best = w.boneIndex0;
            float bestW = w.weight0;
            if (w.weight1 > bestW) { best = w.boneIndex1; bestW = w.weight1; }
            if (w.weight2 > bestW) { best = w.boneIndex2; bestW = w.weight2; }
            if (w.weight3 > bestW) { best = w.boneIndex3; }
            return best;
        }

        /// <summary>頂点がいずれかの bones (bool 配列) にウェイトを持つか。</summary>
        public bool HasWeightOn(int vertex, bool[] boneFlags)
        {
            var w = Weights[vertex];
            return (w.weight0 > 0 && boneFlags[w.boneIndex0])
                   || (w.weight1 > 0 && boneFlags[w.boneIndex1])
                   || (w.weight2 > 0 && boneFlags[w.boneIndex2])
                   || (w.weight3 > 0 && boneFlags[w.boneIndex3]);
        }

        /// <summary>各ボーンが指定 Transform 群のいずれかの子孫 (自身を含む) かを示すフラグ。</summary>
        public bool[] BoneFlagsUnder(IEnumerable<Transform> ancestors)
        {
            var list = new List<Transform>(ancestors);
            var flags = new bool[Bones.Count];
            for (int i = 0; i < Bones.Count; i++)
                foreach (var a in list)
                    if (a != null && Bones[i] != null && PathUtil.IsDescendantOf(Bones[i], a))
                    {
                        flags[i] = true;
                        break;
                    }
            return flags;
        }

        public int DuplicateVertex(int v)
        {
            Vertices.Add(Vertices[v]);
            Normals.Add(Normals[v]);
            Tangents?.Add(Tangents[v]);
            Colors?.Add(Colors[v]);
            for (int ch = 0; ch < 8; ch++) UVs[ch]?.Add(UVs[ch][v]);
            Weights.Add(Weights[v]);
            foreach (var shape in BlendShapes)
                foreach (var f in shape.Frames)
                {
                    f.DeltaVertices.Add(f.DeltaVertices[v]);
                    f.DeltaNormals.Add(f.DeltaNormals[v]);
                    f.DeltaTangents?.Add(f.DeltaTangents[v]);
                }
            return Vertices.Count - 1;
        }

        /// <summary>どの三角形からも参照されない頂点を削除する。</summary>
        public void RemoveUnusedVertices()
        {
            int n = VertexCount;
            var used = new bool[n];
            foreach (var s in SubMeshes)
                foreach (var i in s.Indices) used[i] = true;

            var remap = new int[n];
            int count = 0;
            for (int i = 0; i < n; i++) remap[i] = used[i] ? count++ : -1;
            if (count == n) return;

            Vertices = Filter(Vertices, remap, count);
            Normals = Filter(Normals, remap, count);
            Tangents = Filter(Tangents, remap, count);
            Colors = Filter(Colors, remap, count);
            for (int ch = 0; ch < 8; ch++) UVs[ch] = Filter(UVs[ch], remap, count);
            Weights = Filter(Weights, remap, count);
            foreach (var shape in BlendShapes)
                foreach (var f in shape.Frames)
                {
                    f.DeltaVertices = Filter(f.DeltaVertices, remap, count);
                    f.DeltaNormals = Filter(f.DeltaNormals, remap, count);
                    f.DeltaTangents = Filter(f.DeltaTangents, remap, count);
                }
            foreach (var s in SubMeshes)
                for (int i = 0; i < s.Indices.Count; i++) s.Indices[i] = remap[s.Indices[i]];
        }

        private static List<T> Filter<T>(List<T> list, int[] remap, int count)
        {
            if (list == null) return null;
            var result = new List<T>(count);
            for (int i = 0; i < list.Count; i++)
                if (remap[i] >= 0) result.Add(list[i]);
            return result;
        }

        /// <summary>ボーンを置き換える (null を返すと root 扱い)。重複したボーンのウェイトは合算する。</summary>
        public void RemapBones(System.Func<Transform, Transform> map)
        {
            var newBones = new List<Transform>();
            var indexMap = new int[Bones.Count];
            for (int i = 0; i < Bones.Count; i++)
            {
                var target = map(Bones[i]);
                int idx = newBones.IndexOf(target);
                if (idx < 0)
                {
                    newBones.Add(target);
                    idx = newBones.Count - 1;
                }
                indexMap[i] = idx;
            }
            for (int v = 0; v < Weights.Count; v++) Weights[v] = RemapWeight(Weights[v], indexMap);
            Bones = newBones;
        }

        public static BoneWeight RemapWeight(BoneWeight w, int[] indexMap)
        {
            var idx = new[] { indexMap[w.boneIndex0], indexMap[w.boneIndex1], indexMap[w.boneIndex2], indexMap[w.boneIndex3] };
            var wt = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    if (wt[i] > 0 && wt[j] > 0 && idx[i] == idx[j])
                    {
                        wt[i] += wt[j];
                        wt[j] = 0;
                    }
            // 重みの大きい順に並べ直して正規化する
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    if (wt[j] > wt[i])
                    {
                        (wt[i], wt[j]) = (wt[j], wt[i]);
                        (idx[i], idx[j]) = (idx[j], idx[i]);
                    }
            float sum = wt[0] + wt[1] + wt[2] + wt[3];
            if (sum <= 0) { wt[0] = 1; sum = 1; }
            for (int i = 0; i < 4; i++)
                if (wt[i] <= 0) idx[i] = 0;
            return new BoneWeight
            {
                boneIndex0 = idx[0], weight0 = wt[0] / sum,
                boneIndex1 = idx[1], weight1 = wt[1] / sum,
                boneIndex2 = idx[2], weight2 = wt[2] / sum,
                boneIndex3 = idx[3], weight3 = wt[3] / sum,
            };
        }

        /// <summary>BlendShape の指定ウェイトでの差分を求める (Unity と同じくフレーム間を線形補間)。</summary>
        public static void EvaluateBlendShape(BlendShape shape, float weight, int vertex,
            out Vector3 dv, out Vector3 dn, out Vector3 dt)
        {
            dv = dn = dt = Vector3.zero;
            var frames = shape.Frames;
            if (frames.Count == 0) return;
            if (frames.Count == 1 || weight <= frames[0].Weight)
            {
                var f = frames[0];
                float s = Mathf.Approximately(f.Weight, 0) ? 0 : weight / f.Weight;
                dv = f.DeltaVertices[vertex] * s;
                dn = f.DeltaNormals[vertex] * s;
                if (f.DeltaTangents != null) dt = f.DeltaTangents[vertex] * s;
                return;
            }
            int k = 0;
            while (k < frames.Count - 2 && weight > frames[k + 1].Weight) k++;
            var a = frames[k];
            var b = frames[k + 1];
            float t = Mathf.Approximately(b.Weight, a.Weight) ? 0 : (weight - a.Weight) / (b.Weight - a.Weight);
            dv = Vector3.LerpUnclamped(a.DeltaVertices[vertex], b.DeltaVertices[vertex], t);
            dn = Vector3.LerpUnclamped(a.DeltaNormals[vertex], b.DeltaNormals[vertex], t);
            if (a.DeltaTangents != null && b.DeltaTangents != null)
                dt = Vector3.LerpUnclamped(a.DeltaTangents[vertex], b.DeltaTangents[vertex], t);
        }

        /// <summary>BlendShape を現在の (または指定した) ウェイトでベースメッシュに焼き込む。</summary>
        public void BakeBlendShape(BlendShape shape, float weight)
        {
            for (int v = 0; v < VertexCount; v++)
            {
                EvaluateBlendShape(shape, weight, v, out var dv, out var dn, out var dt);
                Vertices[v] += dv;
                if (dn != Vector3.zero) Normals[v] = (Normals[v] + dn).normalized;
                if (Tangents != null && dt != Vector3.zero)
                {
                    var t = Tangents[v];
                    var nt = (new Vector3(t.x, t.y, t.z) + dt).normalized;
                    Tangents[v] = new Vector4(nt.x, nt.y, nt.z, t.w);
                }
            }
        }

        /// <summary>他のメッシュを結合する。ボーンは和集合、BlendShape は名前で統合、サブメッシュはマテリアルで統合。</summary>
        public void Append(MeshData other)
        {
            int offset = VertexCount;
            int otherCount = other.VertexCount;

            var boneMap = new int[other.Bones.Count];
            for (int i = 0; i < other.Bones.Count; i++) boneMap[i] = GetOrAddBone(other.Bones[i]);

            Vertices.AddRange(other.Vertices);
            Normals.AddRange(other.Normals);

            if (Tangents != null || other.Tangents != null)
            {
                if (Tangents == null) Tangents = Fill(offset, new Vector4(1, 0, 0, 1));
                Tangents.AddRange(other.Tangents ?? Fill(otherCount, new Vector4(1, 0, 0, 1)));
            }
            if (Colors != null || other.Colors != null)
            {
                if (Colors == null) Colors = Fill(offset, Color.white);
                Colors.AddRange(other.Colors ?? Fill(otherCount, Color.white));
            }
            for (int ch = 0; ch < 8; ch++)
            {
                if (UVs[ch] == null && other.UVs[ch] == null) continue;
                if (UVs[ch] == null) UVs[ch] = Fill(offset, Vector4.zero);
                UVs[ch].AddRange(other.UVs[ch] ?? Fill(otherCount, Vector4.zero));
                UVDimensions[ch] = Mathf.Max(UVDimensions[ch], other.UVDimensions[ch], 2);
            }
            foreach (var w in other.Weights) Weights.Add(RemapWeight(w, boneMap));

            // BlendShape: 既存の形状のフレーム構成に合わせて相手側の差分を評価する
            var handled = new HashSet<string>();
            foreach (var shape in BlendShapes)
            {
                handled.Add(shape.Name);
                var src = other.FindBlendShape(shape.Name);
                foreach (var f in shape.Frames)
                {
                    bool hasTangent = f.DeltaTangents != null;
                    for (int v = 0; v < otherCount; v++)
                    {
                        Vector3 dv = Vector3.zero, dn = Vector3.zero, dt = Vector3.zero;
                        if (src != null) EvaluateBlendShape(src, f.Weight, v, out dv, out dn, out dt);
                        f.DeltaVertices.Add(dv);
                        f.DeltaNormals.Add(dn);
                        if (hasTangent) f.DeltaTangents.Add(dt);
                    }
                }
                if (src != null && Mathf.Approximately(shape.Weight, 0)) shape.Weight = src.Weight;
            }
            foreach (var src in other.BlendShapes)
            {
                if (handled.Contains(src.Name)) continue;
                var shape = new BlendShape { Name = src.Name, Weight = src.Weight };
                foreach (var f in src.Frames)
                {
                    var nf = new BlendShapeFrame
                    {
                        Weight = f.Weight,
                        DeltaVertices = Fill(offset, Vector3.zero),
                        DeltaNormals = Fill(offset, Vector3.zero),
                        DeltaTangents = Tangents != null ? Fill(offset, Vector3.zero) : null,
                    };
                    nf.DeltaVertices.AddRange(f.DeltaVertices);
                    nf.DeltaNormals.AddRange(f.DeltaNormals);
                    nf.DeltaTangents?.AddRange(f.DeltaTangents ?? Fill(otherCount, Vector3.zero));
                    shape.Frames.Add(nf);
                }
                BlendShapes.Add(shape);
            }
            // タンジェントの有無を揃える
            if (Tangents != null)
                foreach (var shape in BlendShapes)
                    foreach (var f in shape.Frames)
                        if (f.DeltaTangents == null) f.DeltaTangents = Fill(VertexCount, Vector3.zero);

            foreach (var sub in other.SubMeshes)
            {
                SubMesh target = null;
                foreach (var s in SubMeshes)
                    if (s.Material == sub.Material) { target = s; break; }
                if (target == null)
                {
                    target = new SubMesh { Material = sub.Material };
                    SubMeshes.Add(target);
                }
                foreach (var i in sub.Indices) target.Indices.Add(i + offset);
            }
        }

        private static List<T> Fill<T>(int count, T value)
        {
            var list = new List<T>(count);
            for (int i = 0; i < count; i++) list.Add(value);
            return list;
        }

        public static IndexFormat IndexFormatFor(int vertexCount)
        {
            return vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        }
    }
}
