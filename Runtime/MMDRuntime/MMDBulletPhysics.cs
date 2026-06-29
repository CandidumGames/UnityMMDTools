using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace UMT
{
    /// <summary>
    /// P/Invoke wrapper around the native <c>UMTNativePlugin</c> plugin. Owns the native
    /// physics context and centralizes all rigid-body, joint, ground, transform, reset, and
    /// simulation-step calls into the Bullet-backed MMD physics engine.
    /// </summary>
    public struct MMDBulletPhysics : IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        // The Web platform (UNITY_WEBGL is Unity's scripting define for it, regardless of the WebGL/WebGPU graphics backend) statically links the native plugin into the player, so P/Invoke must resolve against the special "__Internal" pseudo-library rather than a DLL name.
        private const string k_DllName = "__Internal";
#else
        private const string k_DllName = "UMTNativePlugin";
#endif
        /// <summary>Whether the underlying native physics context has been created and not yet disposed.</summary>
        public bool isValid => m_NativeContext != IntPtr.Zero;
        private IntPtr m_NativeContext;

        /// <summary>
        /// Creates a new native physics context with the given simulation parameters.
        /// </summary>
        /// <param name="gravity">Gravity vector in Unity space.</param>
        /// <param name="solverIterations">Number of constraint solver iterations per step.</param>
        /// <param name="maxSubSteps">Maximum number of fixed sub-steps per simulation step.</param>
        /// <param name="fixedTimeStep">Fixed time step used for sub-stepping.</param>
        public MMDBulletPhysics(float3 gravity, int solverIterations, int maxSubSteps, float fixedTimeStep)
        {
            m_NativeContext = CreateContext(gravity, solverIterations, maxSubSteps, fixedTimeStep);
        }

        private unsafe static IntPtr CreateContext(float3 gravity, int solverIterations, int maxSubSteps, float fixedTimeStep)
        {
            return Native.MMDBulletPhysicsCreate((float*)&gravity, solverIterations, maxSubSteps, fixedTimeStep);
        }
        /// <summary>
        /// Destroys the native physics context and invalidates this wrapper.
        /// </summary>
        public void Dispose()
        {
            if (m_NativeContext == IntPtr.Zero)
            {
                return;
            }

            Native.MMDBulletPhysicsDestroy(m_NativeContext);
            m_NativeContext = IntPtr.Zero;
        }

        /// <summary>
        /// Resets the simulation to its initial state using the given random seed.
        /// </summary>
        /// <param name="seed">Random seed used to reset the deterministic simulation.</param>
        public void Reset(uint seed)
        {
            Native.MMDBulletPhysicsReset(m_NativeContext, seed);
        }

        /// <summary>
        /// Builds native rigid bodies from the supplied simulation data.
        /// </summary>
        /// <param name="rigidBodies">Rigid-body simulation data to register with the native context.</param>
        public unsafe void BuildRigidBodies(NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodies)
        {
            Native.MMDBulletPhysicsBuildRigidBodies(m_NativeContext, (MMDRigidBody.RigidBodySimulationData*)rigidBodies.GetUnsafeReadOnlyPtr(), rigidBodies.Length);
        }

        /// <summary>
        /// Builds native joints linking previously built rigid bodies.
        /// </summary>
        /// <param name="joints">Joint data to register with the native context.</param>
        public void BuildJoints(NativeJointData[] joints)
        {
            Native.MMDBulletPhysicsBuildJoints(m_NativeContext, joints, joints.Length);
        }

        /// <summary>
        /// Builds an optional static ground collider in the native context.
        /// </summary>
        /// <param name="enabled">Whether the ground collider is active.</param>
        /// <param name="groupIndex">Collision group of the ground collider.</param>
        /// <param name="collisionMask">Collision mask of the ground collider.</param>
        public void BuildGround(bool enabled, byte groupIndex, short collisionMask)
        {
            Native.MMDBulletPhysicsBuildGround(m_NativeContext, enabled, groupIndex, collisionMask);
        }

        /// <summary>
        /// Toggles ground collision on the existing native ground collider.
        /// </summary>
        /// <param name="enabled">Whether ground collision is active.</param>
        public void SetGroundCollisionEnabled(bool enabled)
        {
            Native.MMDBulletPhysicsSetGroundCollisionEnabled(m_NativeContext, enabled );
        }

        /// <summary>
        /// Pushes world transforms onto the given rigid bodies, optionally clearing their velocity.
        /// </summary>
        /// <param name="count">Number of rigid bodies to update.</param>
        /// <param name="worldTransforms">World transforms to apply, one per rigid body.</param>
        /// <param name="rigidBodyIndices">Indices of the rigid bodies being updated.</param>
        /// <param name="clearVelocity">Whether to zero the linear and angular velocity.</param>
        public unsafe void SetRigidBodyTransforms(int count, in NativeArray<float4x4> worldTransforms, in NativeArray<int> rigidBodyIndices, bool clearVelocity)
        {
            Native.MMDBulletPhysicsSetRigidBodyTransforms(m_NativeContext, (nuint)count, (float*)worldTransforms.GetUnsafeReadOnlyPtr(), (int*)rigidBodyIndices.GetUnsafeReadOnlyPtr(), clearVelocity);
        }

        /// <summary>
        /// Reads back the current motion-state world transforms of the given rigid bodies.
        /// </summary>
        /// <param name="count">Number of rigid bodies to read.</param>
        /// <param name="rigidBodyIndices">Indices of the rigid bodies to read.</param>
        /// <param name="transforms">Output buffer receiving the world transforms.</param>
        public unsafe void GetRigidBodyMotionTransforms(int count, in NativeArray<int> rigidBodyIndices, ref NativeArray<float4x4> transforms)
        {
            Native.MMDBulletPhysicsGetRigidBodyMotionTransforms(m_NativeContext, (nuint)count, (int*)rigidBodyIndices.GetUnsafeReadOnlyPtr(), (float*)transforms.GetUnsafePtr());
        }

        /// <summary>
        /// Shifts a rigid body's position by a world-space delta without resetting velocity.
        /// </summary>
        /// <param name="rigidBodyIndex">Index of the rigid body to shift.</param>
        /// <param name="delta">World-space translation delta.</param>
        public unsafe void ShiftRigidBodyPosition(int rigidBodyIndex, float3 delta)
        {
            Native.MMDBulletPhysicsShiftRigidBodyPosition(m_NativeContext, rigidBodyIndex, (float*)&delta);
        }

        /// <summary>
        /// Advances the simulation by the given elapsed time using fixed sub-steps.
        /// </summary>
        /// <param name="elapsedTime">Elapsed time to simulate, in seconds.</param>
        public void StepSimulation(float elapsedTime)
        {
            Native.MMDBulletPhysicsStepSimulation(m_NativeContext, elapsedTime);
        }

        /// <summary>
        /// Blittable joint data marshalled to the native Bullet plugin.
        /// Field order is layout-critical and must stay in sync with the native struct; do not reorder.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct NativeJointData
        {
            /// <summary>PMX joint type.</summary>
            public PMXJoint.Type type;
            /// <summary>Index of the first connected rigid body.</summary>
            public int rigidBodyAIndex;
            /// <summary>Index of the second connected rigid body.</summary>
            public int rigidBodyBIndex;
            /// <summary>Joint frame relative to the first rigid body.</summary>
            public float4x4 frameInA;
            /// <summary>Joint frame relative to the second rigid body.</summary>
            public float4x4 frameInB;
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
        }

        private static class Native
        {
            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static unsafe extern IntPtr MMDBulletPhysicsCreate(
                [In] float* gravity,
                int solverIterations,
                int maxSubSteps,
                float fixedTimeStep);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsDestroy(IntPtr context);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsReset(IntPtr context, uint seed);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public unsafe static extern void MMDBulletPhysicsBuildRigidBodies(
                IntPtr context,
                [In] MMDRigidBody.RigidBodySimulationData* rigidBodies,
                int rigidBodyCount);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsBuildJoints(
                IntPtr context,
                [In] NativeJointData[] joints,
                int jointCount);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsBuildGround(
                IntPtr context,
                bool enabled,
                byte groupIndex,
                short collisionMask);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsSetGroundCollisionEnabled(IntPtr context, bool enabled);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public unsafe static extern void MMDBulletPhysicsSetRigidBodyTransforms(
                IntPtr context,
                nuint rigidBodyCount,
                [In] float* transforms,
                [In] int* rigidBodyIndices,
                bool clearVelocity);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public unsafe static extern void MMDBulletPhysicsGetRigidBodyMotionTransforms(
                IntPtr context,
                nuint rigidBodyCount,
                [In] int* rigidBodyIndices,
                [Out] float* transforms);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public unsafe static extern void MMDBulletPhysicsShiftRigidBodyPosition(
                IntPtr context,
                int rigidBodyIndex,
                [In] float* delta);

            [DllImport(k_DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void MMDBulletPhysicsStepSimulation(IntPtr context, float elapsedTime);
        }
    }
}
