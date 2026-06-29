using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using System.Runtime.InteropServices;
using static UMT.PMXUtilities;
using System.Collections.Generic;

namespace UMT
{
    /// <summary>
    /// Editor/runtime handle data pairing an IK controller bone with its target, used to expose
    /// IK chains for tooling and bookkeeping.
    /// </summary>
    [Serializable]
    public sealed class MMDIKHandleData
    {
        /// <summary>Display name of the IK handle (usually the controller bone name).</summary>
        public string name;
        /// <summary>IK controller bone.</summary>
        public MMDBoneTransform controller;
        /// <summary>IK target (end-effector) bone.</summary>
        public MMDBoneTransform target;
    }

    /// <summary>
    /// Runtime and edit-mode MMD transform solver. Samples Unity transforms, resets bones to their
    /// initial pose, solves constraints and IK, optionally runs live Bullet physics in play mode,
    /// then flushes solved transforms back to Unity. Owns the Burst/native solver caches.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [ExecuteInEditMode]
    public sealed class MMDTransformManager : MonoBehaviour
    {
        /// <summary>All bone components in PMX bone order.</summary>
        public MMDBoneTransform[] bones = Array.Empty<MMDBoneTransform>();
        /// <summary>Bones solved before physics, sorted by transform level.</summary>
        public MMDBoneTransform[] prePhysicsBones = Array.Empty<MMDBoneTransform>();
        /// <summary>Bones solved after physics, sorted by transform level.</summary>
        public MMDBoneTransform[] afterPhysicsBones = Array.Empty<MMDBoneTransform>();
        /// <summary>IK handles describing each IK controller/target pair.</summary>
        public MMDIKHandleData[] ikHandles = Array.Empty<MMDIKHandleData>();
        /// <summary>Source PMX model backing this solver.</summary>
        public PMXModel model;
        /// <summary>Companion physics manager driving rigid-body simulation, if any.</summary>
        public MMDPhysicsManager physicsManager;
        /// <summary>Master toggle enabling the per-frame transform solve.</summary>
        public bool transformEnabled = true;
        /// <summary>Whether bone constraints are solved.</summary>
        public bool solveConstraints = true;
        /// <summary>Whether IK is solved.</summary>
        public bool solveIK = true;
        /// <summary>Whether live Bullet physics runs in play mode.</summary>
        public bool livePhysics = true;
        /// <summary>Whether the solver also runs in edit mode.</summary>
        public bool solveInEditMode = false;
        /// <summary>Animator on the model root driving the bone transforms.</summary>
        public Animator animator;

        /// <summary>
        /// Native per-controller IK data: controller and target bone indices, the link range, and
        /// iteration/angle settings.
        /// </summary>
        internal struct IKControllerData
        {
            /// <summary>Index of the IK controller bone.</summary>
            public int controllerBoneIndex;
            /// <summary>Index of the IK target (end-effector) bone.</summary>
            public int targetBoneIndex;
            /// <summary>Start index of this controller's links in the shared link array.</summary>
            public int linkStartIndex;
            /// <summary>Number of links in this controller's chain.</summary>
            public int linkCount;
            /// <summary>Maximum IK solver iterations.</summary>
            public int iterations;
            /// <summary>Maximum rotation angle applied per IK step.</summary>
            public float angleLimit;
            /// <summary>Whether this IK controller is enabled.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool enabled;
        }

        /// <summary>
        /// Native per-link IK data: the link bone index and its optional angle limits, derived
        /// fix-axis, and Euler decomposition order.
        /// </summary>
        internal struct IKLinkData
        {
            /// <summary>Index of the bone this link rotates.</summary>
            public int boneIndex;
            /// <summary>Whether this link has per-axis angle limits.</summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool hasAngleLimit;
            /// <summary>Lower per-axis rotation limit, in radians.</summary>
            public float3 lowerLimit;
            /// <summary>Upper per-axis rotation limit, in radians.</summary>
            public float3 upperLimit;
            /// <summary>Axis the IK rotation is constrained to.</summary>
            public MMDIKFixAxis fixAxis;
            /// <summary>Euler decomposition order used when clamping the IK rotation.</summary>
            public MMDIKEulerOrder eulerOrder;
        }

        /// <summary>
        /// Mutable native solver state: bone solver data, sampled transforms, pass orderings, IK
        /// controllers/links, the root parent world matrix, and solve toggles.
        /// </summary>
        internal struct SolverContext
        {
            /// <summary>Per-bone solver data for every bone.</summary>
            internal NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData;
            /// <summary>Sampled local positions captured from Unity transforms.</summary>
            internal NativeArray<float3> sampledLocalPositions;
            /// <summary>Sampled local rotations captured from Unity transforms.</summary>
            internal NativeArray<quaternion> sampledLocalRotations;
            /// <summary>Bone indices solved before physics, sorted by transform level.</summary>
            internal NativeArray<int> prePhysicsBoneIndices;
            /// <summary>Bone indices solved after physics, sorted by transform level.</summary>
            internal NativeArray<int> afterPhysicsBoneIndices;
            /// <summary>Map from bone index to IK controller index, or negative when none.</summary>
            internal NativeArray<int> ikControllerByBoneIndices;
            /// <summary>All IK controllers.</summary>
            internal NativeArray<IKControllerData> ikControllers;
            /// <summary>Flattened IK link chains referenced by the controllers.</summary>
            internal NativeArray<IKLinkData> ikLinks;
            /// <summary>World matrix of the root parent (model root transform).</summary>
            internal float4x4 rootParentWorldMatrix;
            /// <summary>Whether physics has been initialized this play session.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool physicsInitialized;
            /// <summary>Whether constraints are solved this pass.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool solveConstraints;
            /// <summary>Whether IK is solved this pass.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool solveIK;
        }

        SolverContext m_RuntimeContext;

        /// <summary>Reference to this manager's mutable native solver context.</summary>
        internal ref SolverContext Context => ref m_RuntimeContext;

