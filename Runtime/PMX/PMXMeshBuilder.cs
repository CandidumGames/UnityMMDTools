using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UMT
{
    /// <summary>
    /// Builds Unity meshes from a PMX model, producing one mesh per morph-linked material group with one submesh per contained material, plus bone weights, bindposes, and vertex-morph blend shapes.
    /// </summary>
    public static class PMXMeshBuilder
    {
        /// <summary>
        /// Builds one imported mesh per morph-linked material group.
        /// </summary>
        /// <param name="model">PMX model providing vertices, indices, materials, bones, and morphs.</param>
        /// <param name="modelName">Model name used as a prefix for generated mesh names.</param>
        /// <param name="materialGroups">Morph-linked material groups that define mesh boundaries.</param>
        /// <param name="bindposes">Bindpose matrices applied when the model has bones.</param>
        /// <returns>The generated meshes paired with their material indices.</returns>
        public static List<PMXImportedMesh> Build(PMXModel model, string modelName, IReadOnlyList<PMXMorphLinkedMaterialGroup> materialGroups, Matrix4x4[] bindposes)
        {
            List<PMXImportedMesh> meshes = new List<PMXImportedMesh>();
            int[] materialIndexOffsets = BuildMaterialIndexOffsets(model);

            // Copy the blittable source vertices once and share the native view with every generated mesh so the Burst per-vertex pass reads them without re-marshalling the managed array per group.
            NativeArray<PMXVertex> sourceVertices = new NativeArray<PMXVertex>(model.vertices, Allocator.Temp);

            foreach (PMXMorphLinkedMaterialGroup materialGroup in materialGroups)
            {
                string meshName = GetMeshName(model, modelName, materialGroup.materialIndices);
                PMXImportedMesh importedMesh = BuildMesh(model, sourceVertices, materialGroup.materialIndices, materialIndexOffsets, bindposes);
                importedMesh.mesh.name = meshName;
                importedMesh.materialIndices = materialGroup.materialIndices.ToArray();
                importedMesh.name = meshName;

                meshes.Add(importedMesh);
            }

            sourceVertices.Dispose();
            return meshes;
        }

        private static int[] BuildMaterialIndexOffsets(PMXModel model)
        {
            int[] offsets = new int[model.materials.Length];
            int indexOffset = 0;
            for (int i = 0; i < model.materials.Length; ++i)
            {
                offsets[i] = indexOffset;
                indexOffset += model.materials[i].faceIndexCount;
            }
            return offsets;
        }

        /// <summary>
        /// Derives a mesh name from the material in the group with the most faces (ties broken by lowest index), formatted as <c>Mesh_&lt;index&gt;_&lt;sanitizedMaterialName&gt;</c>.
        /// </summary>
        /// <param name="model">PMX model providing material data.</param>
        /// <param name="modelName">Model name (currently unused in the produced name).</param>
        /// <param name="materialIndices">Material indices contained in the group.</param>
        /// <returns>The generated mesh name.</returns>
        internal static string GetMeshName(PMXModel model, string modelName, IReadOnlyList<int> materialIndices)
        {
            int materialIndex = materialIndices[0];
            int faceIndexCount = model.materials[materialIndex].faceIndexCount;
            for (int i = 1; i < materialIndices.Count; ++i)
            {
                int candidateIndex = materialIndices[i];
                int candidateFaceIndexCount = model.materials[candidateIndex].faceIndexCount;
                if (candidateFaceIndexCount > faceIndexCount ||
                    (candidateFaceIndexCount == faceIndexCount && candidateIndex < materialIndex))
                {
                    materialIndex = candidateIndex;
                    faceIndexCount = candidateFaceIndexCount;
                }
            }

            return $"Mesh_{materialIndex:00}_{PMXUtilities.SanitizeFileName(model.materials[materialIndex].renamedName.ToString(), materialIndex)}";
        }

        private static PMXImportedMesh BuildMesh(PMXModel model, NativeArray<PMXVertex> sourceVertices, IReadOnlyList<int> materialIndices, int[] materialIndexOffsets, Matrix4x4[] bindposes)
        {
            Mesh mesh = new Mesh();
            Dictionary<uint, int> vertexMap = new Dictionary<uint, int>();
            List<uint> sourceVertexIndices = new List<uint>();
            NativeArray<int>[] submeshTriangles = new NativeArray<int>[materialIndices.Count];

            for (int materialGroupIndex = 0; materialGroupIndex < materialIndices.Count; ++materialGroupIndex)
            {
                int materialIndex = materialIndices[materialGroupIndex];
                int indexOffset = materialIndexOffsets[materialIndex];
                int faceIndexCount = model.materials[materialIndex].faceIndexCount;
                NativeArray<int> triangles = new NativeArray<int>(faceIndexCount, Allocator.Temp);
                for (int i = 0; i < faceIndexCount; ++i)
                {
                    uint sourceIndex = model.indices[indexOffset + i];
                    if (!vertexMap.TryGetValue(sourceIndex, out int remappedIndex))
                    {
                        remappedIndex = sourceVertexIndices.Count;
                        vertexMap.Add(sourceIndex, remappedIndex);
                        sourceVertexIndices.Add(sourceIndex);
                    }
                    triangles[i] = remappedIndex;
                }
                submeshTriangles[materialGroupIndex] = triangles;
            }

            int vertexCount = sourceVertexIndices.Count;
            NativeArray<uint> sourceIndexMap = new NativeArray<uint>(vertexCount, Allocator.Temp);
            for (int i = 0; i < vertexCount; ++i)
            {
                sourceIndexMap[i] = sourceVertexIndices[i];
            }

            NativeArray<Vector3> vertices = new NativeArray<Vector3>(vertexCount, Allocator.Temp);
            NativeArray<Vector3> normals = new NativeArray<Vector3>(vertexCount, Allocator.Temp);
            NativeArray<Vector2> uvs = new NativeArray<Vector2>(vertexCount, Allocator.Temp);

            // float3/float2 views alias the same buffers as the Vector3/Vector2 arrays uploaded to the mesh; they are held in locals so they can be passed to the Burst pass by ref.
            NativeArray<float3> positionsView = vertices.Reinterpret<float3>();
            NativeArray<float3> normalsView = normals.Reinterpret<float3>();
            NativeArray<float2> uvsView = uvs.Reinterpret<float2>();

            bool hasBones = model.bones.Length > 0;
            bool hasSDEF = false;
            NativeArray<byte> bonesPerVertex = default;
            NativeArray<BoneWeight1> boneWeights = default;
            NativeArray<SDEFVertexData> sdefData = default;

            if (hasBones)
            {
                bonesPerVertex = new NativeArray<byte>(vertexCount, Allocator.Temp);
                boneWeights = new NativeArray<BoneWeight1>(vertexCount * 4, Allocator.Temp);
                sdefData = new NativeArray<SDEFVertexData>(vertexCount, Allocator.Temp);
                MeshMath.BuildVertexData(in sourceVertices, in sourceIndexMap, model.bones.Length, ref positionsView, ref normalsView, ref uvsView, ref bonesPerVertex, ref boneWeights, ref sdefData, out int hasSDEFFlag);
                hasSDEF = hasSDEFFlag != 0;
            }
            else
            {
                MeshMath.BuildGeometry(in sourceVertices, in sourceIndexMap, ref positionsView, ref normalsView, ref uvsView);
            }

            if (vertexCount > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            // SDEF meshes are deformed by the GPU compute pass, which reads the bind-pose source mesh buffer via a raw GraphicsBuffer. Request raw access; the layout is left as Unity's default so the skinned output stream split (deformed attributes only) is preserved, and the actual strides and offsets are queried at runtime rather than forced.
            if (hasSDEF)
            {
                mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            }

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            vertices.Dispose();
            normals.Dispose();
            uvs.Dispose();
            sourceIndexMap.Dispose();

            mesh.subMeshCount = submeshTriangles.Length;
            for (int i = 0; i < submeshTriangles.Length; ++i)
            {
                mesh.SetIndices(submeshTriangles[i], MeshTopology.Triangles, i, false);
                submeshTriangles[i].Dispose();
            }

            if (hasBones)
            {
                mesh.SetBoneWeights(bonesPerVertex, boneWeights);
                mesh.bindposes = bindposes;
                bonesPerVertex.Dispose();
                boneWeights.Dispose();
            }

            string[] blendShapeNames = AddVertexMorphBlendShapes(mesh, model, vertexMap);

            mesh.RecalculateBounds();

            PMXImportedMesh importedMesh = new PMXImportedMesh
            {
                mesh = mesh,
                hasSDEF = hasSDEF,
                hasTangent = false,
            };

            if (hasSDEF)
            {
                importedMesh.sdefVertexData = sdefData.ToArray();
                importedMesh.morphTable = BuildMorphTable(model, vertexMap, vertexCount, blendShapeNames);
            }

            if (sdefData.IsCreated)
            {
                sdefData.Dispose();
            }

            return importedMesh;
        }

        /// <summary>
        /// Builds the compact vertex-morph table for an SDEF mesh: morph slots aligned to the mesh's blend-shape order, flattened per-vertex contributions, and per-vertex ranges. Contributions are collected in managed code (which owns the source-&gt;mesh vertex map), then bucketed by mesh vertex in a Burst CSR pass so no per-vertex native container is allocated.
        /// </summary>
        private static MMDMorphTable BuildMorphTable(PMXModel model, IReadOnlyDictionary<uint, int> vertexMap, int meshVertexCount, string[] blendShapeNames)
        {
            NativeList<int> contributionVertices = new NativeList<int>(Allocator.Temp);
            NativeList<uint4> contributionEntries = new NativeList<uint4>(Allocator.Temp);

            int morphSlot = 0;
            foreach (PMXMorph morph in model.morphs)
            {
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                bool contributed = false;
                for (int i = 0; i < morph.offsets.Length; ++i)
                {
                    PMXVertexMorphData offset = morph.offsets[i] as PMXVertexMorphData;
                    if (offset == null)
                    {
                        continue;
                    }

                    if (vertexMap.TryGetValue(offset.vertexIndex, out int meshVertexIndex))
                    {
                        contributionVertices.Add(meshVertexIndex);
                        contributionEntries.Add(new uint4(math.asuint(offset.positionOffset), (uint)morphSlot));
                        contributed = true;
                    }
                }

                // Advance the slot only for morphs that actually produced a blend shape on this mesh, so slot indices line up with blendShapeNames and the SkinnedMeshRenderer blend-shape order.
                if (contributed)
                {
                    ++morphSlot;
                }
            }

            NativeArray<uint4> flatOffsets = new NativeArray<uint4>(contributionEntries.Length, Allocator.Temp);
            NativeArray<int2> perVertexRanges = new NativeArray<int2>(meshVertexCount, Allocator.Temp);
            NativeArray<int> contributionVerticesArray = contributionVertices.AsArray();
            NativeArray<uint4> contributionEntriesArray = contributionEntries.AsArray();
            MeshMath.BucketMorphContributions(in contributionVerticesArray, in contributionEntriesArray, meshVertexCount, ref flatOffsets, ref perVertexRanges);

            MMDMorphTable result = new MMDMorphTable
            {
                blendShapeNames = blendShapeNames,
                flatOffsets = flatOffsets.ToArray(),
                perVertexRanges = perVertexRanges.ToArray(),
            };

            contributionVertices.Dispose();
            contributionEntries.Dispose();
            flatOffsets.Dispose();
            perVertexRanges.Dispose();

            return result;
        }

        /// <summary>
        /// Adds vertex-morph blend shapes to the mesh and returns the blend-shape names in the order they were added. The returned order matches the morph-slot order used by the SDEF morph table so the compute pass can pull per-slot weights from the <see cref="SkinnedMeshRenderer"/> by name. A single dense delta buffer is reused across morphs; only touched entries are cleared between morphs.
        /// </summary>
        private static string[] AddVertexMorphBlendShapes(Mesh mesh, PMXModel model, IReadOnlyDictionary<uint, int> vertexMap)
        {
            HashSet<string> usedShapeNames = new HashSet<string>(StringComparer.Ordinal);
            List<string> shapeNames = new List<string>();
            Vector3[] deltaVertices = new Vector3[vertexMap.Count];
            List<int> touchedIndices = new List<int>();
            foreach (PMXMorph morph in model.morphs)
            {
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                touchedIndices.Clear();
                bool hasAnyOffset = false;
                for (int i = 0; i < morph.offsets.Length; ++i)
                {
                    PMXVertexMorphData offset = morph.offsets[i] as PMXVertexMorphData;
                    if (offset == null)
                    {
                        continue;
                    }

                    if (vertexMap.TryGetValue(offset.vertexIndex, out int remappedIndex))
                    {
                        deltaVertices[remappedIndex] += (Vector3)offset.positionOffset;
                        touchedIndices.Add(remappedIndex);
                        hasAnyOffset = true;
                    }
                }

                if (hasAnyOffset)
                {
                    string shapeName = GetUniqueBlendShapeName(morph.renamedName.ToString(), usedShapeNames);
                    mesh.AddBlendShapeFrame(shapeName, 100.0f, deltaVertices, null, null);
                    shapeNames.Add(shapeName);

                    for (int i = 0; i < touchedIndices.Count; ++i)
                    {
                        deltaVertices[touchedIndices[i]] = Vector3.zero;
                    }
                }
            }
            return shapeNames.ToArray();
        }

        private static string GetUniqueBlendShapeName(string name, HashSet<string> usedShapeNames)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Morph" : name.Trim();
            if (usedShapeNames.Add(baseName))
            {
                return baseName;
            }

            for (int i = 1; ; ++i)
            {
                string candidate = $"{baseName}_{i:000}";
                if (usedShapeNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>
        /// Burst-compiled per-vertex mesh assembly: geometry extraction, Unity bone weights, SDEF vertex data, and morph-contribution bucketing. All inputs/outputs are blittable native containers.
        /// </summary>
        [BurstCompile]
        private static class MeshMath
        {
            /// <summary>
            /// Fills mesh-order positions, normals, and Y-flipped UVs from the source vertices.
            /// </summary>
            [BurstCompile]
            internal static void BuildGeometry(in NativeArray<PMXVertex> sourceVertices, in NativeArray<uint> sourceIndexMap, ref NativeArray<float3> positions, ref NativeArray<float3> normals, ref NativeArray<float2> uvs)
            {
                for (int i = 0; i < sourceIndexMap.Length; ++i)
                {
                    PMXVertex sourceVertex = sourceVertices[(int)sourceIndexMap[i]];
                    positions[i] = sourceVertex.position;
                    normals[i] = sourceVertex.normal;
                    uvs[i] = new float2(sourceVertex.uv.x, 1.0f - sourceVertex.uv.y);
                }
            }

            /// <summary>
            /// Fills geometry, Unity bone weights (4 influences per vertex), and SDEF vertex data in a single sweep, and reports whether any vertex uses SDEF weighting via <paramref name="hasSDEF"/>.
            /// </summary>
            [BurstCompile]
            internal static void BuildVertexData(in NativeArray<PMXVertex> sourceVertices, in NativeArray<uint> sourceIndexMap, int boneCount, ref NativeArray<float3> positions, ref NativeArray<float3> normals, ref NativeArray<float2> uvs, ref NativeArray<byte> bonesPerVertex, ref NativeArray<BoneWeight1> boneWeights, ref NativeArray<SDEFVertexData> sdefData, out int hasSDEF)
            {
                hasSDEF = 0;
                for (int i = 0; i < sourceIndexMap.Length; ++i)
                {
                    PMXVertex sourceVertex = sourceVertices[(int)sourceIndexMap[i]];
                    positions[i] = sourceVertex.position;
                    normals[i] = sourceVertex.normal;
                    uvs[i] = new float2(sourceVertex.uv.x, 1.0f - sourceVertex.uv.y);

                    PMXWeight weight = sourceVertex.weight;
                    ResolveLinearWeights(weight, boneCount, out int4 indices, out float4 weights, out int count);

                    bonesPerVertex[i] = 4;
                    int baseOffset = i * 4;
                    for (int j = 0; j < 4; ++j)
                    {
                        boneWeights[baseOffset + j] = new BoneWeight1
                        {
                            boneIndex = j < count ? indices[j] : 0,
                            weight = weights[j],
                        };
                    }

                    if (weight.type == PMXWeight.Type.SDEF
                        && weight.boneIndex0 >= 0 && weight.boneIndex0 < boneCount
                        && weight.boneIndex1 >= 0 && weight.boneIndex1 < boneCount)
                    {
                        hasSDEF = 1;
                        float w0 = weight.weight0;
                        MMDSDEFMath.CorrectSDEF(weight.sdefC, weight.sdefR0, weight.sdefR1, w0, out float3 r0p, out float3 r1p);
                        sdefData[i] = new SDEFVertexData
                        {
                            boneIndices = new int4(weight.boneIndex0, weight.boneIndex1, -1, -1),
                            boneWeights = new float4(w0, 1.0f - w0, 0.0f, 0.0f),
                            sdefC = new float4(weight.sdefC, 1.0f),
                            sdefR0 = new float4(r0p, 0.0f),
                            sdefR1 = new float4(r1p, 0.0f),
                        };
                    }
                    else
                    {
                        sdefData[i] = new SDEFVertexData
                        {
                            boneIndices = indices,
                            boneWeights = weights,
                            sdefC = float4.zero,
                            sdefR0 = float4.zero,
                            sdefR1 = float4.zero,
                        };
                    }
                }
            }

            /// <summary>
            /// Buckets flat morph contributions by mesh vertex into a CSR: <paramref name="perVertexRanges"/> holds each vertex's <c>(start, count)</c> range into the grouped <paramref name="flatOffsets"/>.
            /// </summary>
            [BurstCompile]
            internal static void BucketMorphContributions(in NativeArray<int> contributionVertices, in NativeArray<uint4> contributionEntries, int meshVertexCount, ref NativeArray<uint4> flatOffsets, ref NativeArray<int2> perVertexRanges)
            {
                NativeArray<int> starts = new NativeArray<int>(meshVertexCount + 1, Allocator.Temp);
                for (int i = 0; i < contributionVertices.Length; ++i)
                {
                    ++starts[contributionVertices[i] + 1];
                }
                for (int i = 0; i < meshVertexCount; ++i)
                {
                    starts[i + 1] += starts[i];
                }

                NativeArray<int> cursor = new NativeArray<int>(meshVertexCount, Allocator.Temp);
                for (int i = 0; i < meshVertexCount; ++i)
                {
                    cursor[i] = starts[i];
                    perVertexRanges[i] = new int2(starts[i], starts[i + 1] - starts[i]);
                }

                for (int i = 0; i < contributionVertices.Length; ++i)
                {
                    int vertexIndex = contributionVertices[i];
                    flatOffsets[cursor[vertexIndex]++] = contributionEntries[i];
                }

                starts.Dispose();
                cursor.Dispose();
            }

            /// <summary>
            /// Resolves up to four bone influences into a sorted (descending weight), normalized 4-wide result, matching Unity's skinning path: invalid/zero influences are dropped, and an empty set falls back to bone 0 with full weight. Unused slots are padded with index -1 and weight 0.
            /// </summary>
            private static void ResolveLinearWeights(in PMXWeight weight, int boneCount, out int4 outIndices, out float4 outWeights, out int count)
            {
                int4 idx = new int4(-1, -1, -1, -1);
                float4 wt = float4.zero;
                count = 0;
                if (weight.boneIndex0 >= 0 && weight.boneIndex0 < boneCount && weight.weight0 > 0.0f)
                {
                    idx[count] = weight.boneIndex0;
                    wt[count] = weight.weight0;
                    ++count;
                }
                if (weight.boneIndex1 >= 0 && weight.boneIndex1 < boneCount && weight.weight1 > 0.0f)
                {
                    idx[count] = weight.boneIndex1;
                    wt[count] = weight.weight1;
                    ++count;
                }
                if (weight.boneIndex2 >= 0 && weight.boneIndex2 < boneCount && weight.weight2 > 0.0f)
                {
                    idx[count] = weight.boneIndex2;
                    wt[count] = weight.weight2;
                    ++count;
                }
                if (weight.boneIndex3 >= 0 && weight.boneIndex3 < boneCount && weight.weight3 > 0.0f)
                {
                    idx[count] = weight.boneIndex3;
                    wt[count] = weight.weight3;
                    ++count;
                }

                // Insertion sort by descending weight (stable for equal weights), matching the managed path.
                for (int i = 1; i < count; ++i)
                {
                    int boneIndex = idx[i];
                    float boneWeight = wt[i];
                    int j = i - 1;
                    while (j >= 0 && wt[j] < boneWeight)
                    {
                        idx[j + 1] = idx[j];
                        wt[j + 1] = wt[j];
                        --j;
                    }
                    idx[j + 1] = boneIndex;
                    wt[j + 1] = boneWeight;
                }

                float sum = 0.0f;
                for (int i = 0; i < count; ++i)
                {
                    sum += wt[i];
                }

                if (count == 0 || sum <= 0.0f)
                {
                    idx = new int4(0, -1, -1, -1);
                    wt = new float4(1.0f, 0.0f, 0.0f, 0.0f);
                    count = 1;
                    sum = 1.0f;
                }

                outIndices = new int4(-1, -1, -1, -1);
                outWeights = float4.zero;
                for (int i = 0; i < 4; ++i)
                {
                    if (i < count)
                    {
                        outIndices[i] = idx[i];
                        outWeights[i] = wt[i] / sum;
                    }
                }
            }
        }
    }
}
