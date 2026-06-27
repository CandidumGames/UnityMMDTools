using UMT;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>How the PMX importer obtains the materials assigned to the generated renderers.</summary>
    public enum PMXMaterialCreationMode
    {
        /// <summary>Generate and embed a material per PMX material (the default, original behavior).</summary>
        Standard,
        /// <summary>Allow per-slot external materials, remapped through the importer's external object map.</summary>
        Override,
    }

    /// <summary>
    /// Draws the "Materials" tab of <see cref="PMXScriptedImporter"/>: the creation-mode dropdown, the
    /// per-slot remapped-material list (in <see cref="PMXMaterialCreationMode.Override"/> mode), and the
    /// "Extract Materials..." action. Remaps are stored in the importer's native external object map.
    /// </summary>
    public sealed class PMXMaterialRemapEditor
    {
        /// <summary>A single remappable material slot: its sanitized key and the original MMD display name.</summary>
        private readonly struct MaterialSlot
        {
            public readonly string slotKey;
            public readonly string displayName;

            public MaterialSlot(string slotKey, string displayName)
            {
                this.slotKey = slotKey;
                this.displayName = displayName;
            }
        }

        private SerializedProperty m_MaterialCreationModeProp;

        /// <summary>Caches the serialized properties shown on the Materials tab.</summary>
        public void OnEnable(SerializedObject serializedObject)
        {
            m_MaterialCreationModeProp = serializedObject.FindProperty("m_MaterialCreationMode");
        }

        /// <summary>Draws the Materials tab for the given importer.</summary>
        /// <param name="importer">The PMX scripted importer being inspected.</param>
        /// <param name="serializedObject">The importer's serialized object (used to commit the mode field).</param>
        public void OnGUI(PMXScriptedImporter importer, SerializedObject serializedObject)
        {
            SerializedProperty modeProp = m_MaterialCreationModeProp;
            EditorGUILayout.PropertyField(modeProp, new GUIContent("Material Creation Mode"));
            // Commit the mode change before any direct AddRemap/RemoveRemap edits below so a later
            // ApplyModifiedProperties cannot clobber the external object map.
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            if (GUILayout.Button("Extract Materials..."))
            {
                ExtractMaterials(importer, serializedObject, modeProp);
            }

            if ((PMXMaterialCreationMode)modeProp.enumValueIndex != PMXMaterialCreationMode.Override)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Remapped Materials", EditorStyles.boldLabel);

            List<MaterialSlot> slots = EnumerateSlots(importer.assetPath);
            if (slots.Count == 0)
            {
                EditorGUILayout.HelpBox("No materials were found. Reimport the model to populate the slots.", MessageType.Info);
                return;
            }

            Dictionary<AssetImporter.SourceAssetIdentifier, Object> externalMap = importer.GetExternalObjectMap();
            foreach (MaterialSlot slot in slots)
            {
                AssetImporter.SourceAssetIdentifier id = new AssetImporter.SourceAssetIdentifier(typeof(Material), slot.slotKey);
                externalMap.TryGetValue(id, out Object existing);
                Material current = existing as Material;

                EditorGUI.BeginChangeCheck();
                Material next = (Material)EditorGUILayout.ObjectField(slot.displayName, current, typeof(Material), false);
                if (EditorGUI.EndChangeCheck())
                {
                    if (next != null)
                    {
                        importer.AddRemap(id, next);
                    }
                    else
                    {
                        importer.RemoveRemap(id);
                    }
                }
            }
        }

        /// <summary>Builds the ordered material slot list from the imported model's <see cref="PMXModel"/> sub-asset.</summary>
        private static List<MaterialSlot> EnumerateSlots(string assetPath)
        {
            List<MaterialSlot> slots = new List<MaterialSlot>();
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            PMXModel model = null;
            foreach (Object asset in assets)
            {
                if (asset is PMXModel pmxModel)
                {
                    model = pmxModel;
                    break;
                }
            }

            if (model == null)
            {
                return slots;
            }

            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial pmxMaterial = model.materials[i];
                string slotKey = PMXUtilities.SanitizeFileName(pmxMaterial.renamedName.ToString(), i);
                string displayName = pmxMaterial.originalName.ToString();
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = pmxMaterial.originalNameEN.ToString();
                }
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = slotKey;
                }
                slots.Add(new MaterialSlot(slotKey, displayName));
            }

            return slots;
        }

        /// <summary>
        /// Extracts the embedded generated materials into standalone <c>.mat</c> assets under a chosen folder,
        /// switches the importer to <see cref="PMXMaterialCreationMode.Override"/>, and reimports so the
        /// extracted materials are used through the external object map.
        /// </summary>
        private static void ExtractMaterials(PMXScriptedImporter importer, SerializedObject serializedObject, SerializedProperty modeProp)
        {
            string assetPath = importer.assetPath;
            string defaultFolder = Path.GetDirectoryName(assetPath);
            string absoluteFolder = EditorUtility.SaveFolderPanel("Extract Materials", defaultFolder, string.Empty);
            if (string.IsNullOrEmpty(absoluteFolder))
            {
                return;
            }

            string projectFolder = FileUtil.GetProjectRelativePath(absoluteFolder);
            if (string.IsNullOrEmpty(projectFolder) || !projectFolder.StartsWith("Assets"))
            {
                EditorUtility.DisplayDialog("Extract Materials", "Choose a folder inside the project's Assets folder.", "OK");
                return;
            }

            modeProp.enumValueIndex = (int)PMXMaterialCreationMode.Override;
            serializedObject.ApplyModifiedProperties();

            // The embedded material's name is its slot key (PMXMaterialBuilder names each material with the
            // same sanitized key the importer uses for override lookup), so capture it before extraction.
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            List<(string slotKey, string assetPath)> extracted = new List<(string, string)>();
            foreach (Object asset in assets)
            {
                if (!(asset is Material material))
                {
                    continue;
                }

                string slotKey = material.name;
                string destinationPath = AssetDatabase.GenerateUniqueAssetPath($"{projectFolder}/{material.name}.mat");
                string error = AssetDatabase.ExtractAsset(material, destinationPath);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogError($"[PMX Import] Failed to extract material '{material.name}': {error}");
                    return;
                }
                extracted.Add((slotKey, destinationPath));
            }

            // Remap each slot to its extracted material so the reimport resolves the override map to the
            // standalone .mat files instead of regenerating embedded materials and orphaning the extracted ones.
            foreach ((string slotKey, string materialPath) in extracted)
            {
                Material extractedMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (extractedMaterial == null)
                {
                    Debug.LogError($"[PMX Import] Failed to load extracted material at '{materialPath}'.");
                    return;
                }

                AssetImporter.SourceAssetIdentifier id = new AssetImporter.SourceAssetIdentifier(typeof(Material), slotKey);
                importer.AddRemap(id, extractedMaterial);
            }

            AssetDatabase.WriteImportSettingsIfDirty(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            // The reimport invalidates the current inspector state; restart the GUI for a clean redraw.
            GUIUtility.ExitGUI();
        }
    }
}
