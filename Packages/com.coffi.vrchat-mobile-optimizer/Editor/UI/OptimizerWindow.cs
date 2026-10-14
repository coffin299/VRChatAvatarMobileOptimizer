using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

namespace Coffi.MobileOptimizer
{
    public class OptimizerWindow : EditorWindow
    {
        private VRCAvatarDescriptor _avatar;
        private OptimizerSettings _settings;
        private SerializedObject _so;
        private Vector2 _scroll;

        private AvatarStats _originalStats;
        private AvatarStats _estimateStats;
        private OptimizerResult _result;
        private readonly ReportView _reportView = new ReportView();

        private List<PhysBoneEntry> _physBones = new List<PhysBoneEntry>();
        private string[] _rendererPaths = new string[0];
        private string _previewInfo;

        private bool _foldSteps = true, _foldPhysBone = true, _foldHidden, _foldMesh, _foldAtlas, _foldDecimate, _foldSdk;

        private class PhysBoneEntry
        {
            public string Key;
            public bool Auto;
            public int Transforms;
            public VRCPhysBoneBase Component;
        }

        private static readonly string[] OverrideLabels = { "自動", "残す", "削除" };
        private static readonly string[] RankLabels = { "Good", "Medium", "Poor" };
        private static readonly PerfRank[] RankValues = { PerfRank.Good, PerfRank.Medium, PerfRank.Poor };

        [MenuItem("BAKAtool/AvatarBruteOptimizer")]
        public static void Open()
        {
            var window = GetWindow<OptimizerWindow>("AvatarBruteOptimizer");
            var selected = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>() : null;
            if (selected != null) window.SetAvatar(selected);
        }

        private void OnDisable()
        {
            HiddenMeshPreview.Clear();
        }

        private void SetAvatar(VRCAvatarDescriptor avatar)
        {
            _avatar = avatar;
            _result = null;
            _originalStats = null;
            _estimateStats = null;
            _previewInfo = null;
            HiddenMeshPreview.Clear();
            if (avatar == null)
            {
                _settings = null;
                _so = null;
                return;
            }
            _settings = OptimizerSettings.LoadOrCreate(avatar.gameObject.name);
            _so = new SerializedObject(_settings);
            RefreshAvatarInfo();
        }

        private void RefreshAvatarInfo()
        {
            if (_avatar == null || _settings == null) return;
            var root = _avatar.transform;
            _physBones = _avatar.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .Select(pb => new PhysBoneEntry
                {
                    Key = PhysBoneUtil.GetKey(pb, root),
                    Auto = PhysBoneUtil.IsHairCandidate(pb, root, _settings.hairNamePatterns),
                    Transforms = PhysBoneUtil.GetAffectedTransforms(pb).Count,
                    Component = pb,
                })
                .ToList();
            _rendererPaths = new[] { "(自動)" }
                .Concat(_avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => PathUtil.GetPath(r.transform, root)))
                .ToArray();
            RecalculateStats();
        }

