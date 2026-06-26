using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static UMT.PMXUtilities;

namespace UMT
{
    /// <summary>
    /// Coordinates native Bullet-backed MMD rigid-body physics: builds rigid bodies, joints,
    /// optional ground collision, and physics runtime data from the <see cref="PMXModel"/>, and
    /// drives the simulation each frame in concert with <see cref="MMDTransformManager"/>.
    /// </summary>
    [RequireComponent(typeof(MMDTransformManager))]
    public sealed class MMDPhysicsManager : MonoBehaviour
    {
        private const float k_MMDPhysicsGravity = 98.0f;
        private const byte k_MMDGroundCollisionGroup = 15;
        private const short k_MMDGroundCollisionMask = -1;
        private const int k_SolverIterations = 4;
        private const int k_MaxSubSteps = 120;
        private const float k_FixedTimeStep = 1.0f / 120.0f;

        /// <summary>Rigid-body components managed by this physics manager.</summary>
        public MMDRigidBody[] rigidBodies = Array.Empty<MMDRigidBody>();
        /// <summary>Joint components managed by this physics manager.</summary>
        public MMDJoint[] joints = Array.Empty<MMDJoint>();
        /// <summary>Whether the static ground collider is enabled.</summary>
        public bool enableGroundCollision = true;
        /// <summary>Random seed used to reset the deterministic physics simulation.</summary>
        public uint physicsSeed = 0;

        /// <summary>
        /// Mutable native physics solver state: the Bullet context, transform/index buffers,
        /// rigid-body simulation data, and whether the initial pose has been seeded.
        /// </summary>
        internal struct PhysicsSolverContext
        {
            /// <summary>Native Bullet physics context wrapper.</summary>
            internal MMDBulletPhysics bulletPhysicsContext;
            /// <summary>Scratch buffer of rigid-body world transforms.</summary>
            internal NativeArray<float4x4> worldTransforms;
            /// <summary>Scratch buffer of rigid-body indices paired with <see cref="worldTransforms"/>.</summary>
            internal NativeArray<int> rigidBodyIndices;
            /// <summary>Per-rigid-body simulation data mirrored to the native context.</summary>
            internal NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodySimulationData;
            /// <summary>Whether the initial bone-driven rigid-body pose has been applied.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool initialPoseApplied;
        }
        private PhysicsSolverContext m_PhysicsSolverContext;

        /// <summary>Reference to this manager's mutable physics solver context.</summary>
        internal ref PhysicsSolverContext Context => ref m_PhysicsSolverContext;

        private void OnEnable()
        {
            RebuildRuntimeData();
        }

        private void OnDisable()
        {
            DisposePhysics();
        }

        /// <summary>
        /// (Re)creates the native physics context with MMD gravity and solver settings, rebuilds
        /// runtime data, and builds rigid bodies, joints, and ground collision.
        /// </summary>
        internal void Initialize()
        {
            DisposePhysics();
            m_PhysicsSolverContext.bulletPhysicsContext = new MMDBulletPhysics(
                new float3(0.0f, -k_MMDPhysicsGravity * MMDConstants.k_MMDUnitToUnityUnit, 0.0f),
                k_SolverIterations,
                k_MaxSubSteps,
                k_FixedTimeStep);

            RebuildRuntimeData();
            BuildRigidBodies();
            BuildJoints();
            BuildGround();
        }

        /// <summary>
        /// Resets the native simulation with <see cref="physicsSeed"/>, restores simulated bones to
        /// their initial pose, and clears the initial-pose-applied flag.
        /// </summary>
        internal void ResetPhysics()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.Reset(physicsSeed);
            ResetSimulatedBoneTransformsToInitial();
            m_PhysicsSolverContext.initialPoseApplied = false;
        }