        private void OnEnable()
        {
            RebuildNativeCaches();
            if (Application.isPlaying && physicsManager != null)
            {
                InitializePhysics();
            }
        }

        private void OnDisable()
        {
            DisposeRuntimeData();
        }

        private void Start()
        {
            if (!solveInEditMode && !Application.isPlaying)
            {
                return;
            }

            if (ShouldRunLivePhysics())
            {
                Application.targetFrameRate = 60;
            }
            TransformAll(0.0f, true, false);
        }

        private void LateUpdate()
        {
            if (!solveInEditMode && !Application.isPlaying)
            {
                return;
            }

            if (!transformEnabled)
            {
                return;
            }

            m_RuntimeContext.solveConstraints = solveConstraints;
            m_RuntimeContext.solveIK = solveIK;

            if (ShouldRunLivePhysics() && !m_RuntimeContext.physicsInitialized)
            {
                physicsManager.ResetPhysics();
                m_RuntimeContext.physicsInitialized = true;
            }

            TransformAll(Time.deltaTime, true, ShouldRunLivePhysics());
        }

        /// <summary>
        /// Runs a full solve pass with zero physics time, using live physics when applicable.
        /// </summary>
        /// <param name="accessTransforms">Whether to sample from and flush back to Unity transforms.</param>
        public void SolveTransforms(bool accessTransforms)
        {
            TransformAll(0.0f, accessTransforms, ShouldRunLivePhysics());
        }

        /// <summary>
        /// Runs a solve pass advancing physics by a specific elapsed time, used by VMD physics baking.
        /// </summary>
        /// <param name="accessTransforms">Whether to sample from and flush back to Unity transforms.</param>
        /// <param name="physicsElapsedTime">Elapsed time to advance physics.</param>
        /// <param name="runConstraintsAndIKSolver">Whether to run physics for this pass.</param>
        public void SolveWithPhysics(bool accessTransforms, float physicsElapsedTime, bool runConstraintsAndIKSolver)
        {
            TransformAll(physicsElapsedTime, accessTransforms, runConstraintsAndIKSolver);
        }

        /// <summary>
        /// Returns whether a physics manager with at least one rigid body is available for baking.
        /// </summary>
        /// <returns>True when physics can be baked.</returns>
        internal bool HasBakePhysics()
        {
            return physicsManager != null && physicsManager.rigidBodies.Length > 0;
        }

        /// <summary>
        /// Initializes and resets the companion physics manager.
        /// </summary>
        internal void InitializePhysics()
        {
            if (physicsManager == null)
            {
                return;
            }

            physicsManager.Initialize();
            physicsManager.ResetPhysics();
        }

        /// <summary>
        /// Disposes the companion physics manager's native physics, if present.
        /// </summary>
        internal void DisposePhysics()
        {
            if (physicsManager != null)
            {
                physicsManager.DisposePhysics();
            }
        }

        /// <summary>
        /// Disposes physics and native solver caches and clears the physics-initialized flag.
        /// </summary>
        public void DisposeRuntimeData()
        {
            DisposePhysics();
            DisposeNativeCaches();
            m_RuntimeContext.physicsInitialized = false;
        }

        private bool ShouldRunLivePhysics()
        {
            return Application.isPlaying && physicsManager != null && livePhysics;
        }

        /// <summary>
        /// Sets the root parent world matrix used as the base for all bone world transforms.
        /// </summary>
        /// <param name="rootParentWorldMatrix">World matrix of the model root.</param>
        internal void UpdateRootWorldMatrix(float4x4 rootParentWorldMatrix)
        {
            m_RuntimeContext.rootParentWorldMatrix = rootParentWorldMatrix;
        }

        private void TransformAll(float physicsElapsedTime, bool accessTransforms, bool runPhysics)
        {
            if (accessTransforms)
            {
                SyncIKHandles();
                SampleBoneTransforms();
                ResetTransforms();
            }

            m_RuntimeContext.rootParentWorldMatrix = transform.localToWorldMatrix;
            if (runPhysics)
            {
                TransformBonesWithPhysics(ref m_RuntimeContext, ref physicsManager.Context, physicsElapsedTime);
            }
            else
            {
                TransformBones(ref m_RuntimeContext);
            }

            if (accessTransforms)
            {
                FlushBoneTransforms();
                if (physicsManager != null)
                {
                    physicsManager.UpdateTransforms(ref m_RuntimeContext, runPhysics);
                }
            }
        }

        /// <summary>
        /// Solves the pre-physics bone pass, steps physics, then solves the after-physics bone pass.
        /// </summary>
        /// <param name="runtimeContext">Transform solver context.</param>
        /// <param name="physicsContext">Physics solver context to advance.</param>
        /// <param name="physicsElapsedTime">Elapsed time to advance physics.</param>
        internal static void TransformBonesWithPhysics(ref SolverContext runtimeContext, ref MMDPhysicsManager.PhysicsSolverContext physicsContext, float physicsElapsedTime)
        {
            TransformMath.TransformBones(
                ref runtimeContext,
                false);
            MMDPhysicsManager.TransformPhysics(
                physicsElapsedTime,
                ref runtimeContext,
                ref physicsContext);
            TransformMath.TransformBones(
                ref runtimeContext,
                true);
        }

        /// <summary>
        /// Solves the pre-physics and after-physics bone passes without running physics.
        /// </summary>
        /// <param name="runtimeContext">Transform solver context.</param>
        internal static void TransformBones(ref SolverContext runtimeContext)
        {
            TransformMath.TransformBones(
                ref runtimeContext,
                false);
            TransformMath.TransformBones(
                ref runtimeContext,
                true);
        }

        private void SyncIKHandles()
        {
            for (int i = 0; i < m_RuntimeContext.ikControllers.Length; ++i)
            {
                IKControllerData controller = m_RuntimeContext.ikControllers[i];
                controller.enabled = bones[controller.controllerBoneIndex].ikEnabled;
                m_RuntimeContext.ikControllers[i] = controller;
            }
        }

