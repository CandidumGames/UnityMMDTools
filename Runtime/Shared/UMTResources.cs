using System;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// <see cref="ScriptableObject"/> holding serialized <see cref="TextAsset"/> references to the resources used by
    /// PMX import and romanization: rename lists, humanoid avatar mappings, Kawazu/NMeCab dictionary binaries, and the Pinyin dictionary.
    /// </summary>
    public sealed class UMTResources : ScriptableObject
    {
        [SerializeField] private TextAsset m_PMXRenameListsJson;
        [SerializeField] private TextAsset m_PMXHumanoidAvatarMappingsJson;
        [SerializeField] private TextAsset m_KawazuCharBin;
        [SerializeField] private TextAsset m_KawazuMatrixBin;
        [SerializeField] private TextAsset m_KawazuSysDic;
        [SerializeField] private TextAsset m_KawazuUnkDic;
        [SerializeField] private TextAsset m_PinyinDictionaryText;

        /// <summary>
        /// Gets the JSON <see cref="TextAsset"/> holding the PMX rename lists.
        /// </summary>
        public TextAsset pmxRenameListsJson => m_PMXRenameListsJson;

        /// <summary>
        /// Gets the JSON <see cref="TextAsset"/> holding the PMX humanoid avatar mappings.
        /// </summary>
        public TextAsset pmxHumanoidAvatarMappingsJson => m_PMXHumanoidAvatarMappingsJson;

        /// <summary>
        /// Gets the Kawazu character-definition binary <see cref="TextAsset"/>.
        /// </summary>
        public TextAsset kawazuCharBin => m_KawazuCharBin;

        /// <summary>
        /// Gets the Kawazu connection-matrix binary <see cref="TextAsset"/>.
        /// </summary>
        public TextAsset kawazuMatrixBin => m_KawazuMatrixBin;

        /// <summary>
        /// Gets the Kawazu system dictionary <see cref="TextAsset"/>.
        /// </summary>
        public TextAsset kawazuSysDic => m_KawazuSysDic;

        /// <summary>
        /// Gets the Kawazu unknown-word dictionary <see cref="TextAsset"/>.
        /// </summary>
        public TextAsset kawazuUnkDic => m_KawazuUnkDic;

        /// <summary>
        /// Gets the Pinyin dictionary <see cref="TextAsset"/>.
        /// </summary>
        public TextAsset pinyinDictionaryText => m_PinyinDictionaryText;

        /// <summary>
        /// Returns the PMX rename lists JSON text.
        /// </summary>
        /// <returns>The rename lists JSON content.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the rename lists asset is missing.</exception>
        public string GetPMXRenameListsJson()
        {
            return GetRequiredText(m_PMXRenameListsJson, nameof(m_PMXRenameListsJson));
        }

        /// <summary>
        /// Returns the PMX humanoid avatar mappings JSON text.
        /// </summary>
        /// <returns>The avatar mappings JSON content.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the avatar mappings asset is missing.</exception>
        public string GetPMXHumanoidAvatarMappingsJson()
        {
            return GetRequiredText(m_PMXHumanoidAvatarMappingsJson, nameof(m_PMXHumanoidAvatarMappingsJson));
        }

        /// <summary>
        /// Assembles the Kawazu dictionary binaries (char, matrix, system, and unknown dictionaries) into a single data bundle.
        /// </summary>
        /// <returns>The collected Kawazu dictionary data.</returns>
        /// <exception cref="InvalidOperationException">Thrown when any required Kawazu binary asset is missing or empty.</exception>
        public KawazuDictionaryData GetKawazuDictionaryData()
        {
            return new KawazuDictionaryData(
                GetRequiredBytes(m_KawazuCharBin, nameof(m_KawazuCharBin)),
                GetRequiredBytes(m_KawazuMatrixBin, nameof(m_KawazuMatrixBin)),
                GetRequiredBytes(m_KawazuSysDic, nameof(m_KawazuSysDic)),
                GetRequiredBytes(m_KawazuUnkDic, nameof(m_KawazuUnkDic)));
        }

        /// <summary>
        /// Returns the Pinyin dictionary text.
        /// </summary>
        /// <returns>The Pinyin dictionary content.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the Pinyin dictionary asset is missing.</exception>
        public string GetPinyinDictionaryText()
        {
            return GetRequiredText(m_PinyinDictionaryText, nameof(m_PinyinDictionaryText));
        }

        private static string GetRequiredText(TextAsset asset, string fieldName)
        {
            if (asset == null)
            {
                throw new InvalidOperationException($"{nameof(UMTResources)} is missing {fieldName}.");
            }

            return asset.text;
        }

        private static byte[] GetRequiredBytes(TextAsset asset, string fieldName)
        {
            if (asset == null)
            {
                throw new InvalidOperationException($"{nameof(UMTResources)} is missing {fieldName}.");
            }

            byte[] bytes = asset.bytes;
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException($"{nameof(UMTResources)} has an empty {fieldName}.");
            }

            return bytes;
        }
    }

    /// <summary>
    /// Immutable bundle of the raw Kawazu/NMeCab dictionary binaries required to initialize the Japanese romanizer.
    /// </summary>
    public sealed class KawazuDictionaryData
    {
        /// <summary>
        /// The Kawazu character-definition binary bytes.
        /// </summary>
        public readonly byte[] charBin;

        /// <summary>
        /// The Kawazu connection-matrix binary bytes.
        /// </summary>
        public readonly byte[] matrixBin;

        /// <summary>
        /// The Kawazu system dictionary binary bytes.
        /// </summary>
        public readonly byte[] systemDictionary;

        /// <summary>
        /// The Kawazu unknown-word dictionary binary bytes.
        /// </summary>
        public readonly byte[] unknownDictionary;

        /// <summary>
        /// Creates a dictionary data bundle from the four required Kawazu binaries.
        /// </summary>
        /// <param name="charBin">The character-definition binary bytes.</param>
        /// <param name="matrixBin">The connection-matrix binary bytes.</param>
        /// <param name="systemDictionary">The system dictionary binary bytes.</param>
        /// <param name="unknownDictionary">The unknown-word dictionary binary bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown when any of the supplied byte arrays is <c>null</c>.</exception>
        public KawazuDictionaryData(byte[] charBin, byte[] matrixBin, byte[] systemDictionary, byte[] unknownDictionary)
        {
            this.charBin = charBin ?? throw new ArgumentNullException(nameof(charBin));
            this.matrixBin = matrixBin ?? throw new ArgumentNullException(nameof(matrixBin));
            this.systemDictionary = systemDictionary ?? throw new ArgumentNullException(nameof(systemDictionary));
            this.unknownDictionary = unknownDictionary ?? throw new ArgumentNullException(nameof(unknownDictionary));
        }
    }
}
