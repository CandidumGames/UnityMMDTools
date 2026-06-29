using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Threading;
using UnityEngine;

namespace UMT
{
    /// <summary>In-memory result of a PMX import, containing the built Unity objects and all associated import data.</summary>
    public sealed class PMXImportResult
    {
        /// <summary>Root <see cref="GameObject"/> of the imported model hierarchy.</summary>
        public GameObject root;
        /// <summary>Parsed PMX model the result was built from.</summary>
        public PMXModel model;
        /// <summary>Result of the rename pass, or null when renames were not applied.</summary>
        public PMXRenameResult renameResult;
        /// <summary>Summary metadata built from the (renamed) model.</summary>
        public PMXMetadata metadata;
        /// <summary>Bone transforms in PMX bone order.</summary>
        public Transform[] bones = Array.Empty<Transform>();
        /// <summary>Loaded textures indexed by PMX texture index; entries may be null when a texture is missing.</summary>
        public Texture2D[] texturesByIndex = Array.Empty<Texture2D>();
        /// <summary>Result of optional humanoid avatar construction.</summary>
        public PMXAvatarBuildResult avatarResult;
        /// <summary>Result of MMD transform/physics runtime component construction.</summary>
        public MMDTransformBuildResult mmdTransformResult;
        /// <summary>Generated meshes, one per morph-linked material group.</summary>
        public readonly List<PMXImportedMesh> meshes = new List<PMXImportedMesh>();
        /// <summary>Generated materials in PMX material order.</summary>
        public readonly List<Material> materials = new List<Material>();
        /// <summary>Distinct textures created or referenced during import.</summary>
        public readonly List<Texture2D> textures = new List<Texture2D>();
        /// <summary>Non-fatal warnings accumulated during import.</summary>
        public readonly List<string> warnings = new List<string>();
    }

    /// <summary>A generated mesh paired with the PMX material indices that map to its submeshes.</summary>
    public sealed class PMXImportedMesh
    {
        /// <summary>The generated Unity mesh.</summary>
        public Mesh mesh;
        /// <summary>PMX material indices, one per submesh, in submesh order.</summary>
        public int[] materialIndices = Array.Empty<int>();
        /// <summary>Generated mesh name.</summary>
        public string name;
    }

    /// <summary>Result of optional humanoid avatar construction during import.</summary>
    public sealed class PMXAvatarBuildResult
    {
        /// <summary>True when a valid humanoid avatar was created.</summary>
        public bool hasHumanoidAvatar;
        /// <summary>The created humanoid avatar, or null if none was built.</summary>
        public Avatar avatar;
    }

    /// <summary>The single synchronous PMX import/build pipeline that turns PMX data into Unity objects.</summary>
    public static class PMXImporter
    {
        /// <summary>Reads a PMX file from disk, optionally renames, and builds the full Unity import result.</summary>
        /// <param name="pmxFilePath">Path to the source <c>.pmx</c> file.</param>
        /// <param name="options">Import options; a default instance is used when null.</param>
        /// <param name="cancellationToken">Token used to cancel the import.</param>
        /// <returns>The completed import result.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pmxFilePath"/> is null or empty.</exception>
        public static PMXImportResult Import(string pmxFilePath, PMXImportOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(pmxFilePath))
            {
                throw new ArgumentException("A PMX file path is required.", nameof(pmxFilePath));
            }

            options ??= new PMXImportOptions();
            options.sourcePath = pmxFilePath;

            cancellationToken.ThrowIfCancellationRequested();
            PMXModel model;
            using (UMTTiming.Measure(options.timingCallback, "Parse PMX"))
            {
                using FileStream stream = File.OpenRead(pmxFilePath);
                model = PMXReader.Read(stream, options.strictVersion);
            }

            return ImportModel(model, options, cancellationToken);
        }

