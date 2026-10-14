using System.Collections.Generic;
using System.Linq;

namespace Coffi.MobileOptimizer
{
    /// <summary>「別メッシュとして残す」以外のメッシュを 1 つに統合する。</summary>
    internal static class MeshCombiner
    {
        public static void Run(OptimizerContext ctx)
        {
            var targets = ctx.Units.Where(u => !u.KeepSeparate).ToList();
            if (targets.Count <= 1)
            {
                if (targets.Count == 1) targets[0].Name = ctx.Settings.mergedMeshName;
                return;
            }

            // 素体を先頭にすると、Viseme などの BlendShape の並び順が元に近くなる
            var body = HiddenMeshRemover.FindBody(ctx);
            targets = targets.OrderByDescending(u => u == body).ToList();

            var merged = new MeshUnit
            {
                Name = ctx.Settings.mergedMeshName,
                Data = targets[0].Data,
            };
            foreach (var u in targets)
            {
                merged.SourceRenderers.AddRange(u.SourceRenderers);
                merged.SourcePaths.AddRange(u.SourcePaths);
                merged.SourceRendererIds.AddRange(u.SourceRendererIds);
                merged.Toggleable |= u.Toggleable;
            }
            for (int i = 1; i < targets.Count; i++) merged.Data.Append(targets[i].Data);

            var units = new List<MeshUnit> { merged };
            units.AddRange(ctx.Units.Where(u => u.KeepSeparate));
            ctx.Units = units;
            ctx.Report.Info($"メッシュ {targets.Count} 個を '{merged.Name}' に統合しました ({merged.Data.TriangleCount} ポリゴン, BlendShape {merged.Data.BlendShapes.Count} 個)");
        }
    }
}