        private void RecalculateStats()
        {
            if (_avatar == null) return;
            _originalStats = MobileRankCalculator.Calculate(_avatar.gameObject);
            _estimateStats = MobileRankCalculator.Estimate(_avatar.gameObject, _settings);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("AvatarBruteOptimizer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("元アバターを複製して Mobile 向けに軽量化します。元のアバター・アセットは変更しません。", MessageType.None);

            EditorGUI.BeginChangeCheck();
            var avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("アバター", _avatar, typeof(VRCAvatarDescriptor), true);
            if (EditorGUI.EndChangeCheck()) SetAvatar(avatar);

            if (_avatar == null || _so == null)
            {
                EditorGUILayout.HelpBox("シーン上の VRChat アバター (VRCAvatarDescriptor) を指定してください。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            _so.Update();
            EditorGUI.BeginChangeCheck();

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("設定ファイル", _settings, typeof(OptimizerSettings), false);
            DrawTargetRank();
            Prop("outputMode", "出力先", "新しいフォルダ: 毎回日時付きフォルダを作る / 上書き: 前回の出力フォルダを置き換える");

            DrawSteps();
            DrawPhysBones();
            DrawHidden();
            DrawMeshOptions();
            DrawAtlas();
            DrawDecimate();

            bool changed = EditorGUI.EndChangeCheck();
            _so.ApplyModifiedProperties();
            if (changed) _estimateStats = MobileRankCalculator.Estimate(_avatar.gameObject, _settings);

            EditorGUILayout.Space();
            DrawRankTable();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("ランクを再計算", GUILayout.Height(24))) RefreshAvatarInfo();
                GUI.backgroundColor = new Color(0.5f, 0.9f, 0.6f);
                if (GUILayout.Button("Mobile 版を生成", GUILayout.Height(32))) RunOptimizer();
                GUI.backgroundColor = Color.white;
            }

            if (_result != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("レポート", EditorStyles.boldLabel);
                _reportView.Draw(_result);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawTargetRank()
        {
            int index = Array.IndexOf(RankValues, _settings.targetRank);
            if (index < 0) index = 1;
            int newIndex = EditorGUILayout.Popup(new GUIContent("目標ランク",
                "PhysBone 削減・アトラスサイズ・ポリゴン削減の上限に使います。Mobile は既定で Medium 未満が非表示です。"), index, RankLabels);
            if (newIndex != index) _so.FindProperty("targetRank").intValue = (int)RankValues[newIndex];
        }

        private void DrawSteps()
        {
            _foldSteps = Section(_foldSteps, "処理ステップ");
            if (_foldSteps)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Prop("stripComponents", "コンポーネント削除", "髪以外の PhysBone・Contact・Mobile 非対応コンポーネントを削除");
                    Prop("trimPhysBones", "PhysBone を上限内に削減", "目標ランクの上限に収まるよう髪 PhysBone を統合・末端削減");
                    Prop("removeHiddenMesh", "隠れポリゴン削除", "服で隠れた素体のポリゴンを削除");
                    Prop("optimizeBlendShapes", "BlendShape 整理", "未使用の BlendShape を焼き込み・削除");
                    Prop("mergeBones", "ボーン統合", "動かないボーンのウェイトを親へ統合し、未使用ボーンを削除");
                    Prop("combineMeshes", "メッシュ統合", "SkinnedMeshRenderer を 1 つに統合");
                    Prop("atlasTextures", "テクスチャアトラス化", "全マテリアルを 1 枚のアトラス + Toon Lit 1 マテリアルに");
                    Prop("stripVertexAttributes", "不要な頂点属性の削除", "UV2-8・頂点カラー・タンジェントを削除");
                    Prop("decimate", "ポリゴン削減", "目標ランクのポリゴン上限まで削減 (見た目が変わります)");
                }
            }
        }

        private void DrawPhysBones()
        {
            _foldPhysBone = Section(_foldPhysBone, $"PhysBone ({_physBones.Count})");
            if (_foldPhysBone)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Prop("keepContacts", "Contact を残す", null);
                    Prop("hairNamePatterns", "髪と判定する名前", "PhysBone の GameObject 名・ルートとその親の名前に含まれていれば髪と判定します");
                    if (GUILayout.Button("判定を更新")) RefreshAvatarInfo();

                    int kept = 0, transforms = 0;
                    foreach (var e in _physBones)
                    {
                        if (e.Component == null) continue;
                        var ov = _settings.GetOverride(e.Key);
                        bool keep = ov ?? e.Auto;
                        if (keep) { kept++; transforms += e.Transforms; }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var style = new GUIStyle(EditorStyles.label) { normal = { textColor = keep ? new Color(0.4f, 0.85f, 0.5f) : Color.gray } };
                            if (GUILayout.Button(keep ? "残す" : "削除", style, GUILayout.Width(40))) EditorGUIUtility.PingObject(e.Component);
                            EditorGUILayout.LabelField(new GUIContent(e.Key, e.Key), GUILayout.MinWidth(120));
                            EditorGUILayout.LabelField($"{e.Transforms} bones", GUILayout.Width(70));
                            int current = ov == null ? 0 : ov.Value ? 1 : 2;
                            int next = EditorGUILayout.Popup(current, OverrideLabels, GUILayout.Width(60));
                            if (next != current)
                            {
                                Undo.RecordObject(_settings, "PhysBone Override");
                                _settings.SetOverride(e.Key, next == 0 ? (bool?)null : next == 1);
                                EditorUtility.SetDirty(_settings);
                                _so.Update();
                            }
                        }
                    }
                    EditorGUILayout.LabelField($"残す: {kept} 個 / {transforms} Transform", EditorStyles.miniBoldLabel);
                }
            }
        }

        private void DrawHidden()
        {
            _foldHidden = Section(_foldHidden, "隠れポリゴン削除");
            if (_foldHidden)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    var bodyProp = _so.FindProperty("bodyRendererPath");
                    int index = Mathf.Max(0, Array.IndexOf(_rendererPaths, bodyProp.stringValue));
                    int next = EditorGUILayout.Popup("素体メッシュ", index, _rendererPaths);
                    if (next != index) bodyProp.stringValue = next == 0 ? "" : _rendererPaths[next];
                    Prop("hiddenDistance", "判定距離 (m)", "素体の頂点からこの距離以内に服があれば隠れているとみなします");
                    Prop("hiddenRayAngle", "レイの広がり (度)", "法線から傾けた 4 方向にもレイを飛ばし、すべて服に当たった場合のみ削除します");
                    Prop("hiddenProtectedBones", "保護するボーン", "このボーン (と子) にウェイトがある頂点は削除しません");

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("削除予定の面をプレビュー")) RunPreview();
                        using (new EditorGUI.DisabledScope(!HiddenMeshPreview.IsActive))
                            if (GUILayout.Button("プレビューを消す")) { HiddenMeshPreview.Clear(); _previewInfo = null; }
                    }
                    if (_previewInfo != null) EditorGUILayout.HelpBox(_previewInfo, MessageType.Info);
                }
            }
        }

        private void DrawMeshOptions()
        {
            _foldMesh = Section(_foldMesh, "トグル・メッシュ統合");
            if (_foldMesh)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Prop("toggleMode", "トグル対象の扱い", "統合して常に表示 / 別メッシュとして残す (ランクが下がる) / トグル対象ごと削除");
                    Prop("deleteInactiveObjects", "初期非表示のトグルを削除", "アップロード時に非表示で、アニメーションで表示されるオブジェクトを削除します");
                    Prop("mergedMeshName", "統合メッシュ名", null);
                }
            }
        }

        private void DrawAtlas()
        {
            _foldAtlas = Section(_foldAtlas, "テクスチャアトラス");
            if (_foldAtlas)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    var size = _so.FindProperty("atlasSize");
                    size.intValue = EditorGUILayout.IntPopup("アトラスサイズ", size.intValue,
                        new[] { "512", "1024", "2048" }, new[] { 512, 1024, 2048 });
                    Prop("faceTextureBoost", "顔の解像度倍率", "頭ボーンに属するマテリアルにアトラスの面積を多めに割り当てます");
                    Prop("convertShaders", "アトラス化しない場合も Toon Lit に変換", null);
                }
            }
        }

        private void DrawDecimate()
        {
            _foldDecimate = Section(_foldDecimate, "ポリゴン削減");
            if (_foldDecimate)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Prop("decimateTargetTriangles", "目標ポリゴン数", $"0 なら目標ランクの上限から自動 (現在: {_settings.EffectiveDecimateTarget()})");
                    Prop("decimateProtectedBones", "保護するボーン", "このボーン (と子) にウェイトがある頂点は削減しません");
                }
            }
        }

        private void DrawRankTable()
        {
            EditorGUILayout.LabelField("Mobile 予想ランク", EditorStyles.boldLabel);
            if (_originalStats == null) RecalculateStats();
            var after = _result != null && _result.Success ? _result.Stats : null;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150));
                EditorGUILayout.LabelField("元", EditorStyles.miniBoldLabel, GUILayout.Width(110));
                EditorGUILayout.LabelField("見込み", EditorStyles.miniBoldLabel, GUILayout.Width(110));
                EditorGUILayout.LabelField("最適化後", EditorStyles.miniBoldLabel, GUILayout.Width(110));
            }
            var hints = new List<string>();
            foreach (StatCategory c in Enum.GetValues(typeof(StatCategory)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(MobileLimits.DisplayName(c), GUILayout.MinWidth(150));
                    Cell(_originalStats, c);
                    Cell(_estimateStats, c);
                    Cell(after, c);
                }
                var latest = after ?? _estimateStats;
                if (latest != null && latest.RankOf(c) > _settings.targetRank) hints.Add($"{MobileLimits.DisplayName(c)}: {Hint(c)}");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("総合", EditorStyles.boldLabel, GUILayout.MinWidth(150));
                OverallCell(_originalStats);
                OverallCell(_estimateStats);
                OverallCell(after);
            }

            foreach (var stats in new[] { _originalStats, after })
                if (stats != null)
                    foreach (var note in stats.Notes)
                        EditorGUILayout.HelpBox(note, MessageType.Warning);

            if (hints.Count > 0)
                EditorGUILayout.HelpBox($"目標ランク ({MobileLimits.DisplayName(_settings.targetRank)}) に届いていない項目:\n" + string.Join("\n", hints), MessageType.Warning);

            var sdk = after ?? _originalStats;
            if (sdk != null && sdk.SdkOverall != null)
            {
                _foldSdk = EditorGUILayout.Foldout(_foldSdk, $"VRChat SDK による判定 ({(after != null ? "最適化後" : "元")}): {sdk.SdkOverall}", true);
                if (_foldSdk)
                    using (new EditorGUI.IndentLevelScope())
                        foreach (var kv in sdk.SdkCategories)
                            EditorGUILayout.LabelField(kv.Key, kv.Value);
            }
        }

        private static void Cell(AvatarStats stats, StatCategory c)
        {
            if (stats == null || !stats.Values.TryGetValue(c, out var value))
            {
                EditorGUILayout.LabelField("-", GUILayout.Width(110));
                return;
            }
            var rank = stats.RankOf(c);
            var text = c == StatCategory.TextureMemoryMB ? value.ToString("0.0") : value.ToString("0");
            var style = new GUIStyle(EditorStyles.label) { normal = { textColor = RankColor(rank) } };
            EditorGUILayout.LabelField($"{text} ({Short(rank)})", style, GUILayout.Width(110));
        }

        private static void OverallCell(AvatarStats stats)
        {
            if (stats == null)
            {
                EditorGUILayout.LabelField("-", GUILayout.Width(110));
                return;
            }
            var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = RankColor(stats.Overall) } };
            EditorGUILayout.LabelField(MobileLimits.DisplayName(stats.Overall), style, GUILayout.Width(110));
        }

        private static string Short(PerfRank rank)
        {
            switch (rank)
            {
                case PerfRank.Excellent: return "Ex";
                case PerfRank.Good: return "Good";
                case PerfRank.Medium: return "Med";
                case PerfRank.Poor: return "Poor";
                default: return "VP";
            }
        }

        private static Color RankColor(PerfRank rank)
        {
            switch (rank)
            {
                case PerfRank.Excellent: return new Color(0.35f, 0.8f, 1f);
                case PerfRank.Good: return new Color(0.4f, 0.9f, 0.45f);
                case PerfRank.Medium: return new Color(0.95f, 0.85f, 0.3f);
                case PerfRank.Poor: return new Color(1f, 0.6f, 0.25f);
                default: return new Color(1f, 0.35f, 0.35f);
            }
        }

        private static string Hint(StatCategory c)
        {
            switch (c)
            {
                case StatCategory.Triangles: return "隠れポリゴン削除・ポリゴン削減を有効にするか、トグル対象を削除してください";
                case StatCategory.TextureMemoryMB: return "アトラス化を有効にするか、アトラスサイズを下げてください";
                case StatCategory.SkinnedMeshes:
                case StatCategory.BasicMeshes: return "メッシュ統合を有効にし、トグル対象を「別メッシュとして残す」以外にしてください";
                case StatCategory.MaterialSlots: return "テクスチャアトラス化を有効にしてください";
                case StatCategory.Bones: return "ボーン統合・PhysBone 削減を有効にしてください";
                case StatCategory.PhysBoneComponents:
                case StatCategory.PhysBoneTransforms:
                case StatCategory.PhysBoneColliders:
                case StatCategory.PhysBoneCollisionChecks: return "PhysBone 削減を有効にするか、残す PhysBone を減らしてください";
                case StatCategory.Contacts: return "「Contact を残す」をオフにしてください";
                case StatCategory.Constraints: return "Constraint を手動で減らしてください";
                case StatCategory.Animators: return "子オブジェクトの Animator を手動で削除してください";
                default: return "コンポーネント削除を有効にしてください";
            }
        }

        private static bool Section(bool open, string title)
        {
            EditorGUILayout.Space(2);
            return EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
        }

        private void Prop(string name, string label, string tooltip)
        {
            var p = _so.FindProperty(name);
            if (p != null) EditorGUILayout.PropertyField(p, new GUIContent(label, tooltip), true);
        }

        private void RunPreview()
        {
            AssetDatabase.SaveAssets();
            var mesh = OptimizerPipeline.PreviewHidden(_avatar.gameObject, _settings, out int count, out string body);
            if (mesh == null)
            {
                _previewInfo = "削除対象の面がありません (素体または服が見つかりません)";
                HiddenMeshPreview.Clear();
                return;
            }
            HiddenMeshPreview.Show(mesh);
            _previewInfo = $"素体 '{body}' の {count} ポリゴンを削除予定です (赤く表示)";
        }

        private void RunOptimizer()
        {
            HiddenMeshPreview.Clear();
            _previewInfo = null;
            AssetDatabase.SaveAssets();
            _result = OptimizerPipeline.Run(_avatar.gameObject, _settings);
            if (_result.Success)
                EditorGUIUtility.PingObject(_result.Prefab);
            GUIUtility.ExitGUI();
        }
    }
}