        /// <summary>Reads PMX data from a byte buffer, optionally renames, and builds the full Unity import result.</summary>
        /// <param name="pmxBytes">Raw PMX file bytes.</param>
        /// <param name="options">Import options; a default instance is used when null.</param>
        /// <param name="cancellationToken">Token used to cancel the import.</param>
        /// <returns>The completed import result.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pmxBytes"/> is null or empty.</exception>
        public static PMXImportResult Import(byte[] pmxBytes, PMXImportOptions options = null, CancellationToken cancellationToken = default)
        {
            if (pmxBytes == null || pmxBytes.Length == 0)
            {
                throw new ArgumentException("PMX bytes are required.", nameof(pmxBytes));
            }

            options ??= new PMXImportOptions();

            cancellationToken.ThrowIfCancellationRequested();
            PMXModel model;
            using (UMTTiming.Measure(options.timingCallback, "Parse PMX"))
            {
                using MemoryStream stream = new MemoryStream(pmxBytes, false);
                model = PMXReader.Read(stream, options.strictVersion);
            }

            return ImportModel(model, options, cancellationToken);
        }

        /// <summary>
        /// Builds the Unity object graph (textures, materials, bones, meshes, renderers, and MMD runtime
        /// components) from an already-parsed PMX model, without parsing or renaming.
        /// </summary>
        /// <param name="model">Parsed PMX model to build from.</param>
        /// <param name="options">Import options; a default instance is used when null.</param>
        /// <returns>The import result containing the built Unity objects.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        public static PMXImportResult BuildUnityObjects(PMXModel model, PMXImportOptions options = null)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            options ??= new PMXImportOptions();

            PMXImportResult result = new PMXImportResult
            {
                model = model,
            };

            string modelName = PMXUtilities.GetModelName(model, options);
            result.root = new GameObject(modelName);
            if (options.parent != null)
            {
                result.root.transform.SetParent(options.parent, false);
            }

            List<PMXMorphLinkedMaterialGroup> materialGroups =
                PMXMorphBuilder.BuildMorphLinkedMaterialGroups(model);

            using (UMTTiming.Measure(options.timingCallback, "Load Textures"))
            {
                result.texturesByIndex = LoadTextures(model, options, result);
                foreach (Texture2D texture in result.texturesByIndex)
                {
                    if (texture != null && !result.textures.Contains(texture))
                    {
                        result.textures.Add(texture);
                    }
                }
            }

            using (UMTTiming.Measure(options.timingCallback, "Build Materials"))
            {
                result.materials.AddRange(PMXMaterialBuilder.Build(model, options, modelName, result.texturesByIndex));
            }

            Matrix4x4[] bindposes;
            using (UMTTiming.Measure(options.timingCallback, "Build Skeleton"))
            {
                result.bones = PMXBoneBuilder.BuildBones(model, result.root.transform);
                bindposes = PMXBoneBuilder.BuildBindposes(result.root.transform, result.bones);
            }

            if (options.createAvatar || result.mmdTransformResult.transformManager != null)
            {
                using (UMTTiming.Measure(options.timingCallback, "Build Avatar"))
                {
                    result.avatarResult = PMXAvatarBuilder.Build(model, result.root, result.bones, modelName, RequireResources(options, "build a humanoid avatar"));
                }
            }

            using (UMTTiming.Measure(options.timingCallback, "Build Meshes and Renderers"))
            {
                result.meshes.AddRange(PMXMeshBuilder.Build(model, modelName, materialGroups, bindposes));
                PMXRendererBuilder.Build(model, result.root, result.meshes, result.materials, result.bones);
            }

            using (UMTTiming.Measure(options.timingCallback, "Build Runtime"))
            {
                result.mmdTransformResult = MMDTransformBuilder.Build(model, result.root, result.bones);

                if (result.mmdTransformResult.transformManager != null)
                {
                    MMDTransformBuilder.RefreshInitialTransforms(result.mmdTransformResult.transformManager);
                }
            }

