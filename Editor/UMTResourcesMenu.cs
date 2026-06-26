using UMT;
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Editor menu (<c>Tools/UMT/Create Default Resources</c>) that creates and populates the default
    /// <see cref="UMTResources"/> asset from the package resource files.
    /// </summary>
    internal static class UMTResourcesMenu
    {
        /// <summary>Project folder that holds the UMT resource files and the generated resources asset.</summary>
        public const string k_DefaultFolder = "Packages/com.candidumgames.unitymmdtools/Resources";

        /// <summary>Project path of the default <see cref="UMTResources"/> asset.</summary>
        public const string k_DefaultAssetPath = k_DefaultFolder + "/UMTResources.asset";

        private const string k_PMXRenameListsPath = k_DefaultFolder + "/PMXRenameLists.json";
        private const string k_PMXHumanoidAvatarMappingsPath = k_DefaultFolder + "/PMXHumanoidAvatarMappings.json";
        private const string k_KawazuDictionaryFolder = k_DefaultFolder + "/Kawazu/IpaDic";
        private const string k_KawazuCharBinPath = k_KawazuDictionaryFolder + "/char.bytes";
        private const string k_KawazuMatrixBinPath = k_KawazuDictionaryFolder + "/matrix.bytes";
        private const string k_KawazuSysDicPath = k_KawazuDictionaryFolder + "/sys.bytes";
        private const string k_KawazuUnkDicPath = k_KawazuDictionaryFolder + "/unk.bytes";
        private const string k_PinyinDictionaryPath = k_DefaultFolder + "/Pinyin.txt";

        /// <summary>
        /// Creates the default <see cref="UMTResources"/> asset (if missing) and assigns the rename list, humanoid
        /// avatar mapping, Kawazu dictionary, and Pinyin dictionary references from the package resource files.
        /// </summary>
        [MenuItem("Tools/UMT/Create Default Resources")]
        public static void CreateDefaultResources()
        {
            EnsureDefaultFolders();
            AssetDatabase.Refresh();

            UMTResources umtResources = AssetDatabase.LoadAssetAtPath<UMTResources>(k_DefaultAssetPath);
            if (umtResources == null)
            {
                umtResources = ScriptableObject.CreateInstance<UMTResources>();
                AssetDatabase.CreateAsset(umtResources, k_DefaultAssetPath);
            }

            SerializedObject serializedObject = new SerializedObject(umtResources);
            SetObject(serializedObject, "m_PMXRenameListsJson", LoadRequiredAsset<TextAsset>(k_PMXRenameListsPath));
            SetObject(serializedObject, "m_PMXHumanoidAvatarMappingsJson", LoadRequiredAsset<TextAsset>(k_PMXHumanoidAvatarMappingsPath));
            SetObject(serializedObject, "m_KawazuCharBin", LoadRequiredAsset<TextAsset>(k_KawazuCharBinPath));
            SetObject(serializedObject, "m_KawazuMatrixBin", LoadRequiredAsset<TextAsset>(k_KawazuMatrixBinPath));
            SetObject(serializedObject, "m_KawazuSysDic", LoadRequiredAsset<TextAsset>(k_KawazuSysDicPath));
            SetObject(serializedObject, "m_KawazuUnkDic", LoadRequiredAsset<TextAsset>(k_KawazuUnkDicPath));
            SetObject(serializedObject, "m_PinyinDictionaryText", LoadRequiredAsset<TextAsset>(k_PinyinDictionaryPath));
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(umtResources);
            AssetDatabase.SaveAssets();
            Selection.activeObject = umtResources;
            EditorGUIUtility.PingObject(umtResources);
            Debug.Log($"Created UMT import resources: {k_DefaultAssetPath}");
        }

        private static void EnsureDefaultFolders()
        {
            EnsureFolder(k_DefaultFolder, "Kawazu");
            EnsureFolder(k_DefaultFolder + "/Kawazu", "IpaDic");
        }

        private static void EnsureFolder(string parentFolder, string folderName)
        {
            string folderPath = parentFolder + "/" + folderName;
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder(parentFolder, folderName);
            }
        }

        private static T LoadRequiredAsset<T>(string assetPath)
            where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset == null)
            {
                throw new FileNotFoundException($"Required UMT import resource asset was not found: {assetPath}", assetPath);
            }

            return asset;
        }

        private static void SetObject(SerializedObject serializedObject, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                throw new InvalidOperationException($"Serialized property was not found: {propertyName}");
            }

            property.objectReferenceValue = value;
        }
    }
}
