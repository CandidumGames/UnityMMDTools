using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UMT
{
    /// <summary>
    /// Drives full GPU skinning for one generated <see cref="SkinnedMeshRenderer"/> whose mesh contains SDEF vertices. Each dispatch reads the bind-pose source mesh buffer, applies vertex-morph blend shapes and bone skinning (linear blend skinning for BDEF vertices, the SDEF formula for SDEF vertices), and writes the result into the renderer's GPU output vertex buffer via a compute shader. The owning <see cref="MMDTransformManager"/> calls <see cref="Dispatch"/> after the bone solve so the bone matrices are final. The component owns all GPU buffers and releases them on destroy.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MMDSDEFSkinner : MonoBehaviour, IDisposable
    {
        /// <summary>The skinned renderer whose output buffer is overwritten by the compute pass.</summary>
        public SkinnedMeshRenderer targetRenderer;
        /// <summary>Source PMX model providing the bone count.</summary>
        public PMXModel model;
        /// <summary>Mesh bindposes, one per bone, used to form the per-bone skinning matrices.</summary>
        [HideInInspector] public Matrix4x4[] bindposes = Array.Empty<Matrix4x4>();
        /// <summary>Per-vertex SDEF skinning data, in mesh-vertex order.</summary>
        [HideInInspector] public SDEFVertexData[] sdefVertexData = Array.Empty<SDEFVertexData>();
        /// <summary>Vertex-morph table feeding the per-frame morph weights.</summary>
        [HideInInspector] public MMDMorphTable morphTable;
        /// <summary>Whether the fixed vertex layout includes a tangent attribute (reserved; currently false).</summary>
        public bool hasTangent;

        private const int k_ThreadGroupSize = 64;

        private static readonly int s_SourceVerticesProperty = Shader.PropertyToID("_SourceVertices");
        private static readonly int s_DestVerticesProperty = Shader.PropertyToID("_DestVertices");
        private static readonly int s_VertexDataProperty = Shader.PropertyToID("_VertexData");
        private static readonly int s_BoneMatricesProperty = Shader.PropertyToID("_BoneMatrices");
        private static readonly int s_BoneQuaternionsProperty = Shader.PropertyToID("_BoneQuaternions");
        private static readonly int s_MorphWeightsProperty = Shader.PropertyToID("_MorphWeights");
        private static readonly int s_MorphOffsetsProperty = Shader.PropertyToID("_MorphOffsets");
        private static readonly int s_MorphRangesProperty = Shader.PropertyToID("_MorphRanges");
        private static readonly int s_VertexCountProperty = Shader.PropertyToID("_VertexCount");
        private static readonly int s_SourceStrideProperty = Shader.PropertyToID("_SourceStride");
        private static readonly int s_DestStrideProperty = Shader.PropertyToID("_DestStride");
        private static readonly int s_PositionOffsetProperty = Shader.PropertyToID("_PositionOffset");
        private static readonly int s_NormalOffsetProperty = Shader.PropertyToID("_NormalOffset");
        private static readonly int s_HasTangentProperty = Shader.PropertyToID("_HasTangent");
        private static readonly int s_TangentOffsetProperty = Shader.PropertyToID("_TangentOffset");

        private ComputeShader m_ComputeShader;
        private int m_KernelIndex;
        private int m_VertexCount;
        private int m_SourceStride;
        private int m_DestStride;
        private int m_PositionOffset;
        private int m_NormalOffset;
        private int m_TangentOffset;
        private int m_ThreadGroups;

        private GraphicsBuffer m_SourceBuffer;
        private GraphicsBuffer m_DestBuffer;
        private ComputeBuffer m_VertexDataBuffer;
        private ComputeBuffer m_BoneMatrixBuffer;
        private ComputeBuffer m_BoneQuatBuffer;
        private ComputeBuffer m_MorphWeightBuffer;
        private ComputeBuffer m_MorphOffsetsBuffer;
        private ComputeBuffer m_MorphRangeBuffer;
        private CommandBuffer m_CommandBuffer;

        private NativeArray<float4x4> m_BoneMatrixScratch;
        private NativeArray<float4> m_BoneQuaternionScratch;
        private float[] m_MorphWeightScratch;
        private int[] m_BlendShapeIndices;
        private bool m_Initialized;

        void OnEnable()
        {
            m_Initialized = false;
        }

        /// <summary>
        /// Allocates the GPU buffers and command buffer for this renderer's mesh. Must be called once with the SDEF compute shader before the first dispatch; the mesh must be GPU-resident at this point.
        /// </summary>
        /// <param name="shader">The SDEF skinning compute shader.</param>
        /// <exception cref="ArgumentNullException">Thrown when the shader is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the renderer/mesh state is invalid.</exception>
        public void Initialize(ComputeShader shader)
        {
            if (shader == null)
            {
                throw new ArgumentNullException(nameof(shader));
            }
            if (targetRenderer == null || targetRenderer.sharedMesh == null)
            {
                throw new InvalidOperationException("MMDSDEFSkinner requires a target renderer with a shared mesh.");
            }

            Mesh mesh = targetRenderer.sharedMesh;
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            m_SourceBuffer = mesh.GetVertexBuffer(0);
            if (m_SourceBuffer == null)
            {
                throw new InvalidOperationException("Failed to acquire the source mesh vertex buffer; the mesh layout must allow raw GPU access.");
            }

            targetRenderer.vertexBufferTarget |= GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Vertex;
            m_DestBuffer = targetRenderer.GetVertexBuffer();
            if (m_DestBuffer == null)
            {
                return; // The renderer may not have allocated its output buffer yet; it will be acquired on the first dispatch.
            }
            m_VertexCount = mesh.vertexCount;
            if (sdefVertexData.Length != m_VertexCount)
            {
                throw new InvalidOperationException($"SDEF vertex data count ({sdefVertexData.Length}) does not match mesh vertex count ({m_VertexCount}).");
            }

            m_ComputeShader = shader;
            m_KernelIndex = shader.FindKernel("CSSkin");
            m_ThreadGroups = (m_VertexCount + k_ThreadGroupSize - 1) / k_ThreadGroupSize;

            // The authored source buffer (interleaved) and the skinned destination buffer (Unity's deformed stream) usually differ in stride, but both place position and normal at the same offsets. Query the real strides/offsets instead of assuming a fixed layout.
            m_SourceStride = m_SourceBuffer.stride;
            m_DestStride = m_DestBuffer.stride;
            m_PositionOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            m_NormalOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
            m_TangentOffset = hasTangent ? mesh.GetVertexAttributeOffset(VertexAttribute.Tangent) : 0;

            m_VertexDataBuffer = new ComputeBuffer(m_VertexCount, UnsafeStrideOf<SDEFVertexData>(), ComputeBufferType.Structured);
            m_VertexDataBuffer.SetData(sdefVertexData);

            int boneCount = math.max(model.bones.Length, 1);
            m_BoneMatrixBuffer = new ComputeBuffer(boneCount, UnsafeStrideOf<float4x4>(), ComputeBufferType.Structured);
            m_BoneQuatBuffer = new ComputeBuffer(boneCount, UnsafeStrideOf<float4>(), ComputeBufferType.Structured);
            m_BoneMatrixScratch = new NativeArray<float4x4>(boneCount, Allocator.Persistent);
            m_BoneQuaternionScratch = new NativeArray<float4>(boneCount, Allocator.Persistent);

            int morphSlotCount = math.max(morphTable != null ? morphTable.blendShapeNames.Length : 0, 1);
            m_MorphWeightBuffer = new ComputeBuffer(morphSlotCount, sizeof(float), ComputeBufferType.Structured);
            m_MorphWeightScratch = new float[morphSlotCount];

            int MorphOffsetsCount = math.max(morphTable != null ? morphTable.flatOffsets.Length : 0, 1);
            m_MorphOffsetsBuffer = new ComputeBuffer(MorphOffsetsCount * 4, 4, ComputeBufferType.Raw);
            if (morphTable != null && morphTable.flatOffsets.Length > 0)
            {
                m_MorphOffsetsBuffer.SetData(morphTable.flatOffsets);
            }

            m_MorphRangeBuffer = new ComputeBuffer(m_VertexCount, UnsafeStrideOf<int2>(), ComputeBufferType.Structured);
            m_MorphRangeBuffer.SetData(BuildMorphRanges());

            m_BlendShapeIndices = BuildBlendShapeIndices(mesh);

            m_CommandBuffer = new CommandBuffer { name = "MMD SDEF Skinning" };
            m_Initialized = true;

            Debug.Log($"[UMT SDEF] {targetRenderer.name}: verts={m_VertexCount} sourceStride={m_SourceStride} destStride={m_DestStride} (destCount={m_DestBuffer.count}) posOffset={m_PositionOffset} normalOffset={m_NormalOffset} bones={boneCount} morphSlots={m_BlendShapeIndices.Length}", targetRenderer);
        }

        /// <summary>
        /// Updates the per-frame bone matrices, bone rotation quaternions, and morph weights, then encodes and executes the compute dispatch that overwrites the renderer's output vertex buffer.
        /// </summary>
        /// <param name="shader">The SDEF compute shader, used to lazily initialize on the first dispatch.</param>
        public void Dispatch(ComputeShader shader)
        {
            if (!m_Initialized)
            {
                Initialize(shader);
            }

            if (!m_Initialized || m_BoneMatrixBuffer == null || m_BoneQuatBuffer == null || m_MorphWeightBuffer == null || m_MorphOffsetsBuffer == null || m_MorphRangeBuffer == null)
            {
                return;
            }

            // Source the skinning matrices from the live bone transforms rather than the transform solver so SDEF deformation tracks whatever poses the bones (the MMD solver, an Animator, or manual posing) and is independent of the solver's transformEnabled state.
            Transform[] bones = targetRenderer.bones;
            int boneCount = math.min(bones.Length, m_BoneMatrixScratch.Length);
            // The skinned buffer is expressed in the renderer's rootBone space (Unity skins relative to the rootBone, not the SMR's own transform), matching the bindposes Unity uses for the non-SDEF meshes. Prefix each skinning matrix with the rootBone's inverse world matrix so the deformed vertices land in that space and respect the model root transform.
            Transform rootBone = targetRenderer.rootBone != null ? targetRenderer.rootBone : targetRenderer.transform;
            float4x4 worldToObject = (float4x4)rootBone.worldToLocalMatrix;
            for (int b = 0; b < boneCount; ++b)
            {
                Matrix4x4 boneLocalToWorld = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.identity;
                float4x4 skinMatrix = math.mul(worldToObject, math.mul((float4x4)boneLocalToWorld, (float4x4)bindposes[b]));
                m_BoneMatrixScratch[b] = skinMatrix;
                m_BoneQuaternionScratch[b] = ExtractRotation(skinMatrix).value;
            }
            m_BoneMatrixBuffer.SetData(m_BoneMatrixScratch, 0, 0, boneCount);
            m_BoneQuatBuffer.SetData(m_BoneQuaternionScratch, 0, 0, boneCount);

            if (morphTable != null && m_BlendShapeIndices.Length > 0)
            {
                for (int slot = 0; slot < m_BlendShapeIndices.Length; ++slot)
                {
                    int blendShapeIndex = m_BlendShapeIndices[slot];
                    m_MorphWeightScratch[slot] = blendShapeIndex >= 0 ? targetRenderer.GetBlendShapeWeight(blendShapeIndex) / 100.0f : 0.0f;
                }
                m_MorphWeightBuffer.SetData(m_MorphWeightScratch);
            }

            // Re-acquire the destination buffer each frame; the renderer may reallocate it (e.g. on enable or motion-vector buffer swaps).
            m_DestBuffer?.Dispose();
            m_DestBuffer = targetRenderer.GetVertexBuffer();
            if (m_DestBuffer == null)
            {
                throw new InvalidOperationException("Skinned renderer vertex buffer became unavailable during dispatch.");
            }

            m_CommandBuffer.Clear();
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_SourceVerticesProperty, m_SourceBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_DestVerticesProperty, m_DestBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_VertexDataProperty, m_VertexDataBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_BoneMatricesProperty, m_BoneMatrixBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_BoneQuaternionsProperty, m_BoneQuatBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_MorphWeightsProperty, m_MorphWeightBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_MorphOffsetsProperty, m_MorphOffsetsBuffer);
            m_CommandBuffer.SetComputeBufferParam(m_ComputeShader, m_KernelIndex, s_MorphRangesProperty, m_MorphRangeBuffer);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_VertexCountProperty, m_VertexCount);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_SourceStrideProperty, m_SourceStride);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_DestStrideProperty, m_DestStride);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_PositionOffsetProperty, m_PositionOffset);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_NormalOffsetProperty, m_NormalOffset);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_HasTangentProperty, hasTangent ? 1 : 0);
            m_CommandBuffer.SetComputeIntParam(m_ComputeShader, s_TangentOffsetProperty, m_TangentOffset);
            m_CommandBuffer.DispatchCompute(m_ComputeShader, m_KernelIndex, m_ThreadGroups, 1, 1);
            Graphics.ExecuteCommandBuffer(m_CommandBuffer);
        }

        /// <summary>
        /// Maps each morph slot to the renderer mesh's blend-shape index, or -1 when absent.
        /// </summary>
        private int[] BuildBlendShapeIndices(Mesh mesh)
        {
            if (morphTable == null || morphTable.blendShapeNames.Length == 0)
            {
                return Array.Empty<int>();
            }

            int[] indices = new int[morphTable.blendShapeNames.Length];
            for (int slot = 0; slot < indices.Length; ++slot)
            {
                indices[slot] = mesh.GetBlendShapeIndex(morphTable.blendShapeNames[slot]);
            }
            return indices;
        }

        /// <summary>
        /// Returns the per-vertex morph ranges, or a zero-range array when there are no morphs.
        /// </summary>
        private int2[] BuildMorphRanges()
        {
            if (morphTable != null && morphTable.perVertexRanges.Length == m_VertexCount)
            {
                return morphTable.perVertexRanges;
            }
            return new int2[m_VertexCount];
        }

        /// <summary>
        /// Extracts the rotation quaternion from a skinning matrix, normalizing the basis columns so skew or scale in the matrix does not corrupt the quaternion (PMX skinning is rigid, so columns are near orthonormal).
        /// </summary>
        private static quaternion ExtractRotation(float4x4 matrix)
        {
            float3 column0 = math.normalizesafe(matrix.c0.xyz, new float3(1.0f, 0.0f, 0.0f));
            float3 column1 = math.normalizesafe(matrix.c1.xyz, new float3(0.0f, 1.0f, 0.0f));
            float3 column2 = math.normalizesafe(matrix.c2.xyz, new float3(0.0f, 0.0f, 1.0f));
            return math.normalize(new quaternion(new float3x3(column0, column1, column2)));
        }

        private static int UnsafeStrideOf<T>() where T : struct
        {
            return UnsafeUtility.SizeOf<T>();
        }

        private void OnDestroy()
        {
            Dispose();
        }

        /// <summary>
        /// Releases all GPU buffers, the command buffer, and the native scratch arrays.
        /// </summary>
        public void Dispose()
        {
            m_SourceBuffer?.Dispose();
            m_DestBuffer?.Dispose();
            m_VertexDataBuffer?.Dispose();
            m_BoneMatrixBuffer?.Dispose();
            m_BoneQuatBuffer?.Dispose();
            m_MorphWeightBuffer?.Dispose();
            m_MorphOffsetsBuffer?.Dispose();
            m_MorphRangeBuffer?.Dispose();
            m_SourceBuffer = null;
            m_DestBuffer = null;
            m_VertexDataBuffer = null;
            m_BoneMatrixBuffer = null;
            m_BoneQuatBuffer = null;
            m_MorphWeightBuffer = null;
            m_MorphOffsetsBuffer = null;
            m_MorphRangeBuffer = null;

            m_CommandBuffer?.Dispose();
            m_CommandBuffer = null;

            if (m_BoneMatrixScratch.IsCreated)
            {
                m_BoneMatrixScratch.Dispose();
            }
            if (m_BoneQuaternionScratch.IsCreated)
            {
                m_BoneQuaternionScratch.Dispose();
            }

            m_Initialized = false;
        }
    }
}