        private void SampleBoneTransforms()
        {
            for (int i = 0; i < bones.Length; ++i)
            {
                Vector3 localPosition;
                Quaternion localRotation;
                bones[i].transform.GetLocalPositionAndRotation(out localPosition, out localRotation);
                m_RuntimeContext.sampledLocalPositions[i] = localPosition;
                m_RuntimeContext.sampledLocalRotations[i] = localRotation;

                MMDBoneTransform.BoneSolverData componentRuntimeData = bones[i].runtimeData;
                if (!componentRuntimeData.hasSolvedTransform)
                {
                    MMDBoneTransform.BoneSolverData runtimeData = m_RuntimeContext.boneSolverData[i];
                    runtimeData.hasSolvedTransform = false;
                    runtimeData.solvedByIK = false;
                    m_RuntimeContext.boneSolverData[i] = runtimeData;
                }
            }
        }

        private void ResetTransforms()
        {
            TransformMath.ResetTransformsInternal(ref m_RuntimeContext);
        }

        private void FlushBoneTransforms()
        {
            for (int i = 0; i < bones.Length; ++i)
            {
                MMDBoneTransform.BoneSolverData runtimeData = m_RuntimeContext.boneSolverData[i];
                float3 localPosition = runtimeData.hasSolvedTransform
                    ? runtimeData.solvedLocalPosition
                    : runtimeData.localPosition;
                quaternion localRotation = runtimeData.hasSolvedTransform
                    ? runtimeData.solvedLocalRotation
                    : runtimeData.localRotation;
                bones[i].transform.SetLocalPositionAndRotation(localPosition, localRotation);
                bones[i].runtimeData = runtimeData;
            }
        }

        /// <summary>
        /// Restores every bone's Unity local transform to its captured initial (bind) pose and re-seeds the native
        /// solver caches so the next solve pass starts from that pose. Use this to return a posed model to its default
        /// stance after clearing an animation.
        /// </summary>
        public void ResetToBindPose()
        {
            for (int i = 0; i < bones.Length; ++i)
            {
                MMDBoneTransform bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                bone.transform.localPosition = bone.initialLocalPosition;
                bone.transform.localRotation = bone.initialLocalRotation;
            }

            RebuildNativeCaches();
            InitializePhysics();
        }

        /// <summary>
        /// Rebuilds all native solver caches from the current bone/IK component arrays: bone solver
        /// data, sampled-transform buffers, pre/after-physics index lists, and IK controller/link tables.
        /// </summary>
        public void RebuildNativeCaches()
        {
            int ikControllerCount = 0;
            int ikLinkCount = 0;
            for (int i = 0; i < bones.Length; ++i)
            {
                if (bones[i].ik.target != null)
                {
                    ++ikControllerCount;
                    ikLinkCount += bones[i].ik.links.Length;
                }
            }

            ResizePersistent(ref m_RuntimeContext.boneSolverData, bones.Length);
            ResizePersistent(ref m_RuntimeContext.sampledLocalPositions, bones.Length);
            ResizePersistent(ref m_RuntimeContext.sampledLocalRotations, bones.Length);
            ResizePersistent(ref m_RuntimeContext.prePhysicsBoneIndices, prePhysicsBones.Length);
            ResizePersistent(ref m_RuntimeContext.afterPhysicsBoneIndices, afterPhysicsBones.Length);
            ResizePersistent(ref m_RuntimeContext.ikControllerByBoneIndices, bones.Length);
            ResizePersistent(ref m_RuntimeContext.ikControllers, ikControllerCount);
            ResizePersistent(ref m_RuntimeContext.ikLinks, ikLinkCount);

            for (int i = 0; i < bones.Length; ++i)
            {
                MMDBoneTransform bone = bones[i];
                m_RuntimeContext.boneSolverData[i] = new MMDBoneTransform.BoneSolverData(bone);
                m_RuntimeContext.ikControllerByBoneIndices[i] = -1;
            }

            for (int i = 0; i < prePhysicsBones.Length; ++i)
            {
                m_RuntimeContext.prePhysicsBoneIndices[i] = prePhysicsBones[i].boneIndex;
            }

            for (int i = 0; i < afterPhysicsBones.Length; ++i)
            {
                m_RuntimeContext.afterPhysicsBoneIndices[i] = afterPhysicsBones[i].boneIndex;
            }

            int controllerIndex = 0;
            int linkIndex = 0;
            for (int i = 0; i < bones.Length; ++i)
            {
                MMDBoneTransform bone = bones[i];
                if (bone.ik.target == null)
                {
                    continue;
                }

                m_RuntimeContext.ikControllerByBoneIndices[bone.boneIndex] = controllerIndex;
                m_RuntimeContext.ikControllers[controllerIndex] = new IKControllerData
                {
                    controllerBoneIndex = bone.boneIndex,
                    targetBoneIndex = bone.ik.target.boneIndex,
                    linkStartIndex = linkIndex,
                    linkCount = bone.ik.links.Length,
                    iterations = bone.ik.iterations,
                    angleLimit = bone.ik.angleLimit,
                    enabled = bone.ikEnabled,
                };

                for (int j = 0; j < bone.ik.links.Length; ++j)
                {
                    MMDBoneIKLinkData link = bone.ik.links[j];
                    m_RuntimeContext.ikLinks[linkIndex] = new IKLinkData
                    {
                        boneIndex = link.bone.boneIndex,
                        hasAngleLimit = link.hasAngleLimit,
                        lowerLimit = link.lowerLimit,
                        upperLimit = link.upperLimit,
                        fixAxis = link.fixAxis,
                        eulerOrder = link.eulerOrder,
                    };
                    ++linkIndex;
                }

                ++controllerIndex;
            }

            m_RuntimeContext.physicsInitialized = false;
            m_RuntimeContext.solveConstraints = true;
            m_RuntimeContext.solveIK = true;
        }

