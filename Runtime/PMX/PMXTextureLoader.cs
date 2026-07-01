using B83.Image.BMP;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Loads PMX textures from disk into a texture-index map, decoding PNG/JPG via Unity and TGA/BMP via the bundled loaders, caching each resolved file so it is decoded only once.
    /// </summary>
    public static class PMXTextureLoader
    {
        /// <summary>
        /// Loads every PMX texture once into an array indexed by PMX texture index.
        /// </summary>
        /// <param name="model">PMX model providing relative texture paths.</param>
        /// <param name="options">Import options providing the texture base directory and source path.</param>
        /// <param name="result">Import result that collects created textures and warnings.</param>
        /// <returns>Textures indexed by PMX texture index; entries are null when a texture is missing or undecodable.</returns>
        public static Texture2D[] Load(PMXModel model, PMXImportOptions options, PMXImportResult result)
        {
            Texture2D[] texturesByIndex = new Texture2D[model.texturePaths.Length];

            Dictionary<string, Texture2D> loadedTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < model.texturePaths.Length; ++i)
            {
                texturesByIndex[i] = LoadTexture(i, model.texturePaths[i].ToString(), loadedTextures, options, result);
            }

            return texturesByIndex;
        }

        private static Texture2D LoadTexture(int index, string texturePath, Dictionary<string, Texture2D> loadedTextures, PMXImportOptions options, PMXImportResult result)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }

            string textureFullPath = ResolveTexturePath(texturePath, options);
            if (string.IsNullOrEmpty(textureFullPath))
            {
                PMXUtilities.AddWarning(result, $"Texture was not found: {texturePath}");
                return null;
            }

            if (loadedTextures.TryGetValue(textureFullPath, out Texture2D cachedTexture))
            {
                return cachedTexture;
            }

            byte[] textureBytes = File.ReadAllBytes(textureFullPath);
            Texture2D texture = DecodeTexture(textureFullPath, textureBytes);
            if (texture == null)
            {
                PMXUtilities.AddWarning(result, $"Texture could not be decoded: {textureFullPath}");
                return null;
            }

            texture.name = PMXUtilities.SanitizeFileName(Path.GetFileNameWithoutExtension(textureFullPath), index);
            result.textures.Add(texture);
            loadedTextures[textureFullPath] = texture;

            return texture;
        }

        private static Texture2D DecodeTexture(string textureFullPath, byte[] textureBytes)
        {
            string extension = Path.GetExtension(textureFullPath);
            if (string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase))
            {
                using MemoryStream stream = new MemoryStream(textureBytes, false);
                return ThirdParty.TGALoader.LoadTGA(stream);
            }

            if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                BMPImage bmp = new BMPLoader().LoadBMP(textureBytes);
                return bmp?.ToTexture2D();
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(texture, textureBytes))
            {
                return texture;
            }

            UnityEngine.Object.Destroy(texture);
            return null;
        }

        private static string ResolveTexturePath(string texturePath, PMXImportOptions options)
        {
            string baseDirectory = !string.IsNullOrEmpty(options.textureBaseDirectory) ? options.textureBaseDirectory : Path.GetDirectoryName(options.sourcePath);
            if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
            {
                return null;
            }

            string[] candidates = new[]
            {
                Path.Combine(baseDirectory, texturePath),
                Path.Combine(baseDirectory, texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)),
            };

            HashSet<string> visitedCandidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in candidates)
            {
                if (!visitedCandidates.Add(candidate))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            string fileName = Path.GetFileName(texturePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            foreach (string file in Directory.GetFiles(baseDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }
    }
}