            return result;
        }

        /// <summary>
        /// Asynchronously builds the Unity object graph (textures, materials, bones, meshes, renderers, and MMD
        /// runtime components) from an already-parsed PMX model, without parsing or renaming, yielding control back
        /// to the Unity main thread according to <paramref name="frameBudget"/>.
        /// </summary>
        /// <param name="frameBudget">The frame budget used to yield control during long-running build phases.</param>
        /// <param name="model">Parsed PMX model to build from.</param>
        /// <param name="options">Import options; a default instance is used when null.</param>
        /// <returns>The import result containing the built Unity objects.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        public static async Awaitable<PMXImportResult> BuildUnityObjectsAsync(UMTFrameBudget frameBudget, PMXModel model, PMXImportOptions options = null)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            options ??= new PMXImportOptions();

            PMXImportResult result = new PMXImportResult
            {
                model = model,
            };

            string modelName = PMXUtilities.GetModelName(model, options);
            result.root = new GameObject(modelName);
            if (options.parent != null)
            {
                result.root.transform.SetParent(options.parent, false);
            }

            List<PMXMorphLinkedMaterialGroup> materialGroups =
                PMXMorphBuilder.BuildMorphLinkedMaterialGroups(model);
            await frameBudget.YieldIfNeeded();

            using (UMTTiming.Measure(options.timingCallback, "Load Textures"))
            {
                result.texturesByIndex = LoadTextures(model, options, result);
                foreach (Texture2D texture in result.texturesByIndex)
                {
                    if (texture != null && !result.textures.Contains(texture))
                    {
                        result.textures.Add(texture);
                    }
                }
            }
            await frameBudget.YieldIfNeeded();

            using (UMTTiming.Measure(options.timingCallback, "Build Materials"))
            {
                result.materials.AddRange(PMXMaterialBuilder.Build(model, options, modelName, result.texturesByIndex));
            }
            await frameBudget.YieldIfNeeded();

            Matrix4x4[] bindposes;
            using (UMTTiming.Measure(options.timingCallback, "Build Skeleton"))
            {
                result.bones = PMXBoneBuilder.BuildBones(model, result.root.transform);
                bindposes = PMXBoneBuilder.BuildBindposes(result.root.transform, result.bones);
            }
            await frameBudget.YieldIfNeeded();

            if (options.createAvatar || result.mmdTransformResult.transformManager != null)
            {
                using (UMTTiming.Measure(options.timingCallback, "Build Avatar"))
                {
                    result.avatarResult = PMXAvatarBuilder.Build(model, result.root, result.bones, modelName, RequireResources(options, "build a humanoid avatar"));
                }
                await frameBudget.YieldIfNeeded();
            }

            using (UMTTiming.Measure(options.timingCallback, "Build Meshes and Renderers"))
            {
                result.meshes.AddRange(PMXMeshBuilder.Build(model, modelName, materialGroups, bindposes));
                PMXRendererBuilder.Build(model, result.root, result.meshes, result.materials, result.bones);
            }
            await frameBudget.YieldIfNeeded();

            using (UMTTiming.Measure(options.timingCallback, "Build Runtime"))
            {
                result.mmdTransformResult = MMDTransformBuilder.Build(model, result.root, result.bones);

                if (result.mmdTransformResult.transformManager != null)
                {
                    MMDTransformBuilder.RefreshInitialTransforms(result.mmdTransformResult.transformManager);
                }
            }
            await frameBudget.YieldIfNeeded();

            return result;
        }

        private static Texture2D[] LoadTextures(PMXModel model, PMXImportOptions options, PMXImportResult result)
        {
            return options.loadTextures != null
                ? options.loadTextures(model, options)
                : PMXTextureLoader.Load(model, options, result);
        }

        private static PMXImportResult ImportModel(PMXModel model, PMXImportOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PMXRenameResult renameResult = null;
            if (options.applyRenames)
            {
                using (UMTTiming.Measure(options.timingCallback, "Rename"))
                {
                    UMTResources umtResources = RequireResources(options, "load PMX rename lists");
                    PMXRenameLists renameLists = options.renameLists;
                    if (renameLists == null)
                    {
                        renameLists = PMXRenameUtilities.LoadRenameLists(umtResources);
                    }

                    renameResult = PMXRenameUtilities.Rename(model, renameLists, umtResources);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            PMXImportResult result = BuildUnityObjects(model, options);
            result.renameResult = renameResult;

            using (UMTTiming.Measure(options.timingCallback, "Build Metadata"))
            {
                result.metadata = new PMXMetadata(model);
            }

            return result;
        }

        private static UMTResources RequireResources(PMXImportOptions options, string purpose)
        {
            if (options.umtResources == null)
            {
                throw new InvalidOperationException($"{nameof(PMXImportOptions)}.{nameof(PMXImportOptions.umtResources)} is required to {purpose}.");
            }

            return options.umtResources;
        }
    }
}