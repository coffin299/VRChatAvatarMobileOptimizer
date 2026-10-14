namespace Coffi.MobileOptimizer
{
    /// <summary>Toon Lit が使わない頂点属性 (UV1-7, 頂点カラー, タンジェント) を削除する。</summary>
    internal static class VertexAttributeStripper
    {
        public static void Run(OptimizerContext ctx)
        {
            foreach (var unit in ctx.Units)
            {
                var d = unit.Data;
                for (int ch = 1; ch < 8; ch++)
                {
                    d.UVs[ch] = null;
                    d.UVDimensions[ch] = 0;
                }
                d.Colors = null;
                d.Tangents = null;
                foreach (var shape in d.BlendShapes)
                    foreach (var f in shape.Frames)
                        f.DeltaTangents = null;
            }
        }
    }
}
