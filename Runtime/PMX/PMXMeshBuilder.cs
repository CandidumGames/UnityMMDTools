using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UMT
{
    /// <summary>
    /// Builds Unity meshes from a PMX model, producing one mesh per morph-linked material group with one
    /// submesh per contained material, plus bone weights, bindposes, and vertex-morph blend shapes.
    /// </summary>
    public static class PMXMeshBuilder
    {
        /// <summary>Builds one imported mesh per morph-linked material group.</summary>
        /// <param name="model">PMX model providing vertices, indices, materials, bones, and morphs.</param>
        /// <param name="modelName">Model name used as a prefix for generated mesh names.</param>
        /// <param name="materialGroups">Morph-linked material groups that define mesh boundaries.</param>
        /// <param name="bindposes">Bindpose matrices applied when the model has bones.</param>
        /// <returns>The generated meshes paired with their material indices.</returns>
        public static List<PMXImportedMesh> Build(PMXModel model, string modelName, IReadOnlyList<PMXMorphLinkedMaterialGroup> materialGroups, Matrix4x4[] bindposes)
        {
            List<PMXImportedMesh> meshes = new List<PMXImportedMesh>();
            int[] materialIndexOffsets = BuildMaterialIndexOffsets(model);
            foreach (PMXMorphLinkedMaterialGroup materialGroup in materialGroups)
            {
                string meshName = GetMeshName(model, modelName, materialGroup.materialIndices);
                Mesh mesh = BuildMesh(model, materialGroup.materialIndices, materialIndexOffsets, bindposes);
                mesh.name = meshName;

                meshes.Add(new PMXImportedMesh
                {
                    mesh = mesh,
                    materialIndices = materialGroup.materialIndices.ToArray(),
                    name = meshName,
                });
            }
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
        /// Derives a mesh name from the material in the group with the most faces (ties broken by lowest index),
        /// formatted as <c>Mesh_&lt;index&gt;_&lt;sanitizedMaterialName&gt;</c>.
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

        private static Mesh BuildMesh(PMXModel model, IReadOnlyList<int> materialIndices, int[] materialIndexOffsets, Matrix4x4[] bindposes)
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

            if (sourceVertexIndices.Count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            NativeArray<Vector3> vertices = new NativeArray<Vector3>(sourceVertexIndices.Count, Allocator.Temp);
            NativeArray<Vector3> normals = new NativeArray<Vector3>(sourceVertexIndices.Count, Allocator.Temp);
            NativeArray<Vector2> uvs = new NativeArray<Vector2>(sourceVertexIndices.Count, Allocator.Temp);
            for (int i = 0; i < sourceVertexIndices.Count; ++i)
            {
                PMXVertex sourceVertex = model.vertices[sourceVertexIndices[i]];
                vertices[i] = ToUnityVector3(sourceVertex.position);
                normals[i] = ToUnityVector3(sourceVertex.normal);
                uvs[i] = ToUnityUV(sourceVertex.uv);
            }

            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            vertices.Dispose();
            normals.Dispose();
            uvs.Dispose();

            mesh.subMeshCount = submeshTriangles.Length;
            for (int i = 0; i < submeshTriangles.Length; ++i)
            {
                mesh.SetIndices(submeshTriangles[i], MeshTopology.Triangles, i, false);
                submeshTriangles[i].Dispose();
            }

            if (model.bones.Length > 0)
            {
                SetBoneWeights(mesh, model, sourceVertexIndices);
                mesh.bindposes = bindposes;
            }

            AddVertexMorphBlendShapes(mesh, model, vertexMap);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static void SetBoneWeights(Mesh mesh, PMXModel model, IReadOnlyList<uint> sourceVertexIndices)
        {
            NativeArray<byte> bonesPerVertex = new NativeArray<byte>(sourceVertexIndices.Count, Allocator.Temp);
            NativeArray<BoneWeight1> boneWeights = new NativeArray<BoneWeight1>(sourceVertexIndices.Count * 4, Allocator.Temp);
            for (int i = 0; i < sourceVertexIndices.Count; ++i)
            {
                bonesPerVertex[i] = 4;
                FillUnityBoneWeights(model.vertices[sourceVertexIndices[i]].weight, model.bones.Length, boneWeights, i * 4);
            }

            mesh.SetBoneWeights(bonesPerVertex, boneWeights);
            bonesPerVertex.Dispose();
            boneWeights.Dispose();
        }

        private static void FillUnityBoneWeights(PMXWeight weight, int boneCount, NativeArray<BoneWeight1> boneWeights, int outputOffset)
        {
            int[] indices = new int[4];
            float[] weights = new float[4];
            int count = 0;
            if (weight.boneIndex0 >= 0 && weight.boneIndex0 < boneCount && weight.weight0 > 0)
            {
                AddWeight(indices, weights, ref count, weight.boneIndex0, weight.weight0);
            }
            if (weight.boneIndex1 >= 0 && weight.boneIndex1 < boneCount && weight.weight1 > 0)
            {
                AddWeight(indices, weights, ref count, weight.boneIndex1, weight.weight1);
            }
            if (weight.boneIndex2 >= 0 && weight.boneIndex2 < boneCount && weight.weight2 > 0)
            {
                AddWeight(indices, weights, ref count, weight.boneIndex2, weight.weight2);
            }
            if (weight.boneIndex3 >= 0 && weight.boneIndex3 < boneCount && weight.weight3 > 0)
            {
                AddWeight(indices, weights, ref count, weight.boneIndex3, weight.weight3);
            }

            SortWeightsDescending(indices, weights, count);
            float sum = 0.0f;
            for (int i = 0; i < count; ++i)
            {
                sum += weights[i];
            }

            if (count == 0 || sum <= 0.0f)
            {
                indices[0] = 0;
                weights[0] = 1.0f;
                count = 1;
                sum = 1.0f;
            }

            for (int i = 0; i < 4; ++i)
            {
                boneWeights[outputOffset + i] = new BoneWeight1
                {
                    boneIndex = i < count ? indices[i] : 0,
                    weight = i < count ? weights[i] / sum : 0.0f,
                };
            }
        }

        private static void AddWeight(int[] indices, float[] weights, ref int count, int boneIndex, float weight)
        {
            if (count >= 4)
            {
                return;
            }

            indices[count] = boneIndex;
            weights[count] = weight;
            ++count;
        }

        private static void SortWeightsDescending(int[] indices, float[] weights, int count)
        {
            for (int i = 1; i < count; ++i)
            {
                int boneIndex = indices[i];
                float weight = weights[i];
                int j = i - 1;
                while (j >= 0 && weights[j] < weight)
                {
                    indices[j + 1] = indices[j];
                    weights[j + 1] = weights[j];
                    --j;
                }

                indices[j + 1] = boneIndex;
                weights[j + 1] = weight;
            }
        }

        private static void AddVertexMorphBlendShapes(Mesh mesh, PMXModel model, IReadOnlyDictionary<uint, int> vertexMap)
        {
            HashSet<string> usedShapeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (PMXMorph morph in model.morphs)
            {
                if (morph.type != PMXMorph.Type.Vertex)
                {
                    continue;
                }

                Vector3[] deltaVertices = new Vector3[vertexMap.Count];
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
                        deltaVertices[remappedIndex] += ToUnityVector3(offset.positionOffset);
                        hasAnyOffset = true;
                    }
                }

                if (hasAnyOffset)
                {
                    string shapeName = GetUniqueBlendShapeName(morph.renamedName.ToString(), usedShapeNames);
                    mesh.AddBlendShapeFrame(shapeName, 100.0f, deltaVertices, null, null);
                }
            }
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

        private static Vector3 ToUnityVector3(float3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static Vector2 ToUnityUV(float2 uv)
        {
            return new Vector2(uv.x, 1.0f - uv.y);
        }
    }
}
