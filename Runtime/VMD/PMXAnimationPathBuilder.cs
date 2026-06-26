using System;
using System.Collections.Generic;

namespace UMT
{
    /// <summary>
    /// Precomputed Unity animation target paths for a PMX model: transform paths for bones and the
    /// <see cref="UnityEngine.SkinnedMeshRenderer"/> paths affected by each morph.
    /// </summary>
    internal sealed class PMXAnimationPaths
    {
        /// <summary>Transform path from the model root to each bone, indexed by bone index.</summary>
        public string[] bonePaths = Array.Empty<string>();

        /// <summary>Renderer paths affected by each morph, indexed by morph index. Empty for morphs that target no renderer.</summary>
        public string[][] morphRendererPaths = Array.Empty<string[]>();
    }

    /// <summary>
    /// Builds <see cref="PMXAnimationPaths"/> (bone transform paths and per-morph renderer paths) from a
    /// <see cref="PMXModel"/>, matching the names and mesh grouping used when the model is imported.
    /// </summary>
    internal static class PMXAnimationPathBuilder
    {
        /// <summary>
        /// Builds the bone and morph renderer animation paths for the given model.
        /// </summary>
        /// <param name="model">The PMX model to build paths for.</param>
        /// <returns>The computed <see cref="PMXAnimationPaths"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        public static PMXAnimationPaths Build(PMXModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            return new PMXAnimationPaths
            {
                bonePaths = BuildBonePaths(model),
                morphRendererPaths = BuildMorphRendererPaths(model),
            };
        }

        private static string[] BuildBonePaths(PMXModel model)
        {
            string[] names = new string[model.bones.Length];
            string[] paths = new string[model.bones.Length];
            for (int i = 0; i < model.bones.Length; ++i)
            {
                names[i] = PMXUtilities.GetGeneratedObjectName(
                    model.bones[i].renamedName.ToString(),
                    "Bone",
                    i);
            }

            for (int i = 0; i < model.bones.Length; ++i)
            {
                Stack<string> parts = new Stack<string>();
                int boneIndex = i;
                while (boneIndex >= 0)
                {
                    if (boneIndex >= model.bones.Length)
                    {
                        throw new InvalidOperationException(
                            $"PMX bone {i} has invalid parent index {boneIndex}.");
                    }

                    parts.Push(names[boneIndex]);
                    boneIndex = model.bones[boneIndex].parentBoneIndex;
                }

                paths[i] = string.Join("/", parts);
            }

            return paths;
        }

        private static string[][] BuildMorphRendererPaths(PMXModel model)
        {
            string[][] paths = new string[model.morphs.Length][];
            for (int i = 0; i < paths.Length; ++i)
            {
                paths[i] = Array.Empty<string>();
            }
            if (model.materials.Length == 0)
            {
                return paths;
            }

            List<PMXMorphLinkedMaterialGroup> groups =
                PMXMorphBuilder.BuildMorphLinkedMaterialGroups(model);
            HashSet<uint>[] verticesByGroup = BuildVerticesByGroup(model, groups);
            string modelName = model.modelInfo.name.ToString();
            if (string.IsNullOrEmpty(modelName))
            {
                modelName = model.modelInfo.nameEN.ToString();
            }

            for (int morphIndex = 0; morphIndex < model.morphs.Length; ++morphIndex)
            {
                PMXMorph morph = model.morphs[morphIndex];
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                List<string> rendererPaths = new List<string>();
                for (int groupIndex = 0; groupIndex < groups.Count; ++groupIndex)
                {
                    if (!AffectsGroup(morph, verticesByGroup[groupIndex]))
                    {
                        continue;
                    }

                    rendererPaths.Add(PMXMeshBuilder.GetMeshName(
                        model,
                        modelName,
                        groups[groupIndex].materialIndices));
                }

                paths[morphIndex] = rendererPaths.ToArray();
            }

            return paths;
        }

        private static HashSet<uint>[] BuildVerticesByGroup(
            PMXModel model,
            IReadOnlyList<PMXMorphLinkedMaterialGroup> groups)
        {
            int[] materialOffsets = new int[model.materials.Length];
            int offset = 0;
            for (int i = 0; i < model.materials.Length; ++i)
            {
                materialOffsets[i] = offset;
                offset += model.materials[i].faceIndexCount;
            }

            HashSet<uint>[] result = new HashSet<uint>[groups.Count];
            for (int groupIndex = 0; groupIndex < groups.Count; ++groupIndex)
            {
                HashSet<uint> vertices = new HashSet<uint>();
                foreach (int materialIndex in groups[groupIndex].materialIndices)
                {
                    int start = materialOffsets[materialIndex];
                    int count = model.materials[materialIndex].faceIndexCount;
                    for (int i = 0; i < count; ++i)
                    {
                        vertices.Add(model.indices[start + i]);
                    }
                }

                result[groupIndex] = vertices;
            }

            return result;
        }

        private static bool AffectsGroup(PMXMorph morph, HashSet<uint> vertices)
        {
            for (int i = 0; i < morph.offsets.Length; ++i)
            {
                PMXVertexMorphData offset = morph.offsets[i] as PMXVertexMorphData;
                if (offset != null && vertices.Contains(offset.vertexIndex))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
