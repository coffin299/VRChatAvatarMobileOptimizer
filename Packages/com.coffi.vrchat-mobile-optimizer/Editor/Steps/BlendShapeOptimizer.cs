using System.Collections.Generic;
using UnityEngine;

namespace Coffi.MobileOptimizer
{
    /// <summary>
    /// アニメーション・Viseme・まばたきから参照されない BlendShape を、現在の値でベースメッシュに焼き込んでから削除する。
    /// </summary>
    internal static class BlendShapeOptimizer
    {
        public static void Run(OptimizerContext ctx)
        {
            foreach (var unit in ctx.Units)
            {
                var used = new HashSet<string>();
                for (int i = 0; i < unit.SourcePaths.Count; i++)
                {
                    used.UnionWith(ctx.Anim.BlendShapesOf(unit.SourcePaths[i]));
                    int id = unit.SourceRendererIds[i];
                    if (id == ctx.VisemeRendererId) used.UnionWith(ctx.VisemeBlendShapeNames);
                    if (id == ctx.EyelidRendererId)
                        foreach (var n in ctx.EyelidBlendShapeNames)
                            if (n != null) used.Add(n);
                }

                int baked = 0, removed = 0;
                var data = unit.Data;
                var keep = new List<BlendShape>();
                foreach (var shape in data.BlendShapes)
                {
                    if (used.Contains(shape.Name))
                    {
                        keep.Add(shape);
                        continue;
                    }
                    if (Mathf.Abs(shape.Weight) > 0.001f)
                    {
                        data.BakeBlendShape(shape, shape.Weight);
                        baked++;
                    }
                    else removed++;
                }
                data.BlendShapes = keep;
                if (baked + removed > 0)
                    ctx.Report.AddRemoved("BlendShape", $"{unit.Name}: 焼き込み {baked} / 削除 {removed} / 残り {keep.Count}");
            }
        }
    }
}
