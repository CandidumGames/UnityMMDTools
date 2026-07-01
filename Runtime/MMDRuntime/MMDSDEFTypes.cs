using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace UMT
{
    /// <summary>
    /// Per-vertex skinning data uploaded once to the SDEF compute shader. Layout is byte-for-byte compatible with the <c>SDEFVertexData</c> struct in <c>MMDSDEFSkinning.compute</c>; every field is 16 bytes so the C# blittable layout matches the HLSL <c>StructuredBuffer</c> element layout.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct SDEFVertexData
    {
        /// <summary>Up to four influencing bone indices; unused slots are -1.</summary>
        public int4 boneIndices;
        /// <summary>Bone weights matching <see cref="boneIndices"/>; SDEF uses only x and y.</summary>
        public float4 boneWeights;
        /// <summary>SDEF center in <c>.xyz</c>; <c>.w</c> is the deform type (0 = LBS, 1 = SDEF).</summary>
        public float4 sdefC;
        /// <summary>Corrected SDEF R0 vector in <c>.xyz</c> (only meaningful when SDEF).</summary>
        public float4 sdefR0;
        /// <summary>Corrected SDEF R1 vector in <c>.xyz</c> (only meaningful when SDEF).</summary>
        public float4 sdefR1;
    }

    /// <summary>
    /// Compact per-mesh vertex-morph table consumed by the SDEF compute pass. Morph slots line up with the generated mesh's blend-shape order so per-frame weights can be pulled from the <see cref="UnityEngine.SkinnedMeshRenderer"/> by slot.
    /// </summary>
    [Serializable]
    public sealed class MMDMorphTable
    {
        /// <summary>Blend-shape name for each morph slot, in slot order (matches the mesh blend shapes).</summary>
        public string[] blendShapeNames = Array.Empty<string>();
        /// <summary>All morph offsets, grouped by mesh vertex and indexed by <see cref="perVertexRanges"/>.</summary>
        public uint4[] flatOffsets = Array.Empty<uint4>();
        /// <summary>Per mesh vertex <c>(start, count)</c> range into <see cref="flatOffsets"/>.</summary>
        public int2[] perVertexRanges = Array.Empty<int2>();
    }

    /// <summary>
    /// Shared SDEF math helpers reused by the mesh builder and tests.
    /// </summary>
    public static class MMDSDEFMath
    {
        /// <summary>
        /// Applies the SDEF R0/R1 correction so the corrected vectors blend back to the center under the bone weights: <c>Rblend = R0*weight0 + R1*weight1</c>, <c>R0' = center + R0 - Rblend</c>, <c>R1' = center + R1 - Rblend</c> (with <c>weight1 = 1 - weight0</c>).
        /// </summary>
        /// <param name="center">SDEF center, in Unity space.</param>
        /// <param name="R0">Raw SDEF R0 vector, in Unity space.</param>
        /// <param name="R1">Raw SDEF R1 vector, in Unity space.</param>
        /// <param name="weight0">Weight of the first bone (the second bone uses <c>1 - weight0</c>).</param>
        /// <param name="correctedR0">Corrected R0 vector.</param>
        /// <param name="correctedR1">Corrected R1 vector.</param>
        public static void CorrectSDEF(float3 center, float3 R0, float3 R1, float weight0, out float3 correctedR0, out float3 correctedR1)
        {
            float weight1 = 1.0f - weight0;
            float3 weightedRotation = R0 * weight0 + R1 * weight1;
            correctedR0 = center + R0 - weightedRotation;
            correctedR1 = center + R1 - weightedRotation;
        }
    }
}
