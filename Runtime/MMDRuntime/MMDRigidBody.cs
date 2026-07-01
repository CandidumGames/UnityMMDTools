using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Per-rigid-body PMX physics data attached to either a related bone or a generated rigid-body owner object, feeding the native MMD Bullet physics pipeline.
    /// </summary>
    public sealed class MMDRigidBody : MonoBehaviour
    {
        /// <summary>Renamed (ASCII-normalized) PMX rigid-body name.</summary>
        public string renamedName;
        /// <summary>Original PMX rigid-body name.</summary>
        public string originalName;
        /// <summary>Index of this rigid body within the PMX rigid-body list.</summary>
        public int rigidBodyIndex;
        /// <summary>Bone this rigid body is attached to, or null if it has no related bone.</summary>
        public MMDBoneTransform relatedBone;
        /// <summary>Index of the related bone, or negative when there is none.</summary>
        public int relatedBoneIndex;
        /// <summary>Collision group index this rigid body belongs to.</summary>
        public byte groupIndex;
        /// <summary>Bitmask of collision groups this rigid body does not collide with.</summary>
        public short collisionGroupMask;
        /// <summary>Collider shape (sphere, box, or capsule).</summary>
        public PMXRigidBody.Shape shape;
        /// <summary>Collider dimensions in Unity units.</summary>
        public float3 size;
        /// <summary>Rigid-body position in Unity space.</summary>
        public float3 position;
        /// <summary>Rigid-body rotation (Euler) in Unity space.</summary>
        public float3 rotation;
        /// <summary>Rigid-body mass.</summary>
        public float mass;
        /// <summary>Linear velocity damping factor.</summary>
        public float linearDamping;
        /// <summary>Angular velocity damping factor.</summary>
        public float angularDamping;
        /// <summary>Bounciness (restitution) coefficient.</summary>
        public float restitution;
        /// <summary>Surface friction coefficient.</summary>
        public float friction;
        /// <summary>Physics mode: bone-following (kinetic), dynamic, or dynamic bone-aligned.</summary>
        public PMXRigidBody.Mode mode;

        /// <summary>
        /// Blittable rigid-body simulation data marshalled to the native Bullet plugin. Field order is layout-critical and must stay in sync with the native struct; do not reorder.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RigidBodySimulationData
        {
            /// <summary>Index of this rigid body within the PMX rigid-body list.</summary>
            public int rigidBodyIndex;
            /// <summary>Index of the related bone, or negative when there is none.</summary>
            public int relatedBoneIndex;
            /// <summary>Collision group index this rigid body belongs to.</summary>
            public byte groupIndex;
            /// <summary>Bitmask of collision groups this rigid body does not collide with.</summary>
            public short collisionGroupMask;
            /// <summary>Collider shape (sphere, box, or capsule).</summary>
            public PMXRigidBody.Shape shape;
            /// <summary>Collider dimensions in Unity units.</summary>
            public float3 size;
            /// <summary>Rigid-body position in Unity space.</summary>
            public float3 position;
            /// <summary>Rigid-body rotation (Euler) in Unity space.</summary>
            public float3 rotation;
            /// <summary>Rigid-body mass.</summary>
            public float mass;
            /// <summary>Linear velocity damping factor.</summary>
            public float linearDamping;
            /// <summary>Angular velocity damping factor.</summary>
            public float angularDamping;
            /// <summary>Bounciness (restitution) coefficient.</summary>
            public float restitution;
            /// <summary>Surface friction coefficient.</summary>
            public float friction;
            /// <summary>Physics mode: bone-following (kinetic), dynamic, or dynamic bone-aligned.</summary>
            public PMXRigidBody.Mode mode;
            /// <summary>Rigid-body transform relative to its related bone's model-space origin.</summary>
            public float4x4 initialTransform;
            /// <summary>Whether this rigid body has a related bone.</summary>
            public bool hasRelatedBone;
            /// <summary>Inverse of <see cref="initialTransform"/>, mapping the rigid body back to bone-local space.</summary>
            public float4x4 boneLocalTransform;
            /// <summary>Related bone's initial model-space position.</summary>
            public float3 boneModelPosition;
            /// <summary>Rigid-body rest world transform used as the joint reference frame.</summary>
            public float4x4 initialWorldTransform;
            /// <summary>MMD transform level of the related bone, or negative when there is none.</summary>
            public int boneTransformLevel;
        }

        /// <summary>
        /// Native simulation data for this rigid body, populated by <see cref="InitializeRuntimeData"/>.
        /// </summary>
        [NonSerialized] public RigidBodySimulationData runtimeData;

        /// <summary>
        /// Copies PMX rigid-body data and the related bone reference into this component.
        /// </summary>
        /// <param name="index">Index of this rigid body within the PMX rigid-body list.</param>
        /// <param name="rigidBody">Source PMX rigid-body data.</param>
        /// <param name="bone">Resolved related bone component, or null.</param>
        public void SetData(int index, PMXRigidBody rigidBody, MMDBoneTransform bone)
        {
            rigidBodyIndex = index;
            renamedName = rigidBody.renamedName.ToString();
            originalName = rigidBody.originalName.ToString();
            relatedBoneIndex = rigidBody.relatedBoneIndex;
            relatedBone = bone;
            groupIndex = rigidBody.groupIndex;
            collisionGroupMask = rigidBody.collisionGroupMask;
            shape = rigidBody.shape;
            size = rigidBody.size;
            position = rigidBody.position;
            rotation = rigidBody.rotation;
            mass = rigidBody.mass;
            linearDamping = rigidBody.linearDamping;
            angularDamping = rigidBody.angularDamping;
            restitution = rigidBody.restitution;
            friction = rigidBody.friction;
            mode = rigidBody.mode;
        }

        /// <summary>
        /// Populates <see cref="runtimeData"/> from this component's serialized fields and the related bone's initial model position and transform level.
        /// </summary>
        public void InitializeRuntimeData()
        {
            runtimeData.rigidBodyIndex = rigidBodyIndex;
            runtimeData.relatedBoneIndex = relatedBoneIndex;
            runtimeData.groupIndex = groupIndex;
            runtimeData.collisionGroupMask = collisionGroupMask;
            runtimeData.shape = shape;
            runtimeData.size = size;
            runtimeData.position = position;
            runtimeData.rotation = rotation;
            runtimeData.mass = mass;
            runtimeData.linearDamping = linearDamping;
            runtimeData.angularDamping = angularDamping;
            runtimeData.restitution = restitution;
            runtimeData.friction = friction;
            runtimeData.mode = mode;
            runtimeData.initialTransform = float4x4.identity;
            runtimeData.boneLocalTransform = float4x4.identity;
            if (relatedBone != null)
            {
                runtimeData.boneModelPosition = relatedBone.initialModelPosition;
                runtimeData.hasRelatedBone = true;
                runtimeData.boneTransformLevel = relatedBone.transformLevel;
            }
            else
            {
                runtimeData.boneModelPosition = float3.zero;
                runtimeData.hasRelatedBone = false;
                runtimeData.boneTransformLevel = -1;
            }
            runtimeData.initialWorldTransform = float4x4.identity;
        }

#if UNITY_EDITOR
        /// <summary>Wire color for kinetic (bone-following) rigid bodies.</summary>
        private static readonly Color k_KineticColor = new Color(0.0f, 1.0f, 0.0f, 1.0f);
        /// <summary>Wire color for fully dynamic rigid bodies.</summary>
        private static readonly Color k_DynamicColor = new Color(1.0f, 0.0f, 0.0f, 1.0f);
        /// <summary>Wire color for dynamic bone-aligned rigid bodies.</summary>
        private static readonly Color k_DynamicBoneAlignedColor = new Color(1.0f, 0.5f, 0.0f, 1.0f);

        /// <summary>
        /// Draws the collider shape as a wireframe gizmo in this rigid body's own transform space, color-coded by physics mode, when the object is selected.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = ModeColor(mode);
            Gizmos.matrix = transform.localToWorldMatrix;
            switch (shape)
            {
                case PMXRigidBody.Shape.Sphere:
                    Gizmos.DrawWireSphere(Vector3.zero, size.x);
                    break;
                case PMXRigidBody.Shape.Box:
                    Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, size.y, size.z) * 2.0f);
                    break;
                case PMXRigidBody.Shape.Capsule:
                    DrawWireCapsule(size.x, size.y);
                    break;
            }
        }

        private static Color ModeColor(PMXRigidBody.Mode mode)
        {
            switch (mode)
            {
                case PMXRigidBody.Mode.Dynamic:
                    return k_DynamicColor;
                case PMXRigidBody.Mode.DynamicBoneAligned:
                    return k_DynamicBoneAlignedColor;
                default:
                    return k_KineticColor;
            }
        }

        /// <summary>
        /// Draws a capsule wireframe centered at the local origin along the local Y axis, with the given hemisphere radius and cylinder height, using the current <see cref="Gizmos.matrix"/>.
        /// </summary>
        private static void DrawWireCapsule(float radius, float cylinderHeight)
        {
            float halfHeight = cylinderHeight * 0.5f;
            Vector3 top = new Vector3(0.0f, halfHeight, 0.0f);
            Vector3 bottom = new Vector3(0.0f, -halfHeight, 0.0f);

            // Side lines connecting the two hemispheres.
            Gizmos.DrawLine(top + new Vector3(radius, 0.0f, 0.0f), bottom + new Vector3(radius, 0.0f, 0.0f));
            Gizmos.DrawLine(top + new Vector3(-radius, 0.0f, 0.0f), bottom + new Vector3(-radius, 0.0f, 0.0f));
            Gizmos.DrawLine(top + new Vector3(0.0f, 0.0f, radius), bottom + new Vector3(0.0f, 0.0f, radius));
            Gizmos.DrawLine(top + new Vector3(0.0f, 0.0f, -radius), bottom + new Vector3(0.0f, 0.0f, -radius));

            DrawWireCircle(top, radius, Vector3.right, Vector3.forward);
            DrawWireCircle(bottom, radius, Vector3.right, Vector3.forward);
            DrawWireArc(top, radius, Vector3.right, Vector3.up, 180.0f);
            DrawWireArc(top, radius, Vector3.forward, Vector3.up, 180.0f);
            DrawWireArc(bottom, radius, Vector3.right, Vector3.down, 180.0f);
            DrawWireArc(bottom, radius, Vector3.forward, Vector3.down, 180.0f);
        }

        private static void DrawWireCircle(Vector3 center, float radius, Vector3 axisA, Vector3 axisB)
        {
            DrawWireArc(center, radius, axisA, axisB, 360.0f);
        }

        /// <summary>
        /// Draws a wireframe arc of <paramref name="sweepDegrees"/> in the plane spanned by <paramref name="axisA"/> and <paramref name="axisB"/>, starting along <paramref name="axisA"/>.
        /// </summary>
        private static void DrawWireArc(Vector3 center, float radius, Vector3 axisA, Vector3 axisB, float sweepDegrees)
        {
            const int k_Segments = 24;
            float step = sweepDegrees / k_Segments * Mathf.Deg2Rad;
            Vector3 previous = center + axisA * radius;
            for (int i = 1; i <= k_Segments; ++i)
            {
                float angle = step * i;
                Vector3 point = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
        }
#endif
    }
}