        /// <summary>
        /// Seeds or syncs rigid bodies from bone transforms, steps the simulation, and applies
        /// dynamic rigid-body results back onto their related bones.
        /// </summary>
        /// <param name="physicsElapsedTime">Elapsed time to simulate; zero or less skips stepping.</param>
        /// <param name="transformManagerContext">Transform solver context providing bone matrices.</param>
        /// <param name="runtimeContext">Physics solver context to advance.</param>
        internal static void TransformPhysics(float physicsElapsedTime, ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
        {
            PhysicsMath.TransformPhysicsInternal(physicsElapsedTime, ref transformManagerContext, ref runtimeContext);
        }

        /// <summary>
        /// Builds a standalone physics solver context from a model: creates the native context,
        /// allocates buffers, fills rigid-body simulation data, builds rigid bodies, joints, ground,
        /// and resets the simulation.
        /// </summary>
        /// <param name="model">PMX model providing rigid bodies and joints.</param>
        /// <param name="seed">Random seed used to reset the simulation.</param>
        /// <param name="enableGroundCollision">Whether to build the ground collider enabled.</param>
        /// <param name="runtimeContext">Physics solver context to initialize.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        internal static void InitializePhysicsContext(PMXModel model, uint seed, bool enableGroundCollision, ref PhysicsSolverContext runtimeContext)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            DisposePhysicsContext(ref runtimeContext);
            runtimeContext.bulletPhysicsContext = new MMDBulletPhysics(
                new float3(0.0f, -k_MMDPhysicsGravity * MMDConstants.k_MMDUnitToUnityUnit, 0.0f),
                k_SolverIterations,
                k_MaxSubSteps,
                k_FixedTimeStep);
            ResizePersistent(ref runtimeContext.rigidBodySimulationData, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.worldTransforms, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.rigidBodyIndices, model.rigidBodies.Length);

            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody source = model.rigidBodies[i];
                bool hasRelatedBone = source.relatedBoneIndex >= 0 && source.relatedBoneIndex < model.bones.Length;
                runtimeContext.rigidBodySimulationData[i] =
                    new MMDRigidBody.RigidBodySimulationData
                    {
                        rigidBodyIndex = i,
                        relatedBoneIndex = source.relatedBoneIndex,
                        groupIndex = source.groupIndex,
                        collisionGroupMask = source.collisionGroupMask,
                        shape = source.shape,
                        size = source.size,
                        position = source.position,
                        rotation = source.rotation,
                        mass = source.mass,
                        linearDamping = source.linearDamping,
                        angularDamping = source.angularDamping,
                        restitution = source.restitution,
                        friction = source.friction,
                        mode = source.mode,
                        initialTransform = float4x4.identity,
                        hasRelatedBone = hasRelatedBone,
                        boneLocalTransform = float4x4.identity,
                        boneModelPosition = hasRelatedBone ? model.bones[source.relatedBoneIndex].position : float3.zero,
                        initialWorldTransform = float4x4.identity,
                        boneTransformLevel = hasRelatedBone ? model.bones[source.relatedBoneIndex].transformLevel : -1,
                    };
            }

