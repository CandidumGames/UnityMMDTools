using System;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Per-joint PMX physics data attached to a generated joint object, linking two <see cref="MMDRigidBody"/> instances for the native MMD Bullet physics pipeline.
    /// </summary>
    public sealed class MMDJoint : MonoBehaviour
    {
        /// <summary>Renamed (ASCII-normalized) PMX joint name.</summary>
        public string renamedName;
        /// <summary>Original PMX joint name.</summary>
        public string originalName;
        /// <summary>Index of this joint within the PMX joint list.</summary>
        public int jointIndex;
        /// <summary>PMX joint type; only spring/generic 6DOF types are supported by MMD Bullet physics.</summary>
        public PMXJoint.Type type;
        /// <summary>First rigid body connected by this joint.</summary>
        public MMDRigidBody rigidBodyA;
        /// <summary>Second rigid body connected by this joint.</summary>
        public MMDRigidBody rigidBodyB;
        /// <summary>Index of the first connected rigid body.</summary>
        public int rigidBodyAIndex;
        /// <summary>Index of the second connected rigid body.</summary>
        public int rigidBodyBIndex;
        /// <summary>Joint world position in Unity space.</summary>
        public float3 position;
        /// <summary>Joint rotation (Euler ZXY) in Unity space.</summary>
        public float3 rotation;
        /// <summary>Minimum translation limit per axis.</summary>
        public float3 translationLimitMin;
        /// <summary>Maximum translation limit per axis.</summary>
        public float3 translationLimitMax;
        /// <summary>Minimum rotation limit per axis.</summary>
        public float3 rotationLimitMin;
        /// <summary>Maximum rotation limit per axis.</summary>
        public float3 rotationLimitMax;
        /// <summary>Translation spring stiffness per axis.</summary>
        public float3 springTranslation;
        /// <summary>Rotation spring stiffness per axis.</summary>
        public float3 springRotation;

        /// <summary>
        /// Native joint data marshalled to the Bullet physics plugin, rebuilt by <see cref="InitializeRuntimeData"/>.
        /// </summary>
        [NonSerialized] public MMDBulletPhysics.NativeJointData runtimeData;

        /// <summary>
        /// Copies PMX joint data and rigid-body references into this component.
        /// </summary>
        /// <param name="index">Index of this joint within the PMX joint list.</param>
        /// <param name="joint">Source PMX joint data.</param>
        /// <param name="firstRigidBody">Resolved first rigid body component.</param>
        /// <param name="secondRigidBody">Resolved second rigid body component.</param>
        public void SetData(int index, PMXJoint joint, MMDRigidBody firstRigidBody, MMDRigidBody secondRigidBody)
        {
            jointIndex = index;
            renamedName = joint.renamedName.ToString();
            originalName = joint.originalName.ToString();
            type = joint.type;
            rigidBodyA = firstRigidBody;
            rigidBodyB = secondRigidBody;
            rigidBodyAIndex = joint.rigidBodyAIndex;
            rigidBodyBIndex = joint.rigidBodyBIndex;
            position = joint.position;
            rotation = joint.rotation;
            translationLimitMin = joint.translationLimitMin;
            translationLimitMax = joint.translationLimitMax;
            rotationLimitMin = joint.rotationLimitMin;
            rotationLimitMax = joint.rotationLimitMax;
            springTranslation = joint.springTranslation;
            springRotation = joint.springRotation;
        }

        /// <summary>
        /// Builds the native <see cref="MMDBulletPhysics.NativeJointData"/> from this joint's data, computing the joint frames relative to each rigid body's initial world transform and scaling angular spring stiffness into Unity units.
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown when <see cref="type"/> is not a 6DOF joint type.</exception>
        /// <exception cref="InvalidOperationException">Thrown when either connected rigid body is missing.</exception>
        public void InitializeRuntimeData()
        {
            if (type != PMXJoint.Type.Spring6DOF && type != PMXJoint.Type.Generic6DOF)
            {
                throw new NotSupportedException($"PMX joint type {type} is not supported by MMD Bullet physics.");
            }
            if (rigidBodyA == null)
            {
                throw new InvalidOperationException($"MMD joint {jointIndex} does not reference its first rigid body.");
            }
            if (rigidBodyB == null)
            {
                throw new InvalidOperationException($"MMD joint {jointIndex} does not reference its second rigid body.");
            }

            float4x4 jointWorld = float4x4.TRS(position, quaternion.EulerZXY(rotation), new float3(1.0f, 1.0f, 1.0f));
            runtimeData = new MMDBulletPhysics.NativeJointData
            {
                type = type,
                rigidBodyAIndex = rigidBodyAIndex,
                rigidBodyBIndex = rigidBodyBIndex,
                frameInA = math.mul(math.inverse(rigidBodyA.runtimeData.initialWorldTransform), jointWorld),
                frameInB = math.mul(math.inverse(rigidBodyB.runtimeData.initialWorldTransform), jointWorld),
                translationLimitMin = translationLimitMin,
                translationLimitMax = translationLimitMax,
                rotationLimitMin = rotationLimitMin,
                rotationLimitMax = rotationLimitMax,
                springTranslation = springTranslation,
                springRotation = new float3(ScaleAngularSpring(springRotation.x), ScaleAngularSpring(springRotation.y), ScaleAngularSpring(springRotation.z)),
            };
        }

        private static float ScaleAngularSpring(float stiffness)
        {
            return stiffness * MMDConstants.k_MMDUnitToUnityUnit * MMDConstants.k_MMDUnitToUnityUnit;
        }

#if UNITY_EDITOR
        /// <summary>Color of the joint axis cross.</summary>
        private static readonly Color k_JointColor = new Color(1.0f, 1.0f, 0.0f, 1.0f);
        /// <summary>Color of the connection lines to the linked rigid bodies.</summary>
        private static readonly Color k_LinkColor = new Color(0.0f, 0.0f, 1.0f, 1.0f);
        /// <summary>Length of each drawn joint axis, in Unity units.</summary>
        private const float k_AxisLength = 0.02f;

        /// <summary>
        /// Draws the joint as a small coordinate cross at its transform plus faded lines to the two connected rigid bodies, when the object is selected.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position;
            Gizmos.color = k_JointColor;
            //Gizmos.DrawLine(origin - transform.right * k_AxisLength, origin + transform.right * k_AxisLength);
            //Gizmos.DrawLine(origin - transform.up * k_AxisLength, origin + transform.up * k_AxisLength);
            //Gizmos.DrawLine(origin - transform.forward * k_AxisLength, origin + transform.forward * k_AxisLength);
            Matrix4x4 originalMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(Vector3.zero, Vector3.one * k_AxisLength * 0.5f);
            Gizmos.matrix = originalMatrix;
            Gizmos.color = k_LinkColor;
            if (rigidBodyA != null)
            {
                Gizmos.DrawLine(origin, rigidBodyA.transform.position);
            }
            if (rigidBodyB != null)
            {
                Gizmos.DrawLine(origin, rigidBodyB.transform.position);
            }
        }
#endif
    }
}
