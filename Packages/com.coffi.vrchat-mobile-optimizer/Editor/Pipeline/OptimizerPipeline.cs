using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Coffi.MobileOptimizer
{
    public class OptimizerResult
    {
        public GameObject Optimized;
        public GameObject Prefab;
        public string Folder;
        public OptimizerReport Report;
        public AvatarStats Stats;
        public bool Success => Optimized != null && Report.Error == null;
    }

    public static class OptimizerPipeline
    {
        public const string OutputRoot = "Assets/MobileOptimized";
        private const string ProgressTitle = "Mobile Optimizer";

        public static OptimizerResult Run(GameObject original, OptimizerSettings s)
        {
            var report = new OptimizerReport();
            var result = new OptimizerResult { Report = report };
            GameObject clone = null;
            try
            {
                var folder = PrepareOutputFolder(original.name, s.outputMode);
                result.Folder = folder;
                var saver = new AssetSaver(folder);

                Progress("複製と NDMF ベイク", 0.05f);
                clone = AvatarCloner.Clone(original, report, out var keys);
                var ctx = new OptimizerContext(original, clone, s, report, saver) { PhysBoneKeys = keys };
                Prepare(ctx);

                if (s.stripComponents)
                {
                    Progress("コンポーネント削除", 0.15f);
                    ComponentStripper.Run(ctx);
                }
                if (s.trimPhysBones)
                {
                    Progress("PhysBone 削減", 0.2f);
                    PhysBoneTrimmer.Run(ctx);
                }

                Progress("メッシュ読み込み", 0.25f);
                ctx.Units = MeshUnitBuilder.Build(ctx);

                if (s.removeHiddenMesh)
                {
                    Progress("隠れポリゴン削除", 0.35f);
                    HiddenMeshRemover.Run(ctx);
                }
                if (s.optimizeBlendShapes)
                {
                    Progress("BlendShape 整理", 0.45f);
                    BlendShapeOptimizer.Run(ctx);
                }
                if (s.mergeBones)
                {
                    Progress("ボーン統合", 0.5f);
                    BoneMerger.Run(ctx);
                }
                if (s.combineMeshes)
                {
                    Progress("メッシュ統合", 0.55f);
                    MeshCombiner.Run(ctx);
                }
                Progress("テクスチャアトラス化", 0.6f);
                if (s.atlasTextures) MaterialAtlasser.Run(ctx);
                else if (s.convertShaders) MaterialAtlasser.ConvertOnly(ctx);
                if (s.stripVertexAttributes) VertexAttributeStripper.Run(ctx);
                if (s.decimate)
                {
                    Progress("ポリゴン削減", 0.7f);
                    Decimator.Run(ctx);
                }

                Progress("メッシュ書き出し", 0.8f);
                MeshWriter.Write(ctx);
                TransformCleaner.Run(ctx);

                Progress("アニメーション修正", 0.85f);
                var cloner = new AnimatorDeepCloner();
                AnimationPathFixer.Run(ctx, cloner);
                AssetLocalizer.Run(ctx, cloner);
                cloner.SaveAll(saver, clone.name);

                Progress("保存", 0.95f);
                AssetDatabase.SaveAssets();
                result.Prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(clone, $"{folder}/{PathUtil.SanitizeFileName(clone.name)}.prefab",
                    InteractionMode.AutomatedAction);

                Undo.RegisterCreatedObjectUndo(clone, "Mobile Optimize");
                Undo.RecordObject(original, "Mobile Optimize");
                original.SetActive(false);

                result.Optimized = clone;
                result.Stats = MobileRankCalculator.Calculate(clone);
                Selection.activeGameObject = clone;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Error = e.Message;
                if (clone != null) Object.DestroyImmediate(clone);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return result;
        }

        /// <summary>隠れポリゴンのプレビュー。一時的な複製で判定し、削除予定の面をワールド空間のメッシュとして返す。</summary>
        public static Mesh PreviewHidden(GameObject original, OptimizerSettings s, out string message)
        {
            GameObject clone = null;
            try
            {
                Progress("プレビュー計算", 0.3f);
                var report = new OptimizerReport();
                clone = AvatarCloner.Clone(original, report, out var keys);
                var ctx = new OptimizerContext(original, clone, s, report, null) { PhysBoneKeys = keys };
                Prepare(ctx);
                ctx.Units = MeshUnitBuilder.Build(ctx);
                var body = HiddenMeshRemover.FindBody(ctx);
                if (body == null)
                {
                    message = "素体メッシュが見つかりません。「素体メッシュ」を指定してください";
                    return null;
                }
                var occluders = HiddenMeshRemover.GetOccluders(ctx, body);
                if (occluders.Count == 0)
                {
                    message = s.clothingRendererPaths.Count > 0
                        ? "指定した服メッシュが見つかりません (非表示のため削除された可能性があります)"
                        : "服メッシュが見つかりません。「服メッシュ」を指定してください";
                    return null;
                }
                var removed = HiddenMeshRemover.ComputeRemovedTriangles(ctx, body);
                var mesh = HiddenMeshRemover.BuildPreviewMesh(ctx, body, removed, out int count);
                var names = string.Join(", ", occluders.Select(u => u.Name));
                message = $"素体: {body.SourcePaths[0]}\n服 ({occluders.Count}): {names}\n削除予定: {count} ポリゴン (赤く表示)";
                if (count == 0)
                {
                    Object.DestroyImmediate(mesh);
                    return null;
                }
                return mesh;
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                EditorUtility.ClearProgressBar();
            }
        }

        private static void Prepare(OptimizerContext ctx)
        {
            ctx.Anim = AnimationAnalyzer.Analyze(ctx.Root);
            ctx.CaptureDescriptorInfo();
            ComponentStripper.RemoveEditorOnly(ctx);
            ComponentStripper.HandleRenderers(ctx);
        }

        private static string PrepareOutputFolder(string avatarName, OutputMode mode)
        {
            var name = PathUtil.SanitizeFileName(avatarName);
            string folder;
            if (mode == OutputMode.Overwrite)
            {
                folder = $"{OutputRoot}/{name}";
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
            else
            {
                folder = $"{OutputRoot}/{name}_{DateTime.Now:yyyyMMdd_HHmmss}";
            }
            AssetSaver.EnsureFolder(folder);
            return folder;
        }

        private static void Progress(string message, float progress)
        {
            EditorUtility.DisplayProgressBar(ProgressTitle, message, progress);
        }
    }
}