            PhysicsMath.ComputeRigidBodyTransforms(ref runtimeContext.rigidBodySimulationData);
            runtimeContext.bulletPhysicsContext.BuildRigidBodies(runtimeContext.rigidBodySimulationData);
            runtimeContext.bulletPhysicsContext.BuildJoints(BuildPMXJoints(model, runtimeContext.rigidBodySimulationData));
            runtimeContext.bulletPhysicsContext.BuildGround(enableGroundCollision, k_MMDGroundCollisionGroup, k_MMDGroundCollisionMask);
            ResetPhysicsContext(seed, ref runtimeContext);
        }

        /// <summary>
        /// Resets a physics solver context's native simulation with the given seed and clears its
        /// initial-pose-applied flag, if the context is valid.
        /// </summary>
        /// <param name="seed">Random seed used to reset the simulation.</param>
        /// <param name="runtimeContext">Physics solver context to reset.</param>
        internal static void ResetPhysicsContext(uint seed, ref PhysicsSolverContext runtimeContext)
        {
            if (!runtimeContext.bulletPhysicsContext.isValid)
            {
                return;
            }

            runtimeContext.bulletPhysicsContext.Reset(seed);
            runtimeContext.initialPoseApplied = false;
        }

        /// <summary>
        /// Disposes a physics solver context's native context and native arrays, resetting it to default.
        /// </summary>
        /// <param name="runtimeContext">Physics solver context to dispose.</param>
        internal static void DisposePhysicsContext(ref PhysicsSolverContext runtimeContext)
        {
            if (runtimeContext.bulletPhysicsContext.isValid)
            {
                runtimeContext.bulletPhysicsContext.Dispose();
            }

            DisposeNativeArray(ref runtimeContext.worldTransforms);
            DisposeNativeArray(ref runtimeContext.rigidBodyIndices);
            DisposeNativeArray(ref runtimeContext.rigidBodySimulationData);
            runtimeContext = default;
        }

        /// <summary>
        /// Fills a per-bone boolean mask marking bones driven by non-kinetic (physics-controlled)
        /// rigid bodies, used to select bones for physics baking.
        /// </summary>
        /// <param name="runtimeContext">Physics solver context with rigid-body simulation data.</param>
        /// <param name="result">Per-bone mask, indexed by bone index, set true for physics-controlled bones.</param>
        internal static void BuildPhysicsControlledBoneSelection(in PhysicsSolverContext runtimeContext, ref NativeArray<bool> result)
        {
            for (int i = 0; i < result.Length; ++i)
            {
                result[i] = false;
            }

            for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
            {
                MMDRigidBody.RigidBodySimulationData rigidBody =
                    runtimeContext.rigidBodySimulationData[i];
                if (rigidBody.hasRelatedBone &&
                    rigidBody.relatedBoneIndex >= 0 &&
                    rigidBody.relatedBoneIndex < result.Length &&
                    rigidBody.mode != PMXRigidBody.Mode.Kinetic)
                {
                    result[rigidBody.relatedBoneIndex] = true;
                }
            }
        }

        /// <summary>
        /// Disposes this manager's native physics context and native arrays and clears its solver state.
        /// </summary>
        public void DisposePhysics()
        {
            if (m_PhysicsSolverContext.bulletPhysicsContext.isValid)
            {
                m_PhysicsSolverContext.bulletPhysicsContext.Dispose();
                m_PhysicsSolverContext.bulletPhysicsContext = default;
            }

            m_PhysicsSolverContext.initialPoseApplied = false;
            DisposeNativeArray(ref m_PhysicsSolverContext.worldTransforms);
            DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodyIndices);
            DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodySimulationData);
        }

        private void OnDestroy()
        {
            DisposePhysics();
        }

        /// <summary>
        /// Reinitializes runtime data for all rigid bodies and joints, recomputes rigid-body
        /// transforms, and reallocates scratch buffers as needed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when a rigid-body or joint array element is null.</exception>
        internal void RebuildRuntimeData()
        {
            ResizePersistent(ref m_PhysicsSolverContext.rigidBodySimulationData, rigidBodies.Length);

            for (int i = 0; i < rigidBodies.Length; ++i)
            {
                MMDRigidBody rigidBody = rigidBodies[i];
                if (rigidBody == null)
                {
                    throw new InvalidOperationException($"MMD rigid body array element {i} is null.");
                }

                rigidBody.InitializeRuntimeData();
                m_PhysicsSolverContext.rigidBodySimulationData[i] = rigidBody.runtimeData;
            }

            PhysicsMath.ComputeRigidBodyTransforms(ref m_PhysicsSolverContext.rigidBodySimulationData);
            for (int i = 0; i < rigidBodies.Length; ++i)
            {
                rigidBodies[i].runtimeData = m_PhysicsSolverContext.rigidBodySimulationData[i];
            }

            for (int i = 0; i < joints.Length; ++i)
            {
                MMDJoint joint = joints[i];
                if (joint == null)
                {
                    throw new InvalidOperationException($"MMD joint array element {i} is null.");
                }

                joint.InitializeRuntimeData();
            }

            ReallocateArraysIfNeeded(rigidBodies.Length);
        }

        private void BuildRigidBodies()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.BuildRigidBodies(m_PhysicsSolverContext.rigidBodySimulationData);
        }

        private void BuildGround()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.BuildGround(enableGroundCollision, k_MMDGroundCollisionGroup, k_MMDGroundCollisionMask);
        }

        /// <summary>
        /// Toggles ground collision both on this manager and in the native context.
        /// </summary>
        /// <param name="enabled">Whether ground collision is active.</param>
        public void SetGroundCollisionEnabled(bool enabled)
        {
            enableGroundCollision = enabled;
            m_PhysicsSolverContext.bulletPhysicsContext.SetGroundCollisionEnabled(enabled);
        }

        private void BuildJoints()
        {
            MMDBulletPhysics.NativeJointData[] nativeJoints = new MMDBulletPhysics.NativeJointData[joints.Length];
            for (int i = 0; i < joints.Length; ++i)
            {
                nativeJoints[i] = joints[i].runtimeData;
            }

            m_PhysicsSolverContext.bulletPhysicsContext.BuildJoints(nativeJoints);
        }

        /// <summary>
        /// Pushes solved rigid-body and joint transforms onto their Unity transforms, reading them
        /// from the native simulation when <paramref name="usePhysicsTransforms"/> is set, otherwise
        /// computing them from bone matrices.
        /// </summary>
        /// <param name="transformManagerContext">Transform solver context providing bone matrices.</param>
        /// <param name="usePhysicsTransforms">Whether to read rigid-body transforms from the native simulation.</param>
        internal void UpdateTransforms(
            ref MMDTransformManager.SolverContext transformManagerContext,
            bool usePhysicsTransforms)
        {
            int rigidBodyCount = m_PhysicsSolverContext.rigidBodySimulationData.Length;

            if (usePhysicsTransforms && m_PhysicsSolverContext.bulletPhysicsContext.isValid)
            {
                for (int i = 0; i < rigidBodyCount; ++i)
                {
                    m_PhysicsSolverContext.rigidBodyIndices[i] = m_PhysicsSolverContext.rigidBodySimulationData[i].rigidBodyIndex;
                }

                m_PhysicsSolverContext.bulletPhysicsContext.GetRigidBodyMotionTransforms(
                    rigidBodyCount,
                    m_PhysicsSolverContext.rigidBodyIndices,
                    ref m_PhysicsSolverContext.worldTransforms);
            }
            else
            {
                for (int i = 0; i < rigidBodyCount; ++i)
                {
                    m_PhysicsSolverContext.worldTransforms[i] = PhysicsMath.ComputeRigidBodyWorldTransform(
                        m_PhysicsSolverContext.rigidBodySimulationData[i],
                        in transformManagerContext.boneSolverData);
                }
            }

            for (int i = 0; i < rigidBodyCount; ++i)
            {
                float4x4 worldTransform = m_PhysicsSolverContext.worldTransforms[i];
                rigidBodies[i].transform.SetPositionAndRotation(
                    worldTransform.c3.xyz,
                    new quaternion(worldTransform));
            }

            // Joints are rigidly parented to rigid body A, so their local transform is fixed at
            // build time and tracks rigid body A through the hierarchy. Do not overwrite it here:
            // the native frameInA uses rigid body A's bone-offset rest frame, a different basis
            // than the build-time local placement, so writing it displaces the joint object and
            // that displacement persists after exiting play mode.
        }

        private void ReallocateArraysIfNeeded(int count)
        {
            if (!m_PhysicsSolverContext.worldTransforms.IsCreated || m_PhysicsSolverContext.worldTransforms.Length < count)
            {
                DisposeNativeArray(ref m_PhysicsSolverContext.worldTransforms);
                DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodyIndices);
                m_PhysicsSolverContext.worldTransforms = new NativeArray<float4x4>(count, Allocator.Persistent);
                m_PhysicsSolverContext.rigidBodyIndices = new NativeArray<int>(count, Allocator.Persistent);
            }
        }

        private static void DisposeNativeArray<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }

        private static MMDBulletPhysics.NativeJointData[] BuildPMXJoints(
            PMXModel model,
            NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodies)
        {
            MMDBulletPhysics.NativeJointData[] result = new MMDBulletPhysics.NativeJointData[model.joints.Length];
            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint joint = model.joints[i];
                if (joint.type != PMXJoint.Type.Spring6DOF &&
                    joint.type != PMXJoint.Type.Generic6DOF)
                {
                    throw new NotSupportedException(
                        $"PMX joint type {joint.type} is not supported by MMD Bullet physics.");
                }
                if (joint.rigidBodyAIndex < 0 ||
                    joint.rigidBodyAIndex >= rigidBodies.Length ||
                    joint.rigidBodyBIndex < 0 ||
                    joint.rigidBodyBIndex >= rigidBodies.Length)
                {
                    throw new InvalidOperationException(
                        $"PMX joint {i} has invalid rigid body indices.");
                }

                float4x4 jointWorld = float4x4.TRS(
                    joint.position,
                    quaternion.EulerZXY(joint.rotation),
                    new float3(1.0f, 1.0f, 1.0f));
                result[i] = new MMDBulletPhysics.NativeJointData
                {
                    type = joint.type,
                    rigidBodyAIndex = joint.rigidBodyAIndex,
                    rigidBodyBIndex = joint.rigidBodyBIndex,
                    frameInA = math.mul(
                        math.inverse(
                            rigidBodies[joint.rigidBodyAIndex].initialWorldTransform),
                        jointWorld),
                    frameInB = math.mul(
                        math.inverse(
                            rigidBodies[joint.rigidBodyBIndex].initialWorldTransform),
                        jointWorld),
                    translationLimitMin = joint.translationLimitMin,
                    translationLimitMax = joint.translationLimitMax,
                    rotationLimitMin = joint.rotationLimitMin,
                    rotationLimitMax = joint.rotationLimitMax,
                    springTranslation = joint.springTranslation,
                    springRotation = joint.springRotation *
                        MMDConstants.k_MMDUnitToUnityUnit *
                        MMDConstants.k_MMDUnitToUnityUnit,
                };
            }

            return result;
        }

        private void ResetSimulatedBoneTransformsToInitial()
        {
            for (int i = 0; i < rigidBodies.Length; ++i)
            {
                MMDRigidBody rigidBody = rigidBodies[i];
                if (!IsSimulated(rigidBody.runtimeData) || rigidBody.relatedBone == null)
                {
                    continue;
                }

                ResetBoneTransformToInitial(rigidBody.relatedBone);
            }
        }

        private static bool IsSimulated(MMDRigidBody.RigidBodySimulationData rigidBodySimulationData)
        {
            return rigidBodySimulationData.mode == PMXRigidBody.Mode.Dynamic ||
                rigidBodySimulationData.mode == PMXRigidBody.Mode.DynamicBoneAligned;
        }

        private static void ResetBoneTransformToInitial(MMDBoneTransform bone)
        {
            bone.transform.localPosition = bone.initialLocalPosition;
            bone.transform.localRotation = bone.initialLocalRotation;
            bone.runtimeData = MMDBoneTransform.BoneSolverData.CreateDefault();
            bone.runtimeData.localPosition = bone.initialLocalPosition;
            bone.runtimeData.localRotation = bone.initialLocalRotation;
            bone.runtimeData.localPositionForIKLink = bone.initialLocalPosition;
            bone.runtimeData.localRotationForIKLink = bone.initialLocalRotation;
            bone.runtimeData.localMatrix = float4x4.TRS(bone.initialLocalPosition, bone.initialLocalRotation, new float3(1.0f, 1.0f, 1.0f));
            bone.runtimeData.solvedLocalPosition = bone.initialLocalPosition;
            bone.runtimeData.solvedLocalRotation = bone.initialLocalRotation;
        }

        [BurstCompile]
        private static class PhysicsMath
        {
            /// <summary>
            /// Burst implementation that precomputes each rigid body's initial, bone-local, and rest world
            /// transforms from its PMX position/rotation and owning bone model position.
            /// </summary>
            /// <param name="runtimeDataArray">Rigid-body simulation data to update in place.</param>
            [BurstCompile]
            internal static void ComputeRigidBodyTransforms(ref NativeArray<MMDRigidBody.RigidBodySimulationData> runtimeDataArray)
            {
                for (int i = 0; i < runtimeDataArray.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeDataArray[i];
                    rigidBody.initialTransform = ComputeRigidBodyInitialTransform(rigidBody.position, rigidBody.rotation, rigidBody.boneModelPosition);
                    rigidBody.boneLocalTransform = math.inverse(rigidBody.initialTransform);
                    rigidBody.initialWorldTransform = ComputeRigidBodyRestWorldTransform(rigidBody);
                    runtimeDataArray[i] = rigidBody;
                }
            }

            /// <summary>
            /// Burst implementation that advances one MMD physics step: syncs or seeds bone-driven rigid bodies,
            /// steps the native Bullet simulation when time elapses, and writes simulated transforms back to bones.
            /// </summary>
            /// <param name="elapsedTime">Elapsed time for the step; values at or below zero skip the simulation advance.</param>
            /// <param name="transformManagerContext">Bone solver context supplying and receiving bone transforms.</param>
            /// <param name="runtimeContext">Physics solver context holding rigid-body data and the native Bullet context.</param>
            [BurstCompile]
            internal static void TransformPhysicsInternal(
                float elapsedTime,
                ref MMDTransformManager.SolverContext transformManagerContext,
                ref PhysicsSolverContext runtimeContext)
            {
                if (!runtimeContext.bulletPhysicsContext.isValid)
                {
                    return;
                }

                PrepareRigidBodyRelatedBoneMatrices(ref transformManagerContext, ref runtimeContext);
                if (!runtimeContext.initialPoseApplied)
                {
                    SeedRigidBodiesFromBones(in transformManagerContext, ref runtimeContext);
                }
                else
                {
                    SyncBoneDrivenRigidBodies(in transformManagerContext, ref runtimeContext);
                }

                if (elapsedTime <= 0.0f)
                {
                    return;
                }

                runtimeContext.bulletPhysicsContext.StepSimulation(elapsedTime);
                ApplyDynamicRigidBodiesToBones(ref transformManagerContext, ref runtimeContext);
            }

            private static void SeedRigidBodiesFromBones(
                in MMDTransformManager.SolverContext transformManagerContext,
                ref PhysicsSolverContext runtimeContext)
            {
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    runtimeContext.worldTransforms[i] = ComputeRigidBodyWorldTransform(rigidBody, in transformManagerContext.boneSolverData);
                    runtimeContext.rigidBodyIndices[i] = rigidBody.rigidBodyIndex;
                }

                runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(runtimeContext.rigidBodySimulationData.Length, runtimeContext.worldTransforms, runtimeContext.rigidBodyIndices, true);

                runtimeContext.initialPoseApplied = true;
            }

            private static void SyncBoneDrivenRigidBodies(
                in MMDTransformManager.SolverContext transformManagerContext,
                ref PhysicsSolverContext runtimeContext)
            {
                int count = 0;
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    if (rigidBody.mode == PMXRigidBody.Mode.Kinetic)
                    {
                        runtimeContext.worldTransforms[count] = ComputeRigidBodyWorldTransform(rigidBody, in transformManagerContext.boneSolverData);
                        runtimeContext.rigidBodyIndices[count] = rigidBody.rigidBodyIndex;
                        ++count;
                    }
                }
                runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(count, runtimeContext.worldTransforms, runtimeContext.rigidBodyIndices, true);
            }

            private static void PrepareRigidBodyRelatedBoneMatrices(
                ref MMDTransformManager.SolverContext transformManagerContext,
                ref PhysicsSolverContext runtimeContext)
            {
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    if (rigidBody.hasRelatedBone)
                    {
                        PrepareBoneWorldMatrix(ref transformManagerContext, rigidBody.relatedBoneIndex);
                    }
                }
            }

            private static void PrepareBoneWorldMatrix(
                ref MMDTransformManager.SolverContext transformManagerContext,
                int boneIndex)
            {
                MMDBoneTransform.BoneSolverData runtimeData = transformManagerContext.boneSolverData[boneIndex];
                if (runtimeData.hasSolvedTransform)
                {
                    return;
                }

                if (runtimeData.parentBoneIndex >= 0)
                {
                    PrepareBoneWorldMatrix(ref transformManagerContext, runtimeData.parentBoneIndex);
                }

                if ((runtimeData.rotationConstraint || runtimeData.translationConstraint) && transformManagerContext.solveConstraints)
                {
                    MMDBoneTransform.UpdateLocalMatrix(ref transformManagerContext, boneIndex);
                }
                else
                {
                    float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerContext, boneIndex);
                    MMDBoneTransform.ObserveLocalMatrix(ref runtimeData, parentWorldMatrix);
                    transformManagerContext.boneSolverData[boneIndex] = runtimeData;
                }
            }

            private struct RigidBodyTransformLevelComparer : IComparer<int>
            {
                public NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodies;

                public int Compare(int x, int y)
                {
                    int boneIndexX = rigidBodies[x].relatedBoneIndex;
                    int boneIndexY = rigidBodies[y].relatedBoneIndex;
                    int transformLevelComparison = rigidBodies[x].boneTransformLevel.CompareTo(rigidBodies[y].boneTransformLevel);
                    return transformLevelComparison != 0
                        ? transformLevelComparison
                        : boneIndexX.CompareTo(boneIndexY);
                }
            }

            private static void ApplyDynamicRigidBodiesToBones(
                ref MMDTransformManager.SolverContext transformManagerContext,
                ref PhysicsSolverContext runtimeContext
            )
            {
                int count = 0;
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    if (!IsSimulated(rigidBody) || !rigidBody.hasRelatedBone)
                    {
                        continue;
                    }

                    runtimeContext.rigidBodyIndices[count] = i;
                    count++;
                }

                runtimeContext.rigidBodyIndices.GetSubArray(0, count).Sort(new RigidBodyTransformLevelComparer
                {
                    rigidBodies = runtimeContext.rigidBodySimulationData,
                });
                runtimeContext.bulletPhysicsContext.GetRigidBodyMotionTransforms(count, runtimeContext.rigidBodyIndices, ref runtimeContext.worldTransforms);

                for (int i = 0; i < count; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[runtimeContext.rigidBodyIndices[i]];
                    float4x4 boneWorldMatrix = math.mul(runtimeContext.worldTransforms[i], rigidBody.boneLocalTransform);
                    if (rigidBody.mode == PMXRigidBody.Mode.DynamicBoneAligned)
                    {
                        float3 modelTranslationDelta = ApplyKineticBoneAlignedWorldMatrixToBone(ref transformManagerContext, rigidBody.relatedBoneIndex, boneWorldMatrix);
                        ShiftKineticBoneAlignedBodyPosition(ref runtimeContext.bulletPhysicsContext, rigidBody, modelTranslationDelta);
                        continue;
                    }

                    ApplyKineticWorldMatrixToBone(ref transformManagerContext, rigidBody.relatedBoneIndex, boneWorldMatrix);
                }
            }

            /// <summary>
            /// Computes a rigid body's world transform, composing its related bone's world matrix with its
            /// initial transform when bone-related, or returning the initial transform otherwise.
            /// </summary>
            /// <param name="rigidBody">Rigid-body simulation data to transform.</param>
            /// <param name="boneSolverDataArray">Bone solver data used to resolve the related bone's world matrix.</param>
            /// <returns>The rigid body's world transform matrix.</returns>
            internal static float4x4 ComputeRigidBodyWorldTransform(
                MMDRigidBody.RigidBodySimulationData rigidBody,
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverDataArray)
            {
                return rigidBody.hasRelatedBone
                    ? math.mul(boneSolverDataArray[rigidBody.relatedBoneIndex].worldMatrix, rigidBody.initialTransform)
                    : rigidBody.initialTransform;
            }

            private static void ShiftKineticBoneAlignedBodyPosition(ref MMDBulletPhysics context, MMDRigidBody.RigidBodySimulationData rigidBody, float3 modelTranslationDelta)
            {
                context.ShiftRigidBodyPosition(rigidBody.rigidBodyIndex, modelTranslationDelta);
            }

            private static bool IsSimulated(MMDRigidBody.RigidBodySimulationData rigidBodySimulationData)
            {
                return rigidBodySimulationData.mode == PMXRigidBody.Mode.Dynamic ||
                    rigidBodySimulationData.mode == PMXRigidBody.Mode.DynamicBoneAligned;
            }

            private static void ApplyKineticWorldMatrixToBone(
                ref MMDTransformManager.SolverContext transformManagerContext,
                int boneIndex,
                float4x4 worldMatrix)
            {
                MMDBoneTransform.BoneSolverData runtimeData = transformManagerContext.boneSolverData[boneIndex];
                float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerContext, boneIndex);
                float4x4 localMatrix = math.mul(math.inverse(parentWorldMatrix), worldMatrix);
                float3 localPosition = localMatrix.c3.xyz;
                quaternion localRotation = new quaternion(localMatrix);
                MMDBoneTransform.ApplyLocalTransformToBone(ref transformManagerContext, boneIndex, localPosition, localRotation);
            }

            private static float3 ApplyKineticBoneAlignedWorldMatrixToBone(
                ref MMDTransformManager.SolverContext transformManagerData,
                int boneIndex,
                float4x4 worldMatrix)
            {
                MMDBoneTransform.BoneSolverData runtimeData = transformManagerData.boneSolverData[boneIndex];
                float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerData, boneIndex);
                float4x4 localMatrix = math.mul(math.inverse(parentWorldMatrix), worldMatrix);
                float3 localPosition = localMatrix.c3.xyz;
                float3 localTranslationDelta = localPosition - runtimeData.localPosition;
                MMDBoneTransform.ApplyLocalTransformToBone(ref transformManagerData, boneIndex, runtimeData.localPosition, new quaternion(localMatrix));
                return math.mul(parentWorldMatrix, new float4(localTranslationDelta, 0)).xyz;
            }

            private static float4x4 ComputeRigidBodyRestWorldTransform(MMDRigidBody.RigidBodySimulationData rigidBody)
            {
                if (!rigidBody.hasRelatedBone)
                {
                    return rigidBody.initialTransform;
                }

                return math.mul(float4x4.Translate(rigidBody.boneModelPosition), rigidBody.initialTransform);
            }

            private static float4x4 ComputeRigidBodyInitialTransform(float3 position, float3 rotation, float3 bonePosition)
            {
                float3 relatedPosition = position - bonePosition;
                float4x4 initialTransform = math.mul(
                    float4x4.Translate(relatedPosition),
                    float4x4.EulerZXY(rotation));
                return initialTransform;
            }
        }
    }
}
