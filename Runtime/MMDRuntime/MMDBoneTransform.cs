using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Per-bone runtime data for the MMD transform solver: transform order, initial pose, constraints, IK, and solved transform state. Holds the blittable <see cref="BoneSolverData"/> used by the Burst-compiled solver.
    /// </summary>
    public sealed class MMDBoneTransform : MonoBehaviour
    {
        /// <summary>Index of this bone within the PMX bone list.</summary>
        public int boneIndex;
        /// <summary>Original PMX bone name.</summary>
        public string boneName;
        /// <summary>Parent bone in the MMD hierarchy, or null for a root bone.</summary>
        public MMDBoneTransform parentBone;
        /// <summary>Initial bone position in model space.</summary>
        public Vector3 initialModelPosition;
        /// <summary>Initial bone position relative to its parent.</summary>
        public Vector3 initialLocalPosition;
        /// <summary>Initial bone rotation relative to its parent.</summary>
        public Quaternion initialLocalRotation = Quaternion.identity;
        /// <summary>MMD transform-order level used to sort solving passes.</summary>
        public int transformLevel;
        /// <summary>PMX bone flags describing transform/constraint/IK capabilities.</summary>
        public PMXBone.Flags flags;
        /// <summary>Bone supplying rotation/translation constraint values, or null.</summary>
        public MMDBoneTransform constraintTarget;
        /// <summary>Constraint influence weight; negative values invert the constraint.</summary>
        public float constraintInfluence ;
        /// <summary>Whether the constraint reads the target's local (model) transform.</summary>
        public bool localConstraint;
        /// <summary>Whether this bone inherits rotation from its constraint target.</summary>
        public bool rotationConstraint;
        /// <summary>Whether this bone inherits translation from its constraint target.</summary>
        public bool translationConstraint;
        /// <summary>Whether this bone is solved in the after-physics pass.</summary>
        public bool afterPhysics;
        /// <summary>Whether IK solving is enabled for this bone (when it is an IK controller).</summary>
        public bool ikEnabled;
        /// <summary>IK data for this bone when it acts as an IK controller.</summary>
        public MMDBoneIKData ik = new MMDBoneIKData();

        /// <summary>
        /// Blittable per-bone solver state used by the Burst-compiled MMD transform solver. Combines read-only initial/constraint data with mutable solved transform state.
        /// </summary>
        public struct BoneSolverData
        {
            /// <summary>Original PMX bone name truncated to a fixed string.</summary>
            public readonly FixedString32Bytes boneName;
            /// <summary>Initial bone position in model space.</summary>
            public readonly float3 initialModelPosition;
            /// <summary>Initial bone position relative to its parent.</summary>
            public readonly float3 initialLocalPosition;
            /// <summary>Initial bone rotation relative to its parent.</summary>
            public readonly quaternion initialLocalRotation;
            /// <summary>Index of the constraint target bone, or negative when there is none.</summary>
            public readonly int constraintTargetBoneIndex;
            /// <summary>Constraint influence weight; negative values invert the constraint.</summary>
            public readonly float constraintInfluence;
            /// <summary>Whether the constraint reads the target's local (model) transform.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool localConstraint;
            /// <summary>Whether this bone inherits rotation from its constraint target.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool rotationConstraint;
            /// <summary>Whether this bone inherits translation from its constraint target.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool translationConstraint;
            /// <summary>Whether this bone may be translated.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool translatable;
            /// <summary>Whether this bone may be rotated.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool rotatable;
            /// <summary>Index of the parent bone, or negative for a root bone.</summary>
            public readonly int parentBoneIndex;
            /// <summary>Current local position before solving.</summary>
            public float3 localPosition;
            /// <summary>Current local rotation before solving.</summary>
            public quaternion localRotation;
            /// <summary>Accumulated translation contributed to constraint targets.</summary>
            public float3 translationConstraintValue;
            /// <summary>Accumulated rotation contributed to constraint targets.</summary>
            public quaternion rotationConstraintValue;
            /// <summary>IK rotation delta applied on top of the local rotation.</summary>
            public quaternion ikRotation;
            /// <summary>Local transform matrix derived from the solved local pose.</summary>
            public float4x4 localMatrix;
            /// <summary>World transform matrix derived from the parent world matrix and local matrix.</summary>
            public float4x4 worldMatrix;
            /// <summary>Local position used as the IK-link base before applying IK rotation.</summary>
            public float3 localPositionForIKLink;
            /// <summary>Local rotation used as the IK-link base before applying IK rotation.</summary>
            public quaternion localRotationForIKLink;
            /// <summary>Final solved local position flushed back to Unity.</summary>
            public float3 solvedLocalPosition;
            /// <summary>Final solved local rotation flushed back to Unity.</summary>
            public quaternion solvedLocalRotation;
            /// <summary>Whether this bone has a valid solved transform this pass.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool hasSolvedTransform;
            /// <summary>Whether the solved transform came from the IK solver.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool solvedByIK;

            /// <summary>
            /// Initializes solver data from an <see cref="MMDBoneTransform"/> component.
            /// </summary>
            /// <param name="bone">Source bone component.</param>
            public BoneSolverData(MMDBoneTransform bone) : this(bone.boneName, bone.initialModelPosition, bone.initialLocalPosition, bone.initialLocalRotation, bone.constraintInfluence, bone.constraintTarget != null ? bone.constraintTarget.boneIndex : -1, bone.localConstraint, bone.rotationConstraint, bone.translationConstraint, bone.flags.HasFlag(PMXBone.Flags.Translatable), bone.flags.HasFlag(PMXBone.Flags.Rotatable), bone.parentBone != null ? bone.parentBone.boneIndex : -1)
            {
            }

            /// <summary>
            /// Initializes solver data by copying the read-only initial/constraint fields of another instance, resetting all mutable solved state to defaults.
            /// </summary>
            /// <param name="other">Source solver data to copy read-only fields from.</param>
            public BoneSolverData(BoneSolverData other) : this(other.boneName, other.initialModelPosition, other.initialLocalPosition, other.initialLocalRotation, other.constraintInfluence, other.constraintTargetBoneIndex, other.localConstraint, other.rotationConstraint, other.translationConstraint, other.translatable, other.rotatable, other.parentBoneIndex)
            {
            }

            /// <summary>
            /// Initializes solver data from explicit initial and constraint values, with all mutable solved state set to identity/zero defaults.
            /// </summary>
            /// <param name="boneName">Original PMX bone name truncated to a fixed string.</param>
            /// <param name="initialModelPosition">Initial bone position in model space.</param>
            /// <param name="initialLocalPosition">Initial bone position relative to its parent.</param>
            /// <param name="initialLocalRotation">Initial bone rotation relative to its parent.</param>
            /// <param name="constraintInfluence">Constraint influence weight; negative inverts the constraint.</param>
            /// <param name="constraintTargetBoneIndex">Index of the constraint target bone, or negative when none.</param>
            /// <param name="localConstraint">Whether the constraint reads the target's local transform.</param>
            /// <param name="rotationConstraint">Whether this bone inherits rotation from its constraint target.</param>
            /// <param name="translationConstraint">Whether this bone inherits translation from its constraint target.</param>
            /// <param name="translatable">Whether this bone may be translated.</param>
            /// <param name="rotatable">Whether this bone may be rotated.</param>
            /// <param name="parentBoneIndex">Index of the parent bone, or negative for a root bone.</param>
            public BoneSolverData(FixedString32Bytes boneName, float3 initialModelPosition, float3 initialLocalPosition, quaternion initialLocalRotation, float constraintInfluence, int constraintTargetBoneIndex, bool localConstraint, bool rotationConstraint, bool translationConstraint, bool translatable, bool rotatable, int parentBoneIndex)
            {
                this.boneName = boneName;
                this.initialModelPosition = initialModelPosition;
                this.initialLocalPosition = initialLocalPosition;
                this.initialLocalRotation = initialLocalRotation;
                this.constraintTargetBoneIndex = constraintTargetBoneIndex;
                this.constraintInfluence = constraintInfluence;
                this.localConstraint = localConstraint;
                this.rotationConstraint = rotationConstraint;
                this.translationConstraint = translationConstraint;
                this.translatable = translatable;
                this.rotatable = rotatable;
                this.parentBoneIndex = parentBoneIndex;
                localPosition = float3.zero;
                localRotation = quaternion.identity;
                translationConstraintValue = float3.zero;
                rotationConstraintValue = quaternion.identity;
                ikRotation = quaternion.identity;
                localMatrix = float4x4.identity;
                worldMatrix = float4x4.identity;
                localPositionForIKLink = float3.zero;
                localRotationForIKLink = quaternion.identity;
                solvedLocalPosition = float3.zero;
                solvedLocalRotation = quaternion.identity;
                hasSolvedTransform = false;
                solvedByIK = false;
            }

            /// <summary>
            /// Creates a default, empty solver data instance with no constraint, no parent, and identity transforms.
            /// </summary>
            /// <returns>A default-initialized <see cref="BoneSolverData"/>.</returns>
            public static BoneSolverData CreateDefault()
            {
                return new BoneSolverData("", float3.zero, float3.zero, quaternion.identity, 0f, -1, false, false, false, false, false, -1);
            }
        };

        /// <summary>
        /// Current solver state for this bone, mirrored into the transform manager's native cache.
        /// </summary>
        [NonSerialized] public BoneSolverData runtimeData = BoneSolverData.CreateDefault();

        /// <summary>
        /// Captures the current Unity local transform as this bone's initial pose and re-seeds the runtime solver data so the local, IK-link, and solved transforms start from it.
        /// </summary>
        public void RefreshInitialTransform()
        {
            initialLocalPosition = transform.localPosition;
            initialLocalRotation = transform.localRotation;
            runtimeData = new BoneSolverData(this);
            runtimeData.localPosition = initialLocalPosition;
            runtimeData.localRotation = initialLocalRotation;
            runtimeData.localPositionForIKLink = initialLocalPosition;
            runtimeData.localRotationForIKLink = initialLocalRotation;
            runtimeData.solvedLocalPosition = initialLocalPosition;
            runtimeData.solvedLocalRotation = initialLocalRotation;
        }

        
        /// <summary>
        /// Solves a bone's local and world matrices, applying constraints, then writes the result back into the solver context.
        /// </summary>
        /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
        /// <param name="boneIndex">Index of the bone to update.</param>
        internal static void UpdateLocalMatrix(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex)
        {
            BoneMath.UpdateLocalMatrixInternal(ref transformManagerContext, boneIndex);
        }

        /// <summary>
        /// Recomputes a bone's local and world matrices from its sampled local transform without solving constraints, marking it as not yet solved.
        /// </summary>
        /// <param name="runtimeData">Bone solver data to update.</param>
        /// <param name="parentWorldMatrix">World matrix of the bone's parent.</param>
        internal static void ObserveLocalMatrix(ref BoneSolverData runtimeData, in float4x4 parentWorldMatrix)
        {
            BoneMath.ObserveLocalMatrixInternal(ref runtimeData, parentWorldMatrix);
        }

        /// <summary>
        /// Recomputes a bone's matrices from its IK-link base transform plus the current IK rotation, marking it solved by IK.
        /// </summary>
        /// <param name="runtimeData">Bone solver data to update.</param>
        /// <param name="parentWorldMatrix">World matrix of the bone's parent.</param>
        internal static void UpdateLocalMatrixIKLink(ref BoneSolverData runtimeData, in float4x4 parentWorldMatrix)
        {
            BoneMath.UpdateLocalMatrixIKLinkInternal(ref runtimeData, parentWorldMatrix);
        }

        /// <summary>
        /// Resets a bone's runtime data to the start of a solve pass, restoring either the initial pose (when the current transform matches the last solved transform) or the sampled transform.
        /// </summary>
        /// <param name="runtimeData">Bone solver data to reset.</param>
        /// <param name="currentLocalPosition">Current sampled local position.</param>
        /// <param name="currentLocalRotation">Current sampled local rotation.</param>
        internal static void ResetRuntimeData(ref BoneSolverData runtimeData, in float3 currentLocalPosition, in quaternion currentLocalRotation)
        {
            BoneMath.ResetRuntimeDataInternal(ref runtimeData, in currentLocalPosition, in currentLocalRotation);
        }

        /// <summary>
        /// Returns the world matrix of a bone's parent, or the root parent world matrix for a root bone.
        /// </summary>
        /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
        /// <param name="boneIndex">Index of the bone whose parent matrix is requested.</param>
        /// <returns>The parent (or root) world matrix.</returns>
        internal static float4x4 GetParentWorldMatrix(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex)
        {
            BoneMath.GetParentWorldMatrixInternal(ref transformManagerContext, boneIndex, out float4x4 parentWorldMatrix);
            return parentWorldMatrix;
        }

        /// <summary>
        /// Directly applies a local position and rotation to a bone, recomputing its matrices and marking it solved (not by IK).
        /// </summary>
        /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
        /// <param name="boneIndex">Index of the bone to update.</param>
        /// <param name="localPosition">Local position to apply.</param>
        /// <param name="localRotation">Local rotation to apply.</param>
        internal static void ApplyLocalTransformToBone(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex, in float3 localPosition, in quaternion localRotation)
        {
            BoneMath.ApplyLocalTransformToBoneInternal(ref transformManagerContext, boneIndex, in localPosition, in localRotation);
        }

        [BurstCompile]
        private static class BoneMath
        {
            /// <summary>
            /// Burst implementation that recomputes a bone's solved local/world matrices, applying any MMD rotation/translation constraint from its constraint target bone before composing the final transform.
            /// </summary>
            /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
            /// <param name="boneIndex">Index of the bone whose matrix is recomputed.</param>
            [BurstCompile]
            internal static void UpdateLocalMatrixInternal(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex)
            {
                BoneSolverData input = transformManagerContext.boneSolverData[boneIndex];
                BoneSolverData output = input;
                BoneSolverData constraintRuntimeData = input.constraintTargetBoneIndex >= 0 ? transformManagerContext.boneSolverData[input.constraintTargetBoneIndex] : BoneSolverData.CreateDefault();
                float3 nextPosition = input.localPosition;
                quaternion nextRotation = input.localRotation;
                float3 localPositionDelta = LocalPositionDelta(input.localPosition, input.initialLocalPosition);
                quaternion localRotationDelta = LocalRotationDelta(input.localRotation, input.initialLocalRotation);

                if (input.constraintTargetBoneIndex != -1 && !Approximately(input.constraintInfluence, 0.0f) && transformManagerContext.solveConstraints)
                {
                    if (input.rotationConstraint)
                    {
                        quaternion sourceRotation = input.localConstraint ? ModelRotation(constraintRuntimeData.worldMatrix) : math.mul(constraintRuntimeData.rotationConstraintValue, constraintRuntimeData.ikRotation);
                        quaternion weightedRotation = input.constraintInfluence >= 0.0f ? math.slerp(quaternion.identity, sourceRotation, input.constraintInfluence) : math.slerp(quaternion.identity, math.inverse(sourceRotation), -input.constraintInfluence);
                        nextRotation = math.mul(weightedRotation, nextRotation);
                        output.rotationConstraintValue = math.mul(weightedRotation, localRotationDelta);
                    }
                    else
                    {
                        output.rotationConstraintValue = localRotationDelta;
                    }

                    if (input.translationConstraint)
                    {
                        float3 sourceTranslation = input.localConstraint ? math.transform(constraintRuntimeData.worldMatrix, float3.zero)- constraintRuntimeData.initialModelPosition : constraintRuntimeData.translationConstraintValue;
                        float3 weightedTranslation = sourceTranslation * input.constraintInfluence;
                        nextPosition += weightedTranslation;
                        output.translationConstraintValue = weightedTranslation + localPositionDelta;
                    }
                    else
                    {
                        output.translationConstraintValue = localPositionDelta;
                    }
                }
                else
                {
                    output.rotationConstraintValue = localRotationDelta;
                    output.translationConstraintValue = localPositionDelta;
                }

                output.localPositionForIKLink = nextPosition;
                output.localRotationForIKLink = nextRotation;
                output.solvedLocalPosition = nextPosition;
                output.solvedLocalRotation = math.normalize(math.mul(nextRotation, input.ikRotation));
                output.localMatrix = float4x4.TRS(nextPosition, output.solvedLocalRotation, new float3(1.0f, 1.0f, 1.0f));
                GetParentWorldMatrixInternal(ref transformManagerContext, boneIndex, out float4x4 parentWorldMatrix);
                output.worldMatrix = math.mul(parentWorldMatrix, output.localMatrix);
                output.hasSolvedTransform = true;

                transformManagerContext.boneSolverData[boneIndex] = output;
            }

            /// <summary>
            /// Burst implementation that resets a bone's solver data toward its initial pose, restoring the initial local transform when the sampled transform still matches the previously solved value.
            /// </summary>
            /// <param name="runtimeData">Bone solver data to reset, updated in place.</param>
            /// <param name="currentLocalPosition">Local position currently sampled from the Unity transform.</param>
            /// <param name="currentLocalRotation">Local rotation currently sampled from the Unity transform.</param>
            [BurstCompile]
            internal static void ResetRuntimeDataInternal(ref BoneSolverData runtimeData, in float3 currentLocalPosition, in quaternion currentLocalRotation)
            {
                BoneSolverData previousRuntimeData = runtimeData;
                bool currentSolvedTransform = IsCurrentSolvedTransform(previousRuntimeData.hasSolvedTransform, currentLocalPosition, currentLocalRotation, previousRuntimeData.solvedLocalPosition, previousRuntimeData.solvedLocalRotation);
                runtimeData = new BoneSolverData(previousRuntimeData);
                runtimeData.localPosition = currentSolvedTransform ? previousRuntimeData.initialLocalPosition : currentLocalPosition;
                runtimeData.localRotation = currentSolvedTransform ? previousRuntimeData.initialLocalRotation : currentLocalRotation;
                runtimeData.localMatrix = float4x4.TRS(runtimeData.localPosition, runtimeData.localRotation, new float3(1.0f, 1.0f, 1.0f));
                runtimeData.localPositionForIKLink = runtimeData.localPosition;
                runtimeData.localRotationForIKLink = runtimeData.localRotation;
            }

            /// <summary>
            /// Burst implementation that recomputes a bone's local/world matrices and constraint deltas from its current local transform without marking it solved (used to observe externally driven bones).
            /// </summary>
            /// <param name="runtimeData">Bone solver data to update in place.</param>
            /// <param name="parentWorldMatrix">World matrix of the bone's parent.</param>
            [BurstCompile]
            internal static void ObserveLocalMatrixInternal(ref BoneSolverData runtimeData, in float4x4 parentWorldMatrix)
            {
                runtimeData.translationConstraintValue = LocalPositionDelta(runtimeData.localPosition, runtimeData.initialLocalPosition);
                runtimeData.rotationConstraintValue = LocalRotationDelta(runtimeData.localRotation, runtimeData.initialLocalRotation);
                runtimeData.localPositionForIKLink = runtimeData.localPosition;
                runtimeData.localRotationForIKLink = runtimeData.localRotation;
                runtimeData.localMatrix = float4x4.TRS(runtimeData.localPosition, runtimeData.localRotation, new float3(1.0f, 1.0f, 1.0f));
                runtimeData.worldMatrix = math.mul(parentWorldMatrix, runtimeData.localMatrix);
                runtimeData.hasSolvedTransform = false;
            }

            /// <summary>
            /// Burst implementation that rebuilds an IK link bone's matrices from its IK-link local transform and IK rotation, marking the bone as solved by IK.
            /// </summary>
            /// <param name="runtimeData">Bone solver data to update in place.</param>
            /// <param name="parentWorldMatrix">World matrix of the bone's parent.</param>
            [BurstCompile]
            internal static void UpdateLocalMatrixIKLinkInternal(ref BoneSolverData runtimeData, in float4x4 parentWorldMatrix)
            {
                quaternion nextRotation = math.normalize(math.mul(runtimeData.localRotationForIKLink, runtimeData.ikRotation));
                runtimeData.localMatrix = float4x4.TRS(runtimeData.localPositionForIKLink, nextRotation, new float3(1.0f, 1.0f, 1.0f));
                runtimeData.worldMatrix = math.mul(parentWorldMatrix, runtimeData.localMatrix);
                runtimeData.solvedLocalPosition = runtimeData.localPositionForIKLink;
                runtimeData.solvedLocalRotation = nextRotation;
                runtimeData.hasSolvedTransform = true;
                runtimeData.solvedByIK = true;
            }

            private static quaternion ModelRotation(float4x4 worldMatrix)
            {
                return math.normalize(new quaternion(worldMatrix));
            }

            private static float3 LocalPositionDelta(float3 localPosition, float3 initialLocalPosition)
            {
                return localPosition - initialLocalPosition;
            }

            private static quaternion LocalRotationDelta(quaternion localRotation, quaternion initialLocalRotation)
            {
                return math.normalize(math.mul(localRotation, math.inverse(initialLocalRotation)));
            }
            
            private static bool IsCurrentSolvedTransform(bool hasSolvedTransform, float3 currentLocalPosition, quaternion currentLocalRotation, float3 solvedLocalPosition, quaternion solvedLocalRotation)
            {
                return hasSolvedTransform &&
                    math.lengthsq(currentLocalPosition - solvedLocalPosition) == 0.0f &&
                    math.abs(math.dot(currentLocalRotation, solvedLocalRotation) - 1.0f) <= 0.000001f;
            }

            private static bool Approximately(float a, float b)
            {
                return math.abs(b - a) < math.max(0.000001f * math.max(math.abs(a), math.abs(b)), math.EPSILON * 8.0f);
            }

            /// <summary>
            /// Burst implementation that resolves a bone's parent world matrix, falling back to the solver's root parent world matrix when the bone has no parent.
            /// </summary>
            /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
            /// <param name="boneIndex">Index of the bone whose parent matrix is resolved.</param>
            /// <param name="parentWorldMatrix">Receives the parent's world matrix.</param>
            [BurstCompile]
            internal static void GetParentWorldMatrixInternal(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex, out float4x4 parentWorldMatrix)
            {
                int parentBoneIndex = transformManagerContext.boneSolverData[boneIndex].parentBoneIndex;
                parentWorldMatrix = parentBoneIndex >= 0 ? transformManagerContext.boneSolverData[parentBoneIndex].worldMatrix : transformManagerContext.rootParentWorldMatrix;
            }

            /// <summary>
            /// Burst implementation that writes a local position and rotation directly onto a bone, recomputing its matrices and marking it solved but not driven by IK.
            /// </summary>
            /// <param name="transformManagerContext">Solver context holding all bone solver data.</param>
            /// <param name="boneIndex">Index of the bone to update.</param>
            /// <param name="localPosition">Local position to apply.</param>
            /// <param name="localRotation">Local rotation to apply.</param>
            [BurstCompile]
            internal static void ApplyLocalTransformToBoneInternal(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex, in float3 localPosition, in quaternion localRotation)
            {
                MMDBoneTransform.BoneSolverData runtimeData = transformManagerContext.boneSolverData[boneIndex];
                runtimeData.localPosition = localPosition;
                runtimeData.localRotation = localRotation;
                runtimeData.localPositionForIKLink = localPosition;
                runtimeData.localRotationForIKLink = localRotation;
                runtimeData.localMatrix = float4x4.TRS(localPosition, localRotation, new float3(1.0f, 1.0f, 1.0f));
                GetParentWorldMatrixInternal(ref transformManagerContext, boneIndex, out float4x4 parentWorldMatrix);
                runtimeData.worldMatrix = math.mul(parentWorldMatrix, runtimeData.localMatrix);
                runtimeData.solvedLocalPosition = localPosition;
                runtimeData.solvedLocalRotation = localRotation;
                runtimeData.hasSolvedTransform = true;
                runtimeData.solvedByIK = false;
                transformManagerContext.boneSolverData[boneIndex] = runtimeData;
            }
        }
    }

    /// <summary>
    /// IK controller data for a bone: the IK target, iteration count, per-iteration angle limit, and the ordered IK link chain.
    /// </summary>
    [Serializable]
    public sealed class MMDBoneIKData
    {
        /// <summary>End-effector bone the IK solver drives toward the controller.</summary>
        public MMDBoneTransform target;
        /// <summary>Maximum number of IK solver iterations.</summary>
        public int iterations;
        /// <summary>Maximum rotation angle applied per IK step.</summary>
        public float angleLimit;
        /// <summary>Ordered chain of IK links from the effector toward the root.</summary>
        public MMDBoneIKLinkData[] links = Array.Empty<MMDBoneIKLinkData>();
    }

    /// <summary>
    /// A single IK link in an IK chain, with its bone, optional per-axis angle limits, and the derived fix-axis and Euler decomposition order used while solving.
    /// </summary>
    [Serializable]
    public sealed class MMDBoneIKLinkData
    {
        /// <summary>Bone rotated by this IK link.</summary>
        public MMDBoneTransform bone;
        /// <summary>Whether this link has per-axis angle limits.</summary>
        public bool hasAngleLimit;
        /// <summary>Lower per-axis rotation limit, in radians.</summary>
        public float3 lowerLimit;
        /// <summary>Upper per-axis rotation limit, in radians.</summary>
        public float3 upperLimit;
        /// <summary>Axis the IK rotation is constrained to, derived from the angle limits.</summary>
        public MMDIKFixAxis fixAxis;
        /// <summary>Euler decomposition order used when clamping the IK rotation.</summary>
        public MMDIKEulerOrder eulerOrder;
    }

    /// <summary>
    /// Axis constraint applied to an IK link's rotation, derived from its angle limits.
    /// </summary>
    public enum MMDIKFixAxis : byte
    {
        /// <summary>
        /// No axis constraint.
        /// </summary>
        None,
        /// <summary>
        /// The link is fully fixed (no rotation).
        /// </summary>
        Fix,
        /// <summary>
        /// Rotation is constrained to the X axis.
        /// </summary>
        X,
        /// <summary>
        /// Rotation is constrained to the Y axis.
        /// </summary>
        Y,
        /// <summary>
        /// Rotation is constrained to the Z axis.
        /// </summary>
        Z,
    }

    /// <summary>
    /// Euler decomposition order used when clamping an IK link's rotation to its angle limits.
    /// </summary>
    public enum MMDIKEulerOrder : byte
    {
        /// <summary>
        /// Z-X-Y decomposition order.
        /// </summary>
        ZXY,
        /// <summary>
        /// X-Y-Z decomposition order.
        /// </summary>
        XYZ,
        /// <summary>
        /// Y-Z-X decomposition order.
        /// </summary>
        YZX,
    }
}
