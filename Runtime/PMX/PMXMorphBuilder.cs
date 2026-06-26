using System.Collections.Generic;

namespace UMT
{
    /// <summary>A group of PMX material indices linked together because a vertex morph affects all of them.</summary>
    public sealed class PMXMorphLinkedMaterialGroup
    {
        /// <summary>Material indices that belong to this morph-linked group.</summary>
        public readonly List<int> materialIndices = new List<int>();
    }

    /// <summary>
    /// Computes morph-linked material groups so that materials sharing a vertex morph are kept in the same
    /// generated mesh, using union-find over the vertices each morph affects.
    /// </summary>
    public static class PMXMorphBuilder
    {
        /// <summary>Groups materials that are connected by shared vertex morphs.</summary>
        /// <param name="model">PMX model providing materials, indices, and morphs.</param>
        /// <returns>The material groups, ordered by their lowest contained material index.</returns>
        public static List<PMXMorphLinkedMaterialGroup> BuildMorphLinkedMaterialGroups(PMXModel model)
        {
            int materialCount = model.materials.Length;
            int[] parent = new int[materialCount];
            for (int i = 0; i < materialCount; ++i)
            {
                parent[i] = i;
            }
            Dictionary<uint, HashSet<int>> vertexMaterials = BuildVertexMaterials(model);

            foreach (PMXMorph morph in model.morphs)
            {
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                HashSet<int> affectedMaterials = new HashSet<int>();
                for (int offsetIndex = 0; offsetIndex < morph.offsets.Length; ++offsetIndex)
                {
                    PMXVertexMorphData offset = morph.offsets[offsetIndex] as PMXVertexMorphData;
                    if (offset == null)
                    {
                        continue;
                    }

                    if (!vertexMaterials.TryGetValue(offset.vertexIndex, out HashSet<int> materialIndices))
                    {
                        continue;
                    }

                    foreach (int materialIndex in materialIndices)
                    {
                        affectedMaterials.Add(materialIndex);
                    }
                }

                int firstMaterialIndex = -1;
                foreach (int materialIndex in affectedMaterials)
                {
                    if (firstMaterialIndex < 0)
                    {
                        firstMaterialIndex = materialIndex;
                        continue;
                    }

                    Union(parent, firstMaterialIndex, materialIndex);
                }
            }

            Dictionary<int, PMXMorphLinkedMaterialGroup> groupsByRoot = new Dictionary<int, PMXMorphLinkedMaterialGroup>();
            for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
            {
                int root = Find(parent, materialIndex);
                if (!groupsByRoot.TryGetValue(root, out PMXMorphLinkedMaterialGroup group))
                {
                    group = new PMXMorphLinkedMaterialGroup();
                    groupsByRoot.Add(root, group);
                }
                group.materialIndices.Add(materialIndex);
            }

            List<PMXMorphLinkedMaterialGroup> groups = new List<PMXMorphLinkedMaterialGroup>(groupsByRoot.Count);
            foreach (PMXMorphLinkedMaterialGroup group in groupsByRoot.Values)
            {
                groups.Add(group);
            }

            groups.Sort(CompareMaterialGroups);
            return groups;
        }

        private static int CompareMaterialGroups(PMXMorphLinkedMaterialGroup a, PMXMorphLinkedMaterialGroup b)
        {
            return a.materialIndices[0].CompareTo(b.materialIndices[0]);
        }

        private static Dictionary<uint, HashSet<int>> BuildVertexMaterials(PMXModel model)
        {
            Dictionary<uint, HashSet<int>> vertexMaterials = new Dictionary<uint, HashSet<int>>();
            int indexOffset = 0;
            for (int materialIndex = 0; materialIndex < model.materials.Length; ++materialIndex)
            {
                int faceIndexCount = model.materials[materialIndex].faceIndexCount;
                for (int i = 0; i < faceIndexCount; ++i)
                {
                    uint vertexIndex = model.indices[indexOffset + i];
                    if (!vertexMaterials.TryGetValue(vertexIndex, out HashSet<int> materialIndices))
                    {
                        materialIndices = new HashSet<int>();
                        vertexMaterials.Add(vertexIndex, materialIndices);
                    }
                    materialIndices.Add(materialIndex);
                }

                indexOffset += faceIndexCount;
            }

            return vertexMaterials;
        }

        private static int Find(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[rootB] = rootA;
            }
        }
    }
}
