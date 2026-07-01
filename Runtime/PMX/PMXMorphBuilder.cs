using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace UMT
{
    /// <summary>
    /// A group of PMX material indices linked together because a vertex morph affects all of them.
    /// </summary>
    public sealed class PMXMorphLinkedMaterialGroup
    {
        /// <summary>Material indices that belong to this morph-linked group.</summary>
        public readonly List<int> materialIndices = new List<int>();
    }

    /// <summary>
    /// Computes morph-linked material groups so that materials sharing a vertex morph are kept in the same generated mesh, using union-find over the vertices each morph affects.
    /// </summary>
    public static class PMXMorphBuilder
    {
        /// <summary>
        /// Groups materials that are connected by shared vertex morphs.
        /// </summary>
        /// <param name="model">PMX model providing materials, indices, and morphs.</param>
        /// <returns>The material groups, ordered by their lowest contained material index.</returns>
        public static List<PMXMorphLinkedMaterialGroup> BuildMorphLinkedMaterialGroups(PMXModel model)
        {
            int materialCount = model.materials.Length;
            int vertexCount = model.vertices.Length;

            // Flatten the boxed vertex-morph offsets into a blittable (vertexIndices, ranges) pair in managed code, then let Burst build the vertex->materials CSR and run union-find over it. This replaces the former Dictionary<uint, HashSet<int>> (one HashSet allocation per unique vertex).
            NativeArray<uint> indices = new NativeArray<uint>(model.indices, Allocator.Temp);
            NativeArray<int> materialFaceCounts = new NativeArray<int>(materialCount, Allocator.Temp);
            for (int i = 0; i < materialCount; ++i)
            {
                materialFaceCounts[i] = model.materials[i].faceIndexCount;
            }

            NativeList<uint> morphVertexIndices = new NativeList<uint>(Allocator.Temp);
            NativeList<int2> morphRanges = new NativeList<int2>(Allocator.Temp);
            FlattenVertexMorphs(model, morphVertexIndices, morphRanges);

            NativeArray<int> starts = new NativeArray<int>(vertexCount + 1, Allocator.Temp);
            NativeArray<int> flatMaterials = new NativeArray<int>(indices.Length, Allocator.Temp);
            NativeArray<int> parent = new NativeArray<int>(materialCount, Allocator.Temp);

            MorphGroupMath.BuildGroups(indices, materialFaceCounts, morphVertexIndices.AsArray(), morphRanges.AsArray(), vertexCount, ref starts, ref flatMaterials, ref parent);

            List<PMXMorphLinkedMaterialGroup> groups = BuildGroupsFromParent(parent, materialCount);

            indices.Dispose();
            materialFaceCounts.Dispose();
            morphVertexIndices.Dispose();
            morphRanges.Dispose();
            starts.Dispose();
            flatMaterials.Dispose();
            parent.Dispose();

            return groups;
        }

        /// <summary>
        /// Flattens every vertex-type morph into a shared list of affected vertex indices plus a per-morph <c>(start, count)</c> range, reading the boxed <see cref="PMXVertexMorphData"/> offsets once.
        /// </summary>
        private static void FlattenVertexMorphs(PMXModel model, NativeList<uint> morphVertexIndices, NativeList<int2> morphRanges)
        {
            foreach (PMXMorph morph in model.morphs)
            {
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                int start = morphVertexIndices.Length;
                for (int i = 0; i < morph.offsets.Length; ++i)
                {
                    PMXVertexMorphData offset = morph.offsets[i] as PMXVertexMorphData;
                    if (offset == null)
                    {
                        continue;
                    }

                    morphVertexIndices.Add(offset.vertexIndex);
                }

                morphRanges.Add(new int2(start, morphVertexIndices.Length - start));
            }
        }

        /// <summary>
        /// Builds the sorted material groups from the resolved union-find parent array.
        /// </summary>
        private static List<PMXMorphLinkedMaterialGroup> BuildGroupsFromParent(NativeArray<int> parent, int materialCount)
        {
            Dictionary<int, PMXMorphLinkedMaterialGroup> groupsByRoot = new Dictionary<int, PMXMorphLinkedMaterialGroup>();
            for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
            {
                int root = parent[materialIndex];
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

        /// <summary>
        /// Burst-compiled connectivity math: vertex-&gt;materials CSR plus morph-driven union-find.
        /// </summary>
        [BurstCompile]
        private static class MorphGroupMath
        {
            /// <summary>
            /// Builds the vertex-&gt;materials CSR from the face indices, unions the materials touched by each vertex morph, and fully path-compresses <paramref name="parent"/> so every entry holds its root.
            /// </summary>
            [BurstCompile]
            internal static void BuildGroups(in NativeArray<uint> indices, in NativeArray<int> materialFaceCounts, in NativeArray<uint> morphVertexIndices, in NativeArray<int2> morphRanges, int vertexCount, ref NativeArray<int> starts, ref NativeArray<int> flatMaterials, ref NativeArray<int> parent)
            {
                for (int i = 0; i < parent.Length; ++i)
                {
                    parent[i] = i;
                }

                // Count (vertex, material) incidences per vertex. The material of face index k is the running material range k falls into; duplicates are fine (union is idempotent).
                for (int i = 0; i <= vertexCount; ++i)
                {
                    starts[i] = 0;
                }
                int indexOffset = 0;
                for (int materialIndex = 0; materialIndex < materialFaceCounts.Length; ++materialIndex)
                {
                    int faceIndexCount = materialFaceCounts[materialIndex];
                    for (int i = 0; i < faceIndexCount; ++i)
                    {
                        int vertexIndex = (int)indices[indexOffset + i];
                        ++starts[vertexIndex + 1];
                    }
                    indexOffset += faceIndexCount;
                }

                // Prefix-sum counts into start offsets.
                for (int i = 0; i < vertexCount; ++i)
                {
                    starts[i + 1] += starts[i];
                }

                // Fill the flat material ids grouped by vertex using a moving cursor per vertex.
                NativeArray<int> cursor = new NativeArray<int>(vertexCount, Allocator.Temp);
                for (int i = 0; i < vertexCount; ++i)
                {
                    cursor[i] = starts[i];
                }
                indexOffset = 0;
                for (int materialIndex = 0; materialIndex < materialFaceCounts.Length; ++materialIndex)
                {
                    int faceIndexCount = materialFaceCounts[materialIndex];
                    for (int i = 0; i < faceIndexCount; ++i)
                    {
                        int vertexIndex = (int)indices[indexOffset + i];
                        flatMaterials[cursor[vertexIndex]++] = materialIndex;
                    }
                    indexOffset += faceIndexCount;
                }
                cursor.Dispose();

                // Union all materials touched by each vertex morph.
                for (int m = 0; m < morphRanges.Length; ++m)
                {
                    int2 range = morphRanges[m];
                    int firstMaterial = -1;
                    for (int v = 0; v < range.y; ++v)
                    {
                        int vertexIndex = (int)morphVertexIndices[range.x + v];
                        int spanStart = starts[vertexIndex];
                        int spanEnd = starts[vertexIndex + 1];
                        for (int s = spanStart; s < spanEnd; ++s)
                        {
                            int materialIndex = flatMaterials[s];
                            if (firstMaterial < 0)
                            {
                                firstMaterial = materialIndex;
                            }
                            else
                            {
                                Union(ref parent, firstMaterial, materialIndex);
                            }
                        }
                    }
                }

                // Flatten every entry to its root so managed grouping is a plain array read.
                for (int i = 0; i < parent.Length; ++i)
                {
                    parent[i] = Find(ref parent, i);
                }
            }

            private static int Find(ref NativeArray<int> parent, int value)
            {
                while (parent[value] != value)
                {
                    parent[value] = parent[parent[value]];
                    value = parent[value];
                }
                return value;
            }

            private static void Union(ref NativeArray<int> parent, int a, int b)
            {
                int rootA = Find(ref parent, a);
                int rootB = Find(ref parent, b);
                if (rootA != rootB)
                {
                    parent[rootB] = rootA;
                }
            }
        }
    }
}
