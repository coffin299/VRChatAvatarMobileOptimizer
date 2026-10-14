using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Coffi.MobileOptimizer
{
    /// <summary>生成アセットをすべて出力フォルダへ保存する。</summary>
    internal class AssetSaver
    {
        public string Folder { get; }

        public AssetSaver(string folder)
        {
            Folder = folder;
            EnsureFolder(folder);
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        public string UniquePath(string name, string ext)
        {
            return AssetDatabase.GenerateUniqueAssetPath($"{Folder}/{PathUtil.SanitizeFileName(name)}.{ext}");
        }

        public bool IsInOutput(Object obj)
        {
            if (obj == null || !AssetDatabase.Contains(obj)) return false;
            return AssetDatabase.GetAssetPath(obj).StartsWith(Folder + "/", StringComparison.Ordinal);
        }

        public T Save<T>(T obj, string name, string ext = "asset") where T : Object
        {
            if (obj == null || AssetDatabase.Contains(obj)) return obj;
            AssetDatabase.CreateAsset(obj, UniquePath(name, ext));
            return obj;
        }

        public void AddSubAsset(Object obj, Object mainAsset)
        {
            if (obj == null || AssetDatabase.Contains(obj)) return;
            AssetDatabase.AddObjectToAsset(obj, mainAsset);
        }

        public Texture2D SavePng(Texture2D tex, string name, Action<TextureImporter> configure)
        {
            var path = UniquePath(name, "png");
            File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                configure?.Invoke(importer);
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>メモリ上にしかない、または NDMF の一時フォルダにあるアセットか。</summary>
        public static bool IsTemporary(Object obj)
        {
            if (obj == null) return false;
            if (!AssetDatabase.Contains(obj)) return true;
            var path = AssetDatabase.GetAssetPath(obj);
            return path.StartsWith("Packages/nadena.dev.ndmf", StringComparison.Ordinal)
                   || path.Contains("__Generated")
                   || path.StartsWith("Assets/ZZZ_GeneratedAssets", StringComparison.Ordinal);
        }
    }
}
