using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// QEM (Quadric Error Metrics) によるポリゴン削減。片側の頂点へ寄せる half-edge collapse なので、
    /// ウェイト・UV・BlendShape は残る頂点の値をそのまま使える。
    /// UV の継ぎ目・メッシュの境界・保護ボーン (顔・手) の頂点は動かさない。
    /// </summary>
    internal static class Decimator
    {
        public static void Run(OptimizerContext ctx)
        {
            int target = ctx.Settings.EffectiveDecimateTarget();
            int total = ctx.Units.Sum(u => u.Data.TriangleCount);
            if (total <= target) return;

            float ratio = target / (float)total;
            var protectedBones = ctx.HumanBones(ctx.Settings.decimateProtectedBones).ToList();
            int after = 0;
            foreach (var unit in ctx.Units)
            {
                int before = unit.Data.TriangleCount;
                int unitTarget = Mathf.FloorToInt(before * ratio);
                var flags = unit.Data.BoneFlagsUnder(protectedBones);
                Simplify(unit.Data, unitTarget, flags);
                after += unit.Data.TriangleCount;
                ctx.Report.AddRemoved("ポリゴン削減", $"{unit.Name}: {before} → {unit.Data.TriangleCount}");
            }
            if (after > target)
                ctx.Report.Warn($"ポリゴン削減が目標 ({target}) に届きませんでした ({after})。保護ボーンや UV の継ぎ目が多い可能性があります");
        }

        public static void Simplify(MeshData d, int targetTris, bool[] protectedBones)
        {
            int n = d.VertexCount;
            var tris = new List<int>();
            var triSub = new List<int>();
            for (int s = 0; s < d.SubMeshes.Count; s++)
            {
                var idx = d.SubMeshes[s].Indices;
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    tris.Add(idx[i]);
                    tris.Add(idx[i + 1]);
                    tris.Add(idx[i + 2]);
                    triSub.Add(s);
                }
            }
            int triCount = triSub.Count;
            if (triCount <= targetTris) return;

            var locked = new bool[n];
            // UV の継ぎ目など、同じ位置に別の頂点がある頂点
            var byPos = new Dictionary<Vector3, int>();
            foreach (var p in d.Vertices) byPos[p] = byPos.TryGetValue(p, out int c) ? c + 1 : 1;
            for (int v = 0; v < n; v++)
                if (byPos[d.Vertices[v]] > 1 || d.HasWeightOn(v, protectedBones)) locked[v] = true;

            // 境界辺
            var edgeCount = new Dictionary<(int, int), int>();
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    var e = Edge(tris[t * 3 + k], tris[t * 3 + (k + 1) % 3]);
                    edgeCount[e] = edgeCount.TryGetValue(e, out int c) ? c + 1 : 1;
                }
            foreach (var kv in edgeCount)
                if (kv.Value == 1)
                {
                    locked[kv.Key.Item1] = true;
                    locked[kv.Key.Item2] = true;
                }

            var hasShape = new bool[n];
            foreach (var shape in d.BlendShapes)
                foreach (var f in shape.Frames)
                    for (int v = 0; v < n; v++)
                        if (!hasShape[v] && f.DeltaVertices[v].sqrMagnitude > 1e-12f) hasShape[v] = true;

            var q = new double[n, 10];
            var vt = new List<int>[n];
            for (int v = 0; v < n; v++) vt[v] = new List<int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                vt[a].Add(t);
                vt[b].Add(t);
                vt[c].Add(t);
                var normal = Vector3.Cross(d.Vertices[b] - d.Vertices[a], d.Vertices[c] - d.Vertices[a]);
                double area = normal.magnitude * 0.5;
                if (area < 1e-12) continue;
                normal.Normalize();
                double pa = normal.x, pb = normal.y, pc = normal.z, pd = -Vector3.Dot(normal, d.Vertices[a]);
                var plane = new[] { pa * pa, pa * pb, pa * pc, pa * pd, pb * pb, pb * pc, pb * pd, pc * pc, pc * pd, pd * pd };
                foreach (var v in new[] { a, b, c })
                    for (int k = 0; k < 10; k++) q[v, k] += plane[k] * area;
            }

            var dead = new bool[triCount];
            int alive = triCount;
            var candidates = new List<(double cost, int from, int to)>();
            for (int pass = 0; pass < 64 && alive > targetTris; pass++)
            {
                candidates.Clear();
                for (int t = 0; t < triCount; t++)
                {
                    if (dead[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int u = tris[t * 3 + k], v = tris[t * 3 + (k + 1) % 3];
                        if (!locked[u]) candidates.Add((Cost(q, u, v, d.Vertices[v]) * (hasShape[u] ? 10 : 1), u, v));
                        if (!locked[v]) candidates.Add((Cost(q, v, u, d.Vertices[u]) * (hasShape[v] ? 10 : 1), v, u));
                    }
                }
                if (candidates.Count == 0) break;
                candidates.Sort((x, y) => x.cost.CompareTo(y.cost));

                var touched = new bool[n];
                int collapsed = 0;
                foreach (var (_, from, to) in candidates)
                {
                    if (alive <= targetTris) break;
                    if (touched[from] || touched[to]) continue;
                    if (!CanCollapse(d, tris, dead, vt, from, to)) continue;

                    foreach (var t in vt[from])
                    {
                        if (dead[t]) continue;
                        bool hasTo = tris[t * 3] == to || tris[t * 3 + 1] == to || tris[t * 3 + 2] == to;
                        if (hasTo)
                        {
                            dead[t] = true;
                            alive--;
                            continue;
                        }
                        for (int k = 0; k < 3; k++)
                            if (tris[t * 3 + k] == from) tris[t * 3 + k] = to;
                        vt[to].Add(t);
                    }
                    vt[from].Clear();
                    for (int k = 0; k < 10; k++) q[to, k] += q[from, k];

                    touched[from] = true;
                    foreach (var t in vt[to])
                    {
                        if (dead[t]) continue;
                        touched[tris[t * 3]] = touched[tris[t * 3 + 1]] = touched[tris[t * 3 + 2]] = true;
                    }
                    collapsed++;
                }
                if (collapsed == 0) break;
            }

            foreach (var sm in d.SubMeshes) sm.Indices.Clear();
            for (int t = 0; t < triCount; t++)
            {
                if (dead[t]) continue;
                var idx = d.SubMeshes[triSub[t]].Indices;
                idx.Add(tris[t * 3]);
                idx.Add(tris[t * 3 + 1]);
                idx.Add(tris[t * 3 + 2]);
            }
            d.SubMeshes.RemoveAll(sm => sm.Indices.Count == 0);
            d.RemoveUnusedVertices();
        }

        private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        private static double Cost(double[,] q, int a, int b, Vector3 p)
        {
            double x = p.x, y = p.y, z = p.z;
            double Q(int k) => q[a, k] + q[b, k];
            return Q(0) * x * x + 2 * Q(1) * x * y + 2 * Q(2) * x * z + 2 * Q(3) * x
                   + Q(4) * y * y + 2 * Q(5) * y * z + 2 * Q(6) * y
                   + Q(7) * z * z + 2 * Q(8) * z
                   + Q(9);
        }

        /// <summary>from を to に寄せたとき、周囲の三角形が裏返ったり潰れたりしないか。</summary>
        private static bool CanCollapse(MeshData d, List<int> tris, bool[] dead, List<int>[] vt, int from, int to)
        {
            foreach (var t in vt[from])
            {
                if (dead[t]) continue;
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                if (a == to || b == to || c == to) continue;
                var before = Vector3.Cross(d.Vertices[b] - d.Vertices[a], d.Vertices[c] - d.Vertices[a]);
                var pa = a == from ? d.Vertices[to] : d.Vertices[a];
                var pb = b == from ? d.Vertices[to] : d.Vertices[b];
                var pc = c == from ? d.Vertices[to] : d.Vertices[c];
                var after = Vector3.Cross(pb - pa, pc - pa);
                if (after.sqrMagnitude < 1e-14f) return false;
                if (Vector3.Dot(before.normalized, after.normalized) < 0.3f) return false;
            }
            return true;
        }
    }
}
