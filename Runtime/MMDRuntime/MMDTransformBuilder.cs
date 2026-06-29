using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Result of building MMD transform and physics runtime components, reporting the created
    /// managers, rigid bodies, joints, and counts of bone components, IK controllers, and constraints.
    /// </summary>
    public struct MMDTransformBuildResult
    {
        /// <summary>The MMD transform manager component on the model root.</summary>
        public MMDTransformManager transformManager;
        /// <summary>The MMD physics manager component on the model root.</summary>
        public MMDPhysicsManager physicsManager;
        /// <summary>Generated rigid-body components.</summary>
        public MMDRigidBody[] rigidBodies;
        /// <summary>Generated joint components.</summary>
        public MMDJoint[] joints;
        /// <summary>Number of <see cref="MMDBoneTransform"/> components created.</summary>
        public int boneComponentCount;
        /// <summary>Number of IK controller bones found.</summary>
        public int ikControllerCount;
        /// <summary>Number of bones carrying rotation or translation constraints.</summary>
        public int constraintCount;
    }

    /// <summary>
    /// Adds and configures <see cref="MMDTransformManager"/>, <see cref="MMDPhysicsManager"/>,
    /// <see cref="MMDBoneTransform"/>, <see cref="MMDRigidBody"/>, and <see cref="MMDJoint"/>
    /// components from PMX bones, IK, constraints, rigid bodies, and joints.
    /// </summary>
    public static class MMDTransformBuilder
    {
        /// <summary>
        /// Builds the MMD transform/physics component layout on a model root: creates bone
        /// components, parent/constraint/IK links, sorted pre- and after-physics passes, IK
        /// handles, rigid bodies, joints, and an <see cref="Animator"/>.
        /// </summary>
        /// <param name="model">Source PMX model providing bone, IK, constraint, rigid-body, and joint data.</param>
        /// <param name="root">Model root GameObject that receives the manager components.</param>
        /// <param name="bones">Bone transforms in PMX bone order.</param>
        /// <returns>The build result with created components and counts.</returns>
        public static MMDTransformBuildResult Build(PMXModel model, GameObject root, Transform[] bones)
        {
            MMDTransformBuildResult result = new MMDTransformBuildResult();
            result.transformManager = root.GetComponent<MMDTransformManager>();
            if (result.transformManager == null)
            {
                result.transformManager = root.AddComponent<MMDTransformManager>();
            }

            result.physicsManager = root.GetComponent<MMDPhysicsManager>();
            if (result.physicsManager == null)
            {
                result.physicsManager = root.AddComponent<MMDPhysicsManager>();
            }

            MMDBoneTransform[] boneTransforms = new MMDBoneTransform[bones.Length];
            for (int i = 0; i < bones.Length; ++i)
            {
                PMXBone pmxBone = model.bones[i];
                MMDBoneTransform boneTransform = bones[i].GetComponent<MMDBoneTransform>();
                if (boneTransform == null)
                {
                    boneTransform = bones[i].gameObject.AddComponent<MMDBoneTransform>();
                }

                boneTransform.boneIndex = i;
                boneTransform.boneName = pmxBone.originalName.ToString();
                boneTransform.initialModelPosition = pmxBone.position;
                boneTransform.initialLocalPosition = bones[i].localPosition;
                boneTransform.initialLocalRotation = bones[i].localRotation;
                boneTransform.transformLevel = pmxBone.transformLevel;
                boneTransform.flags = pmxBone.flags;
                boneTransform.constraintInfluence  = pmxBone.constraintInfluence ;
                boneTransform.localConstraint = (pmxBone.flags & PMXBone.Flags.LocalConstraint) != 0;
                boneTransform.rotationConstraint = (pmxBone.flags & PMXBone.Flags.RotationConstraint) != 0;
                boneTransform.translationConstraint = (pmxBone.flags & PMXBone.Flags.TranslationConstraint) != 0;
                boneTransform.afterPhysics = (pmxBone.flags & PMXBone.Flags.AfterPhysics) != 0;
                (MMDBoneIKData ik, bool hasIK) = BuildIKData(pmxBone);
                boneTransform.ik = ik;
                boneTransform.ikEnabled = hasIK;
                boneTransforms[i] = boneTransform;
                ++result.boneComponentCount;

                if (boneTransform.rotationConstraint || boneTransform.translationConstraint)
                {
                    ++result.constraintCount;
                }
                if (hasIK)
                {
                    ++result.ikControllerCount;
                }
            }

            List<MMDIKHandleData> ikHandles = new List<MMDIKHandleData>();
            for (int i = 0; i < boneTransforms.Length; ++i)
            {
                PMXBone pmxBone = model.bones[i];
                boneTransforms[i].parentBone = pmxBone.parentBoneIndex >= 0 ? boneTransforms[pmxBone.parentBoneIndex] : null;
                boneTransforms[i].constraintTarget = pmxBone.constraintTargetIndex >= 0 ? boneTransforms[pmxBone.constraintTargetIndex] : null;
                if (pmxBone.ik != null)
                {
                    boneTransforms[i].ik.target = boneTransforms[pmxBone.ik.targetBoneIndex];
                    for (int linkIndex = 0; linkIndex < pmxBone.ik.links.Length; ++linkIndex)
                    {
                        boneTransforms[i].ik.links[linkIndex].bone = boneTransforms[pmxBone.ik.links[linkIndex].boneIndex];
                    }

                    ikHandles.Add(new MMDIKHandleData
                    {
                        name = pmxBone.originalName.ToString(),
                        controller = boneTransforms[i],
                        target = boneTransforms[i].ik.target,
                    });
                }
            }

            result.transformManager.bones = boneTransforms;
            result.transformManager.prePhysicsBones = BuildSortedPass(boneTransforms, false);
            result.transformManager.afterPhysicsBones = BuildSortedPass(boneTransforms, true);
            result.transformManager.ikHandles = ikHandles.ToArray();
            result.transformManager.model = model;
            result.transformManager.physicsManager = result.physicsManager;
            result.rigidBodies = BuildRigidBodies(model, root.transform, boneTransforms);
            result.joints = BuildJoints(model, result.rigidBodies);
            result.physicsManager.rigidBodies = result.rigidBodies;
            result.physicsManager.joints = result.joints;
            result.transformManager.InitializePhysics();

            root.TryGetComponent<Animator>(out Animator animator);
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }
            result.transformManager.animator = animator;
            return result;
        }

        /// <summary>
        /// Refreshes every bone's initial pose from its current transform and rebuilds the
        /// transform manager's native caches, typically after optional avatar creation.
        /// </summary>
        /// <param name="transformManager">Transform manager whose bones are refreshed.</param>
        public static void RefreshInitialTransforms(MMDTransformManager transformManager)
        {
            foreach (MMDBoneTransform boneTransform in transformManager.bones)
            {
                boneTransform.RefreshInitialTransform();
            }
            transformManager.RebuildNativeCaches();
        }

        private static (MMDBoneIKData, bool) BuildIKData(PMXBone pmxBone)
        {
            MMDBoneIKData ik = new MMDBoneIKData();
            if (!pmxBone.flags.HasFlag(PMXBone.Flags.IK))
            {
                return (ik, false);
            }

            ik.iterations = pmxBone.ik.iterations;
            ik.angleLimit = pmxBone.ik.angleLimit;
            ik.links = new MMDBoneIKLinkData[pmxBone.ik.links.Length];
            for (int i = 0; i < pmxBone.ik.links.Length; ++i)
            {
                PMXIKLink link = pmxBone.ik.links[i];
                ik.links[i] = new MMDBoneIKLinkData
                {
                    hasAngleLimit = link.hasAngleLimit,
                    lowerLimit = link.lowerLimit,
                    upperLimit = link.upperLimit,
                };
                if (link.hasAngleLimit)
                {
                    NormalizeIKLimit(ik.links[i]);
                }
            }

            return (ik, true);
        }

        private static MMDRigidBody[] BuildRigidBodies(PMXModel model, Transform root, MMDBoneTransform[] bones)
        {
            MMDRigidBody[] rigidBodies = new MMDRigidBody[model.rigidBodies.Length];

            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody pmxRigidBody = model.rigidBodies[i];
                MMDBoneTransform relatedBone = FindRelatedBone(pmxRigidBody, bones);
                Transform parent = relatedBone != null ? relatedBone.transform : root;
                MMDRigidBody rigidBody = FindRigidBodyComponent(parent, i);
                if (rigidBody == null)
                {
                    GameObject rigidBodyObject = new GameObject();
                    rigidBodyObject.transform.SetParent(parent, false);
                    rigidBody = rigidBodyObject.AddComponent<MMDRigidBody>();
                }

                rigidBody.gameObject.name = PMXUtilities.GetUniqueGeneratedObjectName(
                    parent,
                    pmxRigidBody.renamedName.ToString(),
                    "RigidBody_",
                    "RigidBody",
                    i,
                    rigidBody.transform);
                rigidBody.SetData(i, pmxRigidBody, relatedBone);
                float3 bonePosition = relatedBone != null ? relatedBone.initialModelPosition : float3.zero;
                rigidBody.transform.SetLocalPositionAndRotation(
                    pmxRigidBody.position - bonePosition,
                    quaternion.EulerZXY(pmxRigidBody.rotation));
                rigidBodies[i] = rigidBody;
            }

            return rigidBodies;
        }

        private static MMDJoint[] BuildJoints(PMXModel model, MMDRigidBody[] rigidBodies)
        {
            MMDJoint[] joints = new MMDJoint[model.joints.Length];
            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint pmxJoint = model.joints[i];
                if (pmxJoint.rigidBodyAIndex < 0 || pmxJoint.rigidBodyAIndex >= rigidBodies.Length)
                {
                    throw new InvalidOperationException(
                        $"PMX joint {i} has invalid first rigid body index {pmxJoint.rigidBodyAIndex}.");
                }
                if (pmxJoint.rigidBodyBIndex < 0 || pmxJoint.rigidBodyBIndex >= rigidBodies.Length)
                {
                    throw new InvalidOperationException(
                        $"PMX joint {i} has invalid second rigid body index {pmxJoint.rigidBodyBIndex}.");
                }

                MMDRigidBody rigidBodyA = rigidBodies[pmxJoint.rigidBodyAIndex];
                MMDRigidBody rigidBodyB = rigidBodies[pmxJoint.rigidBodyBIndex];
                MMDJoint joint = FindJointComponent(rigidBodyA.transform, i);
                if (joint == null)
                {
                    GameObject jointObject = new GameObject();
                    jointObject.transform.SetParent(rigidBodyA.transform, false);
                    joint = jointObject.AddComponent<MMDJoint>();
                }

                joint.gameObject.name = PMXUtilities.GetUniqueGeneratedObjectName(
                    rigidBodyA.transform,
                    pmxJoint.renamedName.ToString(),
                    "Joint_",
                    "Joint",
                    i,
                    joint.transform);
                joint.SetData(i, pmxJoint, rigidBodyA, rigidBodyB);

                // The joint is parented to rigid body A. Place it relative to that parent in
                // model space so the result is independent of the model root's world transform,
                // matching how rigid bodies are positioned as local offsets under their bones.
                float4x4 jointModel = float4x4.TRS(
                    pmxJoint.position,
                    quaternion.EulerZXY(pmxJoint.rotation),
                    new float3(1.0f, 1.0f, 1.0f));
                float4x4 rigidBodyAModel = float4x4.TRS(
                    model.rigidBodies[pmxJoint.rigidBodyAIndex].position,
                    quaternion.EulerZXY(model.rigidBodies[pmxJoint.rigidBodyAIndex].rotation),
                    new float3(1.0f, 1.0f, 1.0f));
                float4x4 jointLocal = math.mul(math.inverse(rigidBodyAModel), jointModel);
                joint.transform.SetLocalPositionAndRotation(
                    jointLocal.c3.xyz,
                    new quaternion(new float3x3(jointLocal.c0.xyz, jointLocal.c1.xyz, jointLocal.c2.xyz)));
                joints[i] = joint;
            }

            return joints;
        }

        private static MMDBoneTransform FindRelatedBone(PMXRigidBody rigidBody, MMDBoneTransform[] bones)
        {
            if (rigidBody.relatedBoneIndex < 0 || rigidBody.relatedBoneIndex >= bones.Length)
            {
                return null;
            }

            return bones[rigidBody.relatedBoneIndex];
        }

        private static MMDRigidBody FindRigidBodyComponent(Transform parent, int rigidBodyIndex)
        {
            for (int i = 0; i < parent.childCount; ++i)
            {
                MMDRigidBody component = parent.GetChild(i).GetComponent<MMDRigidBody>();
                if (component != null && component.rigidBodyIndex == rigidBodyIndex)
                {
                    return component;
                }
            }

            return null;
        }

        private static MMDJoint FindJointComponent(Transform parent, int jointIndex)
        {
            for (int i = 0; i < parent.childCount; ++i)
            {
                MMDJoint component = parent.GetChild(i).GetComponent<MMDJoint>();
                if (component != null && component.jointIndex == jointIndex)
                {
                    return component;
                }
            }

            return null;
        }

        private static void NormalizeIKLimit(MMDBoneIKLinkData link)
        {
            float lowerX = Mathf.Min(link.lowerLimit.x, link.upperLimit.x);
            float upperX = Mathf.Max(link.lowerLimit.x, link.upperLimit.x);
            float lowerY = Mathf.Min(link.lowerLimit.y, link.upperLimit.y);
            float upperY = Mathf.Max(link.lowerLimit.y, link.upperLimit.y);
            float lowerZ = Mathf.Min(link.lowerLimit.z, link.upperLimit.z);
            float upperZ = Mathf.Max(link.lowerLimit.z, link.upperLimit.z);
            link.lowerLimit = new Vector3(lowerX, lowerY, lowerZ);
            link.upperLimit = new Vector3(upperX, upperY, upperZ);

            if (-Mathf.PI * 0.5f < lowerX && upperX < Mathf.PI * 0.5f)
            {
                link.eulerOrder = MMDIKEulerOrder.ZXY;
            }
            else if (-Mathf.PI * 0.5f < lowerY && upperY < Mathf.PI * 0.5f)
            {
                link.eulerOrder = MMDIKEulerOrder.XYZ;
            }
            else
            {
                link.eulerOrder = MMDIKEulerOrder.YZX;
            }

            if (lowerX == 0.0f && upperX == 0.0f && lowerY == 0.0f && upperY == 0.0f && lowerZ == 0.0f && upperZ == 0.0f)
            {
                link.fixAxis = MMDIKFixAxis.Fix;
            }
            else if (lowerY == 0.0f && upperY == 0.0f && lowerZ == 0.0f && upperZ == 0.0f)
            {
                link.fixAxis = MMDIKFixAxis.X;
            }
            else if (lowerX == 0.0f && upperX == 0.0f && lowerZ == 0.0f && upperZ == 0.0f)
            {
                link.fixAxis = MMDIKFixAxis.Y;
            }
            else if (lowerX == 0.0f && upperX == 0.0f && lowerY == 0.0f && upperY == 0.0f)
            {
                link.fixAxis = MMDIKFixAxis.Z;
            }
            else
            {
                link.fixAxis = MMDIKFixAxis.None;
            }
        }

        private static MMDBoneTransform[] BuildSortedPass(MMDBoneTransform[] bones, bool afterPhysics)
        {
            List<MMDBoneTransform> sortedBones = new List<MMDBoneTransform>();
            foreach (MMDBoneTransform bone in bones)
            {
                if (bone.afterPhysics == afterPhysics)
                {
                    sortedBones.Add(bone);
                }
            }

            sortedBones.Sort(CompareBones);
            return sortedBones.ToArray();
        }

        private static int CompareBones(MMDBoneTransform x, MMDBoneTransform y)
        {
            int levelComparison = x.transformLevel.CompareTo(y.transformLevel);
            return levelComparison != 0 ? levelComparison : x.boneIndex.CompareTo(y.boneIndex);
        }
    }
}