        /// <summary>
        /// Builds a standalone solver context directly from a PMX model: allocates buffers, seeds
        /// bone solver data from initial poses, splits and sorts pre/after-physics passes by
        /// transform level, and builds IK controllers/links with normalized angle limits.
        /// </summary>
        /// <param name="model">Source PMX model providing bones and IK data.</param>
        /// <param name="runtimeContext">Solver context to initialize.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        internal static void InitializeSolverContext(
            PMXModel model,
            ref SolverContext runtimeContext)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            DisposeSolverContext(ref runtimeContext);

            int ikControllerCount = 0;
            int ikLinkCount = 0;
            NativeList<int> prePhysicsIndices = new NativeList<int>(model.bones.Length, Allocator.Persistent);
            NativeList<int> afterPhysicsIndices = new NativeList<int>(model.bones.Length, Allocator.Persistent);
            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone bone = model.bones[i];
                if ((bone.flags & PMXBone.Flags.AfterPhysics) != 0)
                {
                    afterPhysicsIndices.Add(i);
                }
                else
                {
                    prePhysicsIndices.Add(i);
                }

                if ((bone.flags & PMXBone.Flags.IK) != 0 && bone.ik != null)
                {
                    ++ikControllerCount;
                    ikLinkCount += bone.ik.links.Length;
                }
            }

            ResizePersistent(ref runtimeContext.boneSolverData, model.bones.Length);
            ResizePersistent(ref runtimeContext.sampledLocalPositions, model.bones.Length);
            ResizePersistent(ref runtimeContext.sampledLocalRotations, model.bones.Length);
            ResizePersistent(ref runtimeContext.prePhysicsBoneIndices, prePhysicsIndices.Length);
            ResizePersistent(ref runtimeContext.afterPhysicsBoneIndices, afterPhysicsIndices.Length);
            ResizePersistent(ref runtimeContext.ikControllerByBoneIndices, model.bones.Length);
            ResizePersistent(ref runtimeContext.ikControllers, ikControllerCount);
            ResizePersistent(ref runtimeContext.ikLinks, ikLinkCount);

            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone bone = model.bones[i];
                float3 initialLocalPosition = bone.parentBoneIndex >= 0 ? bone.position - model.bones[bone.parentBoneIndex].position : bone.position;
                FixedString32Bytes boneName = default;
                boneName.CopyFromTruncated(bone.originalName);
                MMDBoneTransform.BoneSolverData runtimeData = new MMDBoneTransform.BoneSolverData(
                    boneName,
                    bone.position,
                    initialLocalPosition,
                    quaternion.identity,
                    bone.constraintInfluence,
                    bone.constraintTargetIndex,
                    (bone.flags & PMXBone.Flags.LocalConstraint) != 0,
                    (bone.flags & PMXBone.Flags.RotationConstraint) != 0,
                    (bone.flags & PMXBone.Flags.TranslationConstraint) != 0,
                    (bone.flags & PMXBone.Flags.Translatable) != 0,
                    (bone.flags & PMXBone.Flags.Rotatable) != 0,
                    bone.parentBoneIndex);
                runtimeData.localPosition = initialLocalPosition;
                runtimeData.localRotation = quaternion.identity;
                runtimeData.localPositionForIKLink = initialLocalPosition;
                runtimeData.localRotationForIKLink = quaternion.identity;
                runtimeData.solvedLocalPosition = initialLocalPosition;
                runtimeData.solvedLocalRotation = quaternion.identity;
                runtimeContext.boneSolverData[i] = runtimeData;
                runtimeContext.sampledLocalPositions[i] = initialLocalPosition;
                runtimeContext.sampledLocalRotations[i] = quaternion.identity;
                runtimeContext.ikControllerByBoneIndices[i] = -1;
            }

            runtimeContext.prePhysicsBoneIndices.CopyFrom(prePhysicsIndices.AsArray());
            runtimeContext.afterPhysicsBoneIndices.CopyFrom(afterPhysicsIndices.AsArray());
            PMXBoneTransformLevelComparer transformLevelComparer = new PMXBoneTransformLevelComparer{bones = model.bones};
            runtimeContext.prePhysicsBoneIndices.Sort(transformLevelComparer);
            runtimeContext.afterPhysicsBoneIndices.Sort(transformLevelComparer);
            prePhysicsIndices.Dispose();
            afterPhysicsIndices.Dispose();

            int controllerIndex = 0;
            int linkIndex = 0;
            for (int boneIndex = 0; boneIndex < model.bones.Length; ++boneIndex)
            {
                PMXBone bone = model.bones[boneIndex];
                if ((bone.flags & PMXBone.Flags.IK) == 0 || bone.ik == null)
                {
                    continue;
                }

                runtimeContext.ikControllerByBoneIndices[boneIndex] = controllerIndex;
                runtimeContext.ikControllers[controllerIndex] = new IKControllerData
                {
                    controllerBoneIndex = boneIndex,
                    targetBoneIndex = bone.ik.targetBoneIndex,
                    linkStartIndex = linkIndex,
                    linkCount = bone.ik.links.Length,
                    iterations = bone.ik.iterations,
                    angleLimit = bone.ik.angleLimit,
                    enabled = true,
                };

                for (int i = 0; i < bone.ik.links.Length; ++i)
                {
                    PMXIKLink sourceLink = bone.ik.links[i];
                    float3 lowerLimit = sourceLink.lowerLimit;
                    float3 upperLimit = sourceLink.upperLimit;
                    MMDIKFixAxis fixAxis = MMDIKFixAxis.None;
                    MMDIKEulerOrder eulerOrder = MMDIKEulerOrder.ZXY;
                    if (sourceLink.hasAngleLimit)
                    {
                        NormalizePMXIKLimit(
                            sourceLink,
                            out lowerLimit,
                            out upperLimit,
                            out fixAxis,
                            out eulerOrder);
                    }
                    runtimeContext.ikLinks[linkIndex] = new IKLinkData
                    {
                        boneIndex = sourceLink.boneIndex,
                        hasAngleLimit = sourceLink.hasAngleLimit,
                        lowerLimit = lowerLimit,
                        upperLimit = upperLimit,
                        fixAxis = fixAxis,
                        eulerOrder = eulerOrder,
                    };
                    ++linkIndex;
                }

                ++controllerIndex;
            }

            runtimeContext.rootParentWorldMatrix = float4x4.identity;
            runtimeContext.physicsInitialized = false;
            runtimeContext.solveConstraints = true;
            runtimeContext.solveIK = true;
        }

        /// <summary>
        /// Resets every bone in the solver context to its sampled or initial transform.
        /// </summary>
        /// <param name="runtimeContext">Solver context to reset.</param>
        internal static void ResetSolverContext(ref SolverContext runtimeContext)
        {
            TransformMath.ResetTransformsInternal(ref runtimeContext);
        }

        /// <summary>
        /// Sets both the constraint-solving and IK-solving toggles on a solver context.
        /// </summary>
        /// <param name="runtimeContext">Solver context to update.</param>
        /// <param name="solve">Whether constraints and IK are solved.</param>
        internal static void SetSolveConstraintsAndIK(
            ref SolverContext runtimeContext,
            bool solve)
        {
            runtimeContext.solveConstraints = solve;
            runtimeContext.solveIK = solve;
        }

        /// <summary>
        /// Disposes all native arrays in a solver context and resets it to default.
        /// </summary>
        /// <param name="runtimeContext">Solver context to dispose.</param>
        internal static void DisposeSolverContext(ref SolverContext runtimeContext)
        {
            DisposeNativeArray(ref runtimeContext.boneSolverData);
            DisposeNativeArray(ref runtimeContext.sampledLocalPositions);
            DisposeNativeArray(ref runtimeContext.sampledLocalRotations);
            DisposeNativeArray(ref runtimeContext.prePhysicsBoneIndices);
            DisposeNativeArray(ref runtimeContext.afterPhysicsBoneIndices);
            DisposeNativeArray(ref runtimeContext.ikControllerByBoneIndices);
            DisposeNativeArray(ref runtimeContext.ikControllers);
            DisposeNativeArray(ref runtimeContext.ikLinks);
            runtimeContext = default;
        }

        private void DisposeNativeCaches()
        {
            DisposeNativeArray(ref m_RuntimeContext.boneSolverData);
            DisposeNativeArray(ref m_RuntimeContext.sampledLocalPositions);
            DisposeNativeArray(ref m_RuntimeContext.sampledLocalRotations);
            DisposeNativeArray(ref m_RuntimeContext.prePhysicsBoneIndices);
            DisposeNativeArray(ref m_RuntimeContext.afterPhysicsBoneIndices);
            DisposeNativeArray(ref m_RuntimeContext.ikControllerByBoneIndices);
            DisposeNativeArray(ref m_RuntimeContext.ikControllers);
            DisposeNativeArray(ref m_RuntimeContext.ikLinks);
        }

        private static void DisposeNativeArray<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }

        private static void NormalizePMXIKLimit(
            PMXIKLink link,
            out float3 lowerLimit,
            out float3 upperLimit,
            out MMDIKFixAxis fixAxis,
            out MMDIKEulerOrder eulerOrder)
        {
            lowerLimit = math.min(link.lowerLimit, link.upperLimit);
            upperLimit = math.max(link.lowerLimit, link.upperLimit);

            if (-math.PI * 0.5f < lowerLimit.x && upperLimit.x < math.PI * 0.5f)
            {
                eulerOrder = MMDIKEulerOrder.ZXY;
            }
            else if (-math.PI * 0.5f < lowerLimit.y && upperLimit.y < math.PI * 0.5f)
            {
                eulerOrder = MMDIKEulerOrder.XYZ;
            }
            else
            {
                eulerOrder = MMDIKEulerOrder.YZX;
            }

            if (math.all(lowerLimit == float3.zero) && math.all(upperLimit == float3.zero))
            {
                fixAxis = MMDIKFixAxis.Fix;
            }
            else if (lowerLimit.y == 0.0f && upperLimit.y == 0.0f &&
                lowerLimit.z == 0.0f && upperLimit.z == 0.0f)
            {
                fixAxis = MMDIKFixAxis.X;
            }
            else if (lowerLimit.x == 0.0f && upperLimit.x == 0.0f &&
                lowerLimit.z == 0.0f && upperLimit.z == 0.0f)
            {
                fixAxis = MMDIKFixAxis.Y;
            }
            else if (lowerLimit.x == 0.0f && upperLimit.x == 0.0f &&
                lowerLimit.y == 0.0f && upperLimit.y == 0.0f)
            {
                fixAxis = MMDIKFixAxis.Z;
            }
            else
            {
                fixAxis = MMDIKFixAxis.None;
            }
        }

        private struct PMXBoneTransformLevelComparer : IComparer<int>
        {
            public PMXBone[] bones;

            public int Compare(int left, int right)
            {
                int levelComparison = bones[left].transformLevel.CompareTo(bones[right].transformLevel);
                return levelComparison != 0 ? levelComparison : left.CompareTo(right);
            }
        }

        [BurstCompile]
        private static class TransformMath
        {
            /// <summary>
            /// Burst implementation that solves a full bone pass in MMD transform order, applying constraints
            /// and IK as enabled, for either the pre-physics or after-physics pass.
            /// </summary>
            /// <param name="runtimeContext">Solver context holding bone, IK, and pass-ordering data.</param>
            /// <param name="afterPhysics">When <c>true</c> processes the after-physics bone pass; otherwise the pre-physics pass.</param>
            [BurstCompile]
            internal static void TransformBones(
                ref SolverContext runtimeContext,
                bool afterPhysics)
            {
                ref NativeArray<int> transformBoneIndices = ref runtimeContext.prePhysicsBoneIndices;
                if (afterPhysics)
                {
                    transformBoneIndices = ref runtimeContext.afterPhysicsBoneIndices;
                }

                ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverDataArray = ref runtimeContext.boneSolverData;
                ref NativeArray<IKControllerData> ikControllers = ref runtimeContext.ikControllers;
                ref NativeArray<IKLinkData> ikLinks = ref runtimeContext.ikLinks;
                ref NativeArray<int> ikControllerByBoneIndices = ref runtimeContext.ikControllerByBoneIndices;
                float4x4 rootParentWorldMatrix = runtimeContext.rootParentWorldMatrix;

                for (int i = 0; i < transformBoneIndices.Length; ++i)
                {
                    int boneIndex = transformBoneIndices[i];
                    MMDBoneTransform.BoneSolverData runtimeData = boneSolverDataArray[boneIndex];
                    if (!runtimeData.hasSolvedTransform)
                    {
                        if ((runtimeData.rotationConstraint || runtimeData.translationConstraint) && runtimeContext.solveConstraints)
                        {
                            MMDBoneTransform.UpdateLocalMatrix(ref runtimeContext, boneIndex);
                        }
                        else
                        {
                            float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref runtimeContext, boneIndex);
                            MMDBoneTransform.ObserveLocalMatrix(ref runtimeData, parentWorldMatrix);
                            boneSolverDataArray[boneIndex] = runtimeData;
                        }
                    }

                    if (runtimeContext.solveIK)
                    {
                        int ikControllerIndex = ikControllerByBoneIndices[boneIndex];
                        if (ikControllerIndex >= 0)
                        {
                            TransformIK(ref runtimeContext, ikControllerIndex);
                        }
                    }
                }
            }

            /// <summary>
            /// Burst implementation that resets every bone's solver data back toward its initial pose using the
            /// sampled local transforms captured for the current frame.
            /// </summary>
            /// <param name="runtimeContext">Solver context holding bone solver data and sampled transforms.</param>
            [BurstCompile]
            internal static void ResetTransformsInternal(
                ref SolverContext runtimeContext)
            {
                for (int i = 0; i < runtimeContext.boneSolverData.Length; ++i)
                {
                    MMDBoneTransform.BoneSolverData runtimeData = runtimeContext.boneSolverData[i];
                    MMDBoneTransform.ResetRuntimeData(ref runtimeData, runtimeContext.sampledLocalPositions[i], runtimeContext.sampledLocalRotations[i]);
                    runtimeContext.boneSolverData[i] = runtimeData;
                }
            }

            private static void TransformIK(
                ref SolverContext runtimeContext,
                int ikControllerIndex)
            {
                ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverDataArray = ref runtimeContext.boneSolverData;
                ref NativeArray<IKControllerData> ikControllers = ref runtimeContext.ikControllers;
                ref NativeArray<IKLinkData> ikLinks = ref runtimeContext.ikLinks;

                IKControllerData controller = ikControllers[ikControllerIndex];
                if (!controller.enabled || controller.targetBoneIndex < 0)
                {
                    return;
                }

                for (int i = 0; i < controller.linkCount; ++i)
                {
                    IKLinkData link = ikLinks[controller.linkStartIndex + i];
                    MMDBoneTransform.BoneSolverData linkRuntimeData = boneSolverDataArray[link.boneIndex];
                    linkRuntimeData.ikRotation = quaternion.identity;
                    boneSolverDataArray[link.boneIndex] = linkRuntimeData;
                }

                MMDBoneTransform.BoneSolverData controllerRuntimeData = boneSolverDataArray[controller.controllerBoneIndex];
                float3 goalPosition = ModelPosition(in controllerRuntimeData);
                MMDBoneTransform.UpdateLocalMatrix(ref runtimeContext, controller.targetBoneIndex);
                MMDBoneTransform.BoneSolverData targetRuntimeData = boneSolverDataArray[controller.targetBoneIndex];
                float3 effectorPosition = ModelPosition(in targetRuntimeData);
                if (math.lengthsq(goalPosition - effectorPosition) <= 0.00000001f)
                {
                    return;
                }
                RecalculateBaseIKChain(ref runtimeContext, in controller, controller.linkCount - 1);
                targetRuntimeData = boneSolverDataArray[controller.targetBoneIndex];
                if (math.lengthsq(goalPosition - ModelPosition(in targetRuntimeData)) <= 0.00000001f)
                {
                    return;
                }

                for (int iteration = 0; iteration < controller.iterations; ++iteration)
                {
                    for (int linkIndex = 0; linkIndex < controller.linkCount; ++linkIndex)
                    {
                        IKLinkData link = ikLinks[controller.linkStartIndex + linkIndex];
                        if (link.fixAxis == MMDIKFixAxis.Fix)
                        {
                            continue;
                        }

                        SolveIKLinks(ref runtimeContext, in controller, in goalPosition, linkIndex, iteration < controller.iterations / 2);
                        targetRuntimeData = runtimeContext.boneSolverData[controller.targetBoneIndex];
                        if (math.lengthsq(goalPosition - ModelPosition(in targetRuntimeData)) <= math.EPSILON)
                        {
                            return;
                        }
                    }
                }
            }

            private static void SolveIKLink(
                in float3 goalPosition,
                in float3 linkPosition,
                in float3 effectorPosition,
                in float4x4 parentWorldMatrix,
                in MMDBoneTransform.BoneSolverData linkData,
                bool hasAngleLimit,
                in float3 lowerLimit,
                in float3 upperLimit,
                MMDIKFixAxis fixAxis,
                MMDIKEulerOrder eulerOrder,
                float angleLimit,
                int linkIndex,
                bool reflectLimitedAngle,
                out quaternion solvedIKRotation,
                out bool solved)
            {
                solvedIKRotation = linkData.ikRotation;
                solved = false;
                float3 effectorToLink = linkPosition - effectorPosition;
                float3 goalToLink = linkPosition - goalPosition;
                if (math.lengthsq(effectorToLink) <= math.EPSILON || math.lengthsq(goalToLink) <= math.EPSILON)
                {
                    return;
                }

                effectorToLink = math.normalize(effectorToLink);
                goalToLink = math.normalize(goalToLink);
                float3 axis = math.cross(effectorToLink, goalToLink);
                if (math.lengthsq(axis) <= math.EPSILON && hasAngleLimit)
                {
                    return;
                }

                if (math.lengthsq(axis) > math.EPSILON)
                {
                    axis = math.normalize(axis);
                }
                float4x4 parentMatrix = parentWorldMatrix;
                parentMatrix.c3 = new float4(0.0f, 0.0f, 0.0f, 1.0f);
                float4x4 inverseParentWorldMatrix = math.transpose(parentMatrix);
                float3 localAxis = math.rotate(inverseParentWorldMatrix, axis);
                if (math.lengthsq(localAxis) > math.EPSILON)
                {
                    localAxis = math.normalize(localAxis);
                }

                quaternion localDelta;
                if (!hasAngleLimit)
                {
                    float dot = math.clamp(math.dot(effectorToLink, goalToLink), -1.0f, 1.0f);
                    float angle = math.min(math.acos(dot), angleLimit);
                    if (math.lengthsq(localAxis) <= math.EPSILON)
                    {
                        localAxis = FindDeterministicLocalAxis(math.rotate(inverseParentWorldMatrix, effectorToLink));
                    }
                    localDelta = quaternion.AxisAngle(localAxis, angle);
                }
                else
                {
                    localAxis = LimitIKAxis(axis, parentMatrix, localAxis, fixAxis);
                    float dot = math.clamp(math.dot(effectorToLink, goalToLink), -1.0f, 1.0f);
                    float angle = math.min(math.acos(dot), angleLimit * (linkIndex + 1));
                    localDelta = quaternion.AxisAngle(localAxis, angle);
                }

                quaternion currentLocalRotation = NormalizeRotation(math.mul(linkData.localRotationForIKLink, linkData.ikRotation));
                quaternion nextLocalRotation = NormalizeRotation(math.mul(localDelta, currentLocalRotation));
                solvedIKRotation = NormalizeRotation(math.mul(math.inverse(linkData.localRotationForIKLink), nextLocalRotation));
                if (hasAngleLimit)
                {
                    solvedIKRotation = LimitAngle(linkData.localRotationForIKLink, solvedIKRotation, lowerLimit, upperLimit, eulerOrder, reflectLimitedAngle);
                }

                solved = true;
            }

            private static void SolveIKLinks(
                ref SolverContext runtimeContext,
                in IKControllerData controller,
                in float3 goalPosition,
                int linkIndex,
                bool reflectLimitedAngle)
            {
                IKLinkData link = runtimeContext.ikLinks[controller.linkStartIndex + linkIndex];
                MMDBoneTransform.BoneSolverData linkRuntimeData = runtimeContext.boneSolverData[link.boneIndex];
                float3 linkPosition = ModelPosition(in linkRuntimeData);
                float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref runtimeContext, link.boneIndex);
                MMDBoneTransform.BoneSolverData targetRuntimeData = runtimeContext.boneSolverData[controller.targetBoneIndex];
                float3 effectorPosition = ModelPosition(in targetRuntimeData);
                SolveIKLink(
                    in goalPosition,
                    in linkPosition,
                    in effectorPosition,
                    in parentWorldMatrix,
                    in linkRuntimeData,
                    link.hasAngleLimit,
                    in link.lowerLimit,
                    in link.upperLimit,
                    link.fixAxis,
                    link.eulerOrder,
                    controller.angleLimit,
                    linkIndex,
                    reflectLimitedAngle,
                    out quaternion solvedIKRotation,
                    out bool solved);
                if (solved)
                {
                    linkRuntimeData.ikRotation = solvedIKRotation;
                    runtimeContext.boneSolverData[link.boneIndex] = linkRuntimeData;
                    RecalculateIKChain(ref runtimeContext, in controller, linkIndex);
                }
            }

            private static float3 ModelPosition(in MMDBoneTransform.BoneSolverData runtimeData)
            {
                return math.transform(runtimeData.worldMatrix, float3.zero);
            }

            private static void RecalculateBaseIKChain(
                ref SolverContext runtimeContext,
                in IKControllerData controller,
                int linkIndex)
            {
                for (int i = linkIndex; i >= 0; --i)
                {
                    IKLinkData link = runtimeContext.ikLinks[controller.linkStartIndex + i];
                    MMDBoneTransform.UpdateLocalMatrix(ref runtimeContext, link.boneIndex);
                }

                MMDBoneTransform.UpdateLocalMatrix(ref runtimeContext, controller.targetBoneIndex);
            }

            private static float3 LimitIKAxis(float3 modelAxis, float4x4 parentWorldMatrix, float3 localAxis, MMDIKFixAxis fixAxis)
            {
                switch (fixAxis)
                {
                    case MMDIKFixAxis.X:
                        return math.dot(modelAxis, ParentAxisX(parentWorldMatrix)) >= 0.0f ? new float3(1.0f, 0.0f, 0.0f) : new float3(-1.0f, 0.0f, 0.0f);
                    case MMDIKFixAxis.Y:
                        return math.dot(modelAxis, ParentAxisY(parentWorldMatrix)) >= 0.0f ? new float3(0.0f, 1.0f, 0.0f) : new float3(0.0f, -1.0f, 0.0f);
                    case MMDIKFixAxis.Z:
                        return math.dot(modelAxis, ParentAxisZ(parentWorldMatrix)) >= 0.0f ? new float3(0.0f, 0.0f, 1.0f) : new float3(0.0f, 0.0f, -1.0f);
                    default:
                        return localAxis;
                }
            }

            private static float3 ParentAxisX(float4x4 matrix)
            {
                return matrix.c0.xyz;
            }

            private static float3 ParentAxisY(float4x4 matrix)
            {
                return matrix.c1.xyz;
            }

            private static float3 ParentAxisZ(float4x4 matrix)
            {
                return matrix.c2.xyz;
            }

            private static float3 FindDeterministicLocalAxis(float3 localDirection)
            {
                localDirection = math.normalize(localDirection);
                float3 referenceAxis = math.abs(localDirection.x) < 0.75f ? new float3(1.0f, 0.0f, 0.0f) : new float3(0.0f, 1.0f, 0.0f);
                return math.normalize(math.cross(localDirection, referenceAxis));
            }

            private static quaternion LimitAngle(quaternion baseRotation, quaternion ikRotation, float3 lowerLimit, float3 upperLimit, MMDIKEulerOrder eulerOrder, bool reflectLimitedAngle)
            {
                quaternion limitedLocalRotation = NormalizeRotation(math.mul(baseRotation, ikRotation));
                float3 euler = DecomposeLimitedRotation(limitedLocalRotation, eulerOrder);
                euler.x = LimitAngleAxis(euler.x, lowerLimit.x, upperLimit.x, reflectLimitedAngle);
                euler.y = LimitAngleAxis(euler.y, lowerLimit.y, upperLimit.y, reflectLimitedAngle);
                euler.z = LimitAngleAxis(euler.z, lowerLimit.z, upperLimit.z, reflectLimitedAngle);
                quaternion recomposedRotation = ComposeLimitedRotation(euler, eulerOrder);
                return NormalizeRotation(math.mul(math.inverse(baseRotation), recomposedRotation));
            }

            private static float3 DecomposeLimitedRotation(quaternion rotation, MMDIKEulerOrder eulerOrder)
            {
                float4x4 matrix = new float4x4(rotation, float3.zero);
                float3 angle = float3.zero;
                switch (eulerOrder)
                {
                    case MMDIKEulerOrder.ZXY:
                        {
                            angle.x = ClampAsinInput(matrix.c1.z);
                            float inverseCos = InverseCosForLimitedEuler(angle.x);
                            angle.y = math.atan2(-matrix.c0.z * inverseCos, matrix.c2.z * inverseCos);
                            angle.z = math.atan2(-matrix.c1.x * inverseCos, matrix.c1.y * inverseCos);
                            return angle;
                        }
                    case MMDIKEulerOrder.XYZ:
                        {
                            angle.y = ClampAsinInput(matrix.c2.x);
                            float inverseCos = InverseCosForLimitedEuler(angle.y);
                            angle.x = math.atan2(-matrix.c2.y * inverseCos, matrix.c2.z * inverseCos);
                            angle.z = math.atan2(-matrix.c1.x * inverseCos, matrix.c0.x * inverseCos);
                            return angle;
                        }
                    default:
                        {
                            angle.z = ClampAsinInput(matrix.c0.y);
                            float inverseCos = InverseCosForLimitedEuler(angle.z);
                            angle.x = math.atan2(-matrix.c2.y * inverseCos, matrix.c1.y * inverseCos);
                            angle.y = math.atan2(-matrix.c0.z * inverseCos, matrix.c0.x * inverseCos);
                            return angle;
                        }
                }
            }

            private static quaternion ComposeLimitedRotation(float3 angle, MMDIKEulerOrder eulerOrder)
            {
                quaternion x = quaternion.AxisAngle(new float3(1.0f, 0.0f, 0.0f), angle.x);
                quaternion y = quaternion.AxisAngle(new float3(0.0f, 1.0f, 0.0f), angle.y);
                quaternion z = quaternion.AxisAngle(new float3(0.0f, 0.0f, 1.0f), angle.z);
                switch (eulerOrder)
                {
                    case MMDIKEulerOrder.ZXY:
                        return NormalizeRotation(math.mul(math.mul(z, x), y));
                    case MMDIKEulerOrder.XYZ:
                        return NormalizeRotation(math.mul(math.mul(x, y), z));
                    default:
                        return NormalizeRotation(math.mul(math.mul(y, z), x));
                }
            }

            private static float ClampAsinInput(float value)
            {
                float angle = math.asin(math.clamp(value, -1.0f, 1.0f));
                if (math.abs(angle) > 1.535889f)
                {
                    angle = angle < 0.0f ? -1.535889f : 1.535889f;
                }
                return angle;
            }

            private static float InverseCosForLimitedEuler(float angle)
            {
                float cosine = math.cos(angle);
                return cosine != 0.0f ? 1.0f / cosine : 0.0f;
            }

            private static float LimitAngleAxis(float angle, float lowerLimit, float upperLimit, bool reflectLimitedAngle)
            {
                if (angle < lowerLimit)
                {
                    float reflected = 2.0f * lowerLimit - angle;
                    return reflected <= upperLimit && reflectLimitedAngle ? reflected : lowerLimit;
                }
                if (angle > upperLimit)
                {
                    float reflected = 2.0f * upperLimit - angle;
                    return reflected >= lowerLimit && reflectLimitedAngle ? reflected : upperLimit;
                }
                return angle;
            }

            private static quaternion NormalizeRotation(quaternion rotation)
            {
                return math.normalize(rotation);
            }

            private static void RecalculateIKChain(
                ref SolverContext runtimeContext,
                in IKControllerData controller,
                int linkIndex)
            {
                for (int i = linkIndex; i >= 0; --i)
                {
                    IKLinkData link = runtimeContext.ikLinks[controller.linkStartIndex + i];
                    MMDBoneTransform.BoneSolverData runtimeData = runtimeContext.boneSolverData[link.boneIndex];
                    float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref runtimeContext, link.boneIndex);
                    MMDBoneTransform.UpdateLocalMatrixIKLink(ref runtimeData, parentWorldMatrix);
                    runtimeContext.boneSolverData[link.boneIndex] = runtimeData;
                }

                MMDBoneTransform.UpdateLocalMatrix(ref runtimeContext, controller.targetBoneIndex);
            }
        }
    }
}
