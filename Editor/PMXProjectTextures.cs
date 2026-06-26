using UMT;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Editor texture loader that resolves a PMX model's texture paths to project <see cref="Texture2D"/> assets
    /// through the <see cref="AssetDatabase"/> for the editor import path.
    /// </summary>
    public static class PMXProjectTextures
    {
        /// <summary>
        /// Loads each PMX texture into a texture-index map of project <see cref="Texture2D"/> assets, resolving paths
        /// relative to the source asset directory and falling back to a recursive file-name search.
        /// </summary>
        /// <param name="model">PMX model providing the texture paths to resolve.</param>
        /// <param name="sourceAssetPath">Project path of the source PMX asset used as the texture lookup base.</param>
        /// <returns>An array of project textures indexed by PMX texture index; unresolved entries are <c>null</c>.</returns>
        public static Texture2D[] Load(PMXModel model, string sourceAssetPath)
        {
            HashSet<string> textureWarnings = new HashSet<string>(StringComparer.Ordinal);
            Texture2D[] texturesByIndex = new Texture2D[model.texturePaths.Length];
            for (int i = 0; i < model.texturePaths.Length; ++i)
            {
                texturesByIndex[i] = LoadTexture(model, i, sourceAssetPath, textureWarnings);
            }
            return texturesByIndex;
        }

        private static Texture2D LoadTexture(PMXModel model, int textureIndex, string sourceAssetPath, ISet<string> warnings)
        {
            if (textureIndex < 0 || textureIndex >= model.texturePaths.Length)
            {
                return null;
            }

            string texturePath = model.texturePaths[textureIndex].ToString();
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }

            string sourceDirectory = Path.GetDirectoryName(sourceAssetPath)?.Replace('\\', '/') ?? "Assets";
            string[] candidates = new[]
            {
                $"{sourceDirectory}/{texturePath}",
                $"{sourceDirectory}/{texturePath.Replace('\\', '/')}",
                $"{sourceDirectory}/{texturePath.Replace('/', '\\')}".Replace('\\', '/'),
            };

            HashSet<string> visitedCandidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in candidates)
            {
                if (!visitedCandidates.Add(candidate))
                {
                    continue;
                }

                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(candidate);
                if (texture != null)
                {
                    return texture;
                }
            }

            string fileName = Path.GetFileName(texturePath);
            if (string.IsNullOrEmpty(fileName))
            {
                AddTextureWarning(warnings, $"Texture path has no file name: {texturePath}");
                return null;
            }

            string sourceFullPath = Path.GetFullPath(sourceDirectory);
            if (!Directory.Exists(sourceFullPath))
            {
                AddTextureWarning(warnings, $"Texture source directory was not found: {sourceDirectory}");
                return null;
            }

            foreach (string file in Directory.GetFiles(sourceFullPath, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string assetPath = ToAssetPath(file);
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (texture != null)
                {
                    return texture;
                }
            }

            AddTextureWarning(warnings, $"Texture asset was not found: {texturePath} (source: {sourceAssetPath})");
            return null;
        }

        private static void AddTextureWarning(ISet<string> warnings, string message)
        {
            if (warnings != null && !warnings.Add(message))
            {
                return;
            }
            Debug.LogWarning($"[PMX Unity Import] {message}");
        }

        private static string ToAssetPath(string fullPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            return Path.GetFullPath(fullPath).Replace('\\', '/').Replace(projectRoot + "/", "");
        }
    }
}
