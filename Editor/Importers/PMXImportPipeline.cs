using UMT;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Summary of the editor PMX import pipeline run, reporting the generated model name and component/asset counts.
    /// </summary>
    public sealed class PMXImportPipelineResult
    {
        /// <summary>Sanitized name assigned to the imported PMX model root and used for generated sub-assets.</summary>
        public string modelName;

        /// <summary>Number of generated meshes added to the imported asset.</summary>
        public int meshCount;

        /// <summary>Number of generated materials added to the imported asset.</summary>
        public int materialCount;

        /// <summary>Number of material slots resolved to external (remapped) materials instead of generated ones.</summary>
        public int remappedMaterialCount;

        /// <summary>Whether a humanoid avatar was created and added to the imported asset.</summary>
        public bool hasAvatar;

        /// <summary>Number of MMD bone transform components created on the imported root.</summary>
        public int mmdBoneComponentCount;

        /// <summary>Number of MMD IK controllers created on the imported root.</summary>
        public int mmdIKControllerCount;

        /// <summary>Number of MMD constraints created on the imported root.</summary>
        public int mmdConstraintCount;

        /// <summary>Number of PMX names renamed during import.</summary>
        public int renameCount;

        /// <summary>Warnings collected during the import.</summary>
        public List<string> warnings = new List<string>();
    }

    /// <summary>
    /// Editor import and post-processing pipeline for PMX assets: loads UMT resources and registers the imported
    /// objects (root, model, meshes, materials, avatar) with the asset import context.
    /// </summary>
    public static class PMXImportPipeline
    {
        /// <summary>
        /// Loads the single project-wide <see cref="UMTResources"/> asset required for PMX import.
        /// </summary>
        /// <returns>The loaded <see cref="UMTResources"/> asset.</returns>
        public static UMTResources LoadRequiredResources()
        {
            UMTResources defaultResources = AssetDatabase.LoadAssetAtPath<UMTResources>(UMTResourcesMenu.k_DefaultAssetPath);
            if (defaultResources != null)
            {
                return defaultResources;
            }

            string[] guids = AssetDatabase.FindAssets($"t:{nameof(UMTResources)}");
            if (guids.Length == 0)
            {
                throw new FileNotFoundException(
                    $"UMT import resources asset was not found. Run Tools/UMT/Create Default Import Resources to create {UMTResourcesMenu.k_DefaultAssetPath}.");
            }
            if (guids.Length > 1)
            {
                throw new InvalidOperationException(
                    "Multiple UMT import resources assets were found. Keep exactly one project-wide resource asset.");
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            UMTResources umtResources = AssetDatabase.LoadAssetAtPath<UMTResources>(assetPath);
            if (umtResources == null)
            {
                throw new FileNotFoundException($"UMT import resources asset could not be loaded: {assetPath}", assetPath);
            }

            return umtResources;
        }

        /// <summary>
        /// Registers the objects produced by a completed PMX import with the asset import context and, optionally,
        /// writes metadata and string-map debug files next to the source asset.
        /// </summary>
        /// <param name="importResult">Completed in-memory PMX import result to register.</param>
        /// <param name="pmxAssetPath">Project path of the source PMX asset.</param>
        /// <param name="ctx">Asset import context to add the generated objects to.</param>
        /// <param name="generateDebugData">When true, writes sibling metadata and string-map JSON debug files.</param>
        /// <returns>A summary of the import run.</returns>
        public static PMXImportPipelineResult Import(PMXImportResult importResult, string pmxAssetPath,
            AssetImportContext ctx, bool generateDebugData)
        {
            if (importResult == null)
            {
                throw new ArgumentNullException(nameof(importResult));
            }
            if (importResult.model == null)
            {
                throw new ArgumentException("PMX import result model is required.", nameof(importResult));
            }
            if (importResult.root == null)
            {
                throw new ArgumentException("PMX import result root is required.", nameof(importResult));
            }
            if (string.IsNullOrEmpty(pmxAssetPath))
            {
                throw new ArgumentException("PMX asset path is required.", nameof(pmxAssetPath));
            }
            if (ctx == null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }

            PMXImportPipelineResult result = new PMXImportPipelineResult();

            string modelName = GetAssetModelName(importResult.model, pmxAssetPath);
            modelName = PMXUtilities.SanitizeFileName(modelName, -1);

            string modelSubassetName = $"{modelName}_PMXModel";
            importResult.model.name = modelSubassetName;
            importResult.root.name = modelName;

            ctx.AddObjectToAsset(importResult.root.name, importResult.root);
            ctx.SetMainObject(importResult.root);

            ctx.AddObjectToAsset(importResult.model.name, importResult.model);

            foreach (PMXImportedMesh importedMesh in importResult.meshes)
            {
                importedMesh.mesh.name = importedMesh.name;
                ctx.AddObjectToAsset(importedMesh.mesh.name, importedMesh.mesh);
            }

            int remappedMaterialCount = 0;
            foreach (Material material in importResult.materials)
            {
                // External (remapped) materials are already standalone project assets; only embed generated ones.
                if (AssetDatabase.Contains(material))
                {
                    ++remappedMaterialCount;
                    continue;
                }
                ctx.AddObjectToAsset(material.name, material);
            }

            Avatar avatar = importResult.avatarResult?.avatar;
            if (avatar != null)
            {
                ctx.AddObjectToAsset(avatar.name, avatar);
            }

            if (generateDebugData)
            {
                string metadataJson = importResult.metadata != null ? importResult.metadata.ToJson() : "{}";
                string metadataPath = Path.ChangeExtension(pmxAssetPath, ".pmx.metadata.json");
                File.WriteAllText(metadataPath, metadataJson);

                string stringMapJson = PMXRenameUtilities.ToStringMapJson(importResult.renameResult);
                string stringMapPath = Path.ChangeExtension(pmxAssetPath, ".pmx.string-map.json");
                File.WriteAllText(stringMapPath, stringMapJson);
            }

            result.modelName = modelName;
            result.meshCount = importResult.meshes.Count;
            result.materialCount = importResult.materials.Count;
            result.remappedMaterialCount = remappedMaterialCount;
            result.hasAvatar = avatar != null;
            result.mmdBoneComponentCount = importResult.mmdTransformResult.boneComponentCount;
            result.mmdIKControllerCount = importResult.mmdTransformResult.ikControllerCount;
            result.mmdConstraintCount = importResult.mmdTransformResult.constraintCount;
            result.renameCount = importResult.renameResult?.totalRenameCount ?? 0;
            result.warnings.AddRange(importResult.warnings);

            return result;
        }

        private static string GetAssetModelName(PMXModel model, string pmxAssetPath)
        {
            string modelName = model.modelInfo.name.ToString();
            if (!string.IsNullOrEmpty(modelName))
            {
                return modelName;
            }
            string modelNameEN = model.modelInfo.nameEN.ToString();
            if (!string.IsNullOrEmpty(modelNameEN))
            {
                return modelNameEN;
            }
            return Path.GetFileNameWithoutExtension(pmxAssetPath);
        }
    }
}