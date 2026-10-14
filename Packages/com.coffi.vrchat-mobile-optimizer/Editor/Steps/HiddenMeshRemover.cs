using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>服で隠れた素体のポリゴンを、頂点からのレイ判定で削除する。</summary>
    internal static class HiddenMeshRemover
    {
        public static void Run(OptimizerContext ctx)
        {
            var body = FindBody(ctx);
            if (body == null)
            {
                ctx.Report.Warn($"素体メッシュ '{ctx.Settings.bodyRendererPath}' が見つからないため、隠れポリゴン削除をスキップしました (非表示扱いで削除された可能性があります)");
                return;
            }
            var removed = ComputeRemovedTriangles(ctx, body);
            if (removed == null)
            {
                ctx.Report.Warn("服メッシュが見つからないため、隠れポリゴン削除をスキップしました");
                return;
            }

            int count = 0;
            var data = body.Data;
            for (int s = 0; s < data.SubMeshes.Count; s++)
            {
                var src = data.SubMeshes[s].Indices;
                var dst = new List<int>(src.Count);
                for (int t = 0; t < src.Count / 3; t++)
                {
                    if (removed[s][t]) { count++; continue; }
                    dst.Add(src[t * 3]);
                    dst.Add(src[t * 3 + 1]);
                    dst.Add(src[t * 3 + 2]);
                }
                data.SubMeshes[s].Indices = dst;
            }
            data.SubMeshes.RemoveAll(sm => sm.Indices.Count == 0);
            data.RemoveUnusedVertices();
            ctx.Report.AddRemoved("隠れポリゴン", $"{body.Name}: {count} ポリゴン");
        }

        public static MeshUnit FindBody(OptimizerContext ctx)
        {
            var path = ctx.Settings.bodyRendererPath;
            if (!string.IsNullOrEmpty(path))
                return ctx.Units.FirstOrDefault(u => u.SourcePaths.Contains(path));
            var named = ctx.Units
                .Where(u => u.Name.ToLowerInvariant().Contains("body"))
                .OrderByDescending(u => u.Data.VertexCount)
                .FirstOrDefault();
            return named ?? ctx.Units.OrderByDescending(u => u.Data.VertexCount).FirstOrDefault();
        }

        /// <summary>
        /// 素体を隠す服。指定があればそれだけ (トグル対象でも使う)、なければ素体以外で常に表示されるメッシュすべて。
        /// </summary>
        public static List<MeshUnit> GetOccluders(OptimizerContext ctx, MeshUnit body)
        {
            var paths = ctx.Settings.clothingRendererPaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
                return ctx.Units.Where(u => u != body && u.SourcePaths.Any(paths.Contains)).ToList();
            return ctx.Units.Where(u => u != body && !u.KeepSeparate).ToList();
        }

        /// <summary>サブメッシュごとに、削除する三角形のフラグを返す。</summary>
        public static List<bool[]> ComputeRemovedTriangles(OptimizerContext ctx, MeshUnit body)
        {
            var s = ctx.Settings;
            var occluders = GetOccluders(ctx, body);
            if (occluders.Count == 0) return null;

            float scale = Mathf.Max(1e-5f, Mathf.Abs(ctx.RootTransform.lossyScale.x));
            float dist = s.hiddenDistance / scale;
            float back = Mathf.Min(0.005f / scale, dist * 0.3f);

            var grid = new TriangleGrid(Mathf.Max(dist, 1e-4f));
            foreach (var u in occluders)
                foreach (var sm in u.Data.SubMeshes)
                    for (int i = 0; i + 2 < sm.Indices.Count; i += 3)
                        grid.Add(u.Data.Vertices[sm.Indices[i]], u.Data.Vertices[sm.Indices[i + 1]], u.Data.Vertices[sm.Indices[i + 2]]);

            var data = body.Data;
            var protectedFlags = data.BoneFlagsUnder(ctx.HumanBones(s.hiddenProtectedBones));
            var dirs = new Vector3[5];
            var hidden = new bool[data.VertexCount];
            float angle = s.hiddenRayAngle * Mathf.Deg2Rad;
            for (int v = 0; v < data.VertexCount; v++)
            {
                if (data.HasWeightOn(v, protectedFlags)) continue;
                var n = data.Normals[v];
                if (n.sqrMagnitude < 1e-8f) continue;
                n.Normalize();
                BuildDirections(n, angle, dirs);
                var p = data.Vertices[v];
                var start = p - n * back;
                bool all = true;
                foreach (var d in dirs)
                {
                    if (!grid.SegmentHits(start, p + d * dist))
                    {
                        all = false;
                        break;
                    }
                }
                hidden[v] = all;
            }

            var result = new List<bool[]>();
            foreach (var sm in data.SubMeshes)
            {
                var flags = new bool[sm.Indices.Count / 3];
                for (int t = 0; t < flags.Length; t++)
                    flags[t] = hidden[sm.Indices[t * 3]] && hidden[sm.Indices[t * 3 + 1]] && hidden[sm.Indices[t * 3 + 2]];
                result.Add(flags);
            }
            return result;
        }

        /// <summary>プレビュー用に、削除予定の三角形だけをワールド空間のメッシュにする。</summary>
        public static Mesh BuildPreviewMesh(OptimizerContext ctx, MeshUnit body, List<bool[]> removed, out int count)
        {
            count = 0;
            var verts = new List<Vector3>();
            var m = ctx.RootTransform.localToWorldMatrix;
            for (int s = 0; s < body.Data.SubMeshes.Count; s++)
            {
                var idx = body.Data.SubMeshes[s].Indices;
                for (int t = 0; t < removed[s].Length; t++)
                {
                    if (!removed[s][t]) continue;
                    count++;
                    for (int k = 0; k < 3; k++) verts.Add(m.MultiplyPoint3x4(body.Data.Vertices[idx[t * 3 + k]]));
                }
            }
            var mesh = new Mesh { indexFormat = MeshData.IndexFormatFor(verts.Count), hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(verts);
            mesh.SetTriangles(Enumerable.Range(0, verts.Count).ToArray(), 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static void BuildDirections(Vector3 n, float angle, Vector3[] dirs)
        {
            var u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var w = Vector3.Cross(n, u);
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            dirs[0] = n;
            dirs[1] = n * c + u * s;
            dirs[2] = n * c - u * s;
            dirs[3] = n * c + w * s;
            dirs[4] = n * c - w * s;
        }

        /// <summary>三角形の一様グリッド。短い線分との交差判定に使う。</summary>
        private class TriangleGrid
        {
            private readonly float _cell;
            private readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();
            private readonly List<int> _large = new List<int>();
            private readonly List<Vector3> _tri = new List<Vector3>();
            private int[] _stamp = new int[0];
            private int _query;

            public TriangleGrid(float cell)
            {
                _cell = cell;
            }

            public void Add(Vector3 a, Vector3 b, Vector3 c)
            {
                int id = _tri.Count / 3;
                _tri.Add(a);
                _tri.Add(b);
                _tri.Add(c);
                var min = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                var max = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                long volume = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
                if (volume > 4096)
                {
                    _large.Add(id);
                    return;
                }
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    var key = new Vector3Int(x, y, z);
                    if (!_cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        _cells[key] = list;
                    }
                    list.Add(id);
                }
            }

            public bool SegmentHits(Vector3 p0, Vector3 p1)
            {
                if (_stamp.Length != _tri.Count / 3) _stamp = new int[_tri.Count / 3];
                _query++;
                var dir = p1 - p0;
                float len = dir.magnitude;
                if (len < 1e-8f) return false;
                dir /= len;

                foreach (var id in _large)
                    if (Intersects(id, p0, dir, len)) return true;

                var min = Cell(Vector3.Min(p0, p1));
                var max = Cell(Vector3.Max(p0, p1));
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                    foreach (var id in list)
                    {
                        if (_stamp[id] == _query) continue;
                        _stamp[id] = _query;
                        if (Intersects(id, p0, dir, len)) return true;
                    }
                }
                return false;
            }

            private Vector3Int Cell(Vector3 p)
            {
                return new Vector3Int(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));
            }

            // Möller–Trumbore (両面)
            private bool Intersects(int id, Vector3 origin, Vector3 dir, float maxT)
            {
                var a = _tri[id * 3];
                var e1 = _tri[id * 3 + 1] - a;
                var e2 = _tri[id * 3 + 2] - a;
                var p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) return false;
                float inv = 1f / det;
                var tv = origin - a;
                float u = Vector3.Dot(tv, p) * inv;
                if (u < 0 || u > 1) return false;
                var q = Vector3.Cross(tv, e1);
                float v = Vector3.Dot(dir, q) * inv;
                if (v < 0 || u + v > 1) return false;
                float t = Vector3.Dot(e2, q) * inv;
                return t >= 0 && t <= maxT;
            }
        }
    }
}
