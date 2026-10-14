# VRChat Avatar Mobile Optimizer

VRChat アバターを複製して、Mobile (Android / iOS / Quest) 向けに半自動で軽量化する Unity エディタ拡張です。
見た目より軽さ優先。元のアバターとアセットには手を加えません。

## できること

- 髪以外の PhysBone を削除し、残りを目標ランクの上限内に収める
- 服で隠れた素体のポリゴンを削除 (Scene ビューでプレビュー可)
- 使っていない BlendShape の焼き込み・削除
- 動かないボーンを親に統合 (MA で着せた服のボーンなど)
- メッシュを 1 つに統合し、テクスチャをアトラス化して `VRChat/Mobile/Toon Lit` 1 マテリアルに
- ポリゴン削減 (任意)
- Mobile 予想ランクを「元 / 見込み / 最適化後」で比較表示

## 必要なもの

- Unity 2022.3
- VRChat SDK3 Avatars
- NDMF / Modular Avatar

## 使い方

1. `Packages/com.coffi.vrchat-mobile-optimizer` をプロジェクトの `Packages/` に置く (または unitypackage をインポート)
2. メニュー `Tools > Mobile Optimizer` を開く
3. アバターを指定し、PhysBone の「残す / 削除」や目標ランクを確認
4. 「Mobile 版を生成」を押す

`Assets/MobileOptimized/<アバター名>_<日時>/` に Prefab と生成アセットが出力されます。
Blueprint ID は引き継がれるので、Android に切り替えてそのまま同じアバターとしてアップロードできます。
設定は `Assets/MobileOptimized/Settings/` にアバターごとに保存されます。

## 注意

- トグルで切り替える服は、統合すると常に表示になります (設定で「別メッシュとして残す」「削除」も選べます)
- アトラス化すると透過・ノーマルマップ・エミッションなどは失われます
- 予想ランクは目安です。最終確認は SDK のビルドパネルで

## unitypackage のビルド

```
tools\build-unitypackage.bat
```

`dist/` に出力されます (Unity 不要)。

## License

Apache License 2.0
