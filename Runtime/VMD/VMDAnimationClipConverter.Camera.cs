using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {
        private const string k_CameraFieldOfViewProperty = "field of view";

        /// <summary>Default name of the camera rig child transform that carries the camera movement and orientation.</summary>
        public const string k_DefaultCameraTargetName = "CameraTarget";

        /// <summary>Default name of the camera rig child transform that carries the <see cref="Camera"/>.</summary>
        public const string k_DefaultCameraChildName = "Camera";

        /// <summary>
        /// Converts the camera section of a VMD animation into an <see cref="AnimationClip"/> for a two-node camera
        /// rig. The clip root carries the camera center (look-at target) movement and orientation, and a child
        /// transform named <paramref name="cameraChildName"/> is offset along its local Z axis by the camera distance
        /// and carries the <see cref="Camera"/> field-of-view curve.
        /// </summary>
        /// <param name="animation">The parsed VMD animation whose camera frames are converted.</param>
        /// <param name="frameRate">Frames per second of the generated clip. The native 30 fps VMD timeline is sub-sampled when this is a higher integer multiple (60 or 120), preserving the clip's real-time duration.</param>
        /// <param name="cameraChildName">Name of the rig child transform (relative to the clip root) that carries the camera.</param>
        /// <param name="timingCallback">Optional callback invoked with a stage label and elapsed time for timing measurements.</param>
        /// <param name="progress">Optional progress callback.</param>
        /// <returns>The generated <see cref="AnimationClip"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="animation"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the VMD animation contains no camera frames.</exception>
        public static AnimationClip ConvertCamera(
            VMDAnimation animation,
            float frameRate = 30.0f,
            Action<string, TimeSpan> timingCallback = null,
            ProgressCallback progress = null)
        {
            if (animation == null)
            {
                throw new ArgumentNullException(nameof(animation));
            }
            if (animation.cameraFrames.Length == 0)
            {
                throw new InvalidOperationException("The VMD animation contains no camera frames.");
            }

            ReportProgress(progress, Stage.Setup, 0, 0);
            AnimationClip clip = new AnimationClip
            {
                frameRate = frameRate,
                legacy = false,
            };

            uint lastFrame = GetLastCameraFrame(animation);
            int upsample = (int)(frameRate / MMDConstants.k_VMDNativeFrameRate);
            int frameCount = checked((int)lastFrame * upsample + 1);
            ReportProgress(progress, Stage.CameraConversion, 0, frameCount);

            NativeArray<VMDCameraFrame> sortedFrames = BuildSortedCameraFrames(animation, Allocator.Persistent);
            CameraCurveBuffers buffers = new CameraCurveBuffers(frameCount, Allocator.Persistent);

            using (UMTTiming.Measure(timingCallback, "Camera Conversion"))
            {
                AnimationMath.BakeCameraFrames(
                    in sortedFrames,
                    frameCount,
                    MMDConstants.k_MMDUnitToUnityUnit,
                    frameRate,
                    ref buffers);

                string cameraPath = k_DefaultCameraTargetName + "/" + k_DefaultCameraChildName;
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localPosition.x", buffers.positionX);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localPosition.y", buffers.positionY);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localPosition.z", buffers.positionZ);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localRotation.x", buffers.rotationX);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localRotation.y", buffers.rotationY);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localRotation.z", buffers.rotationZ);
                SetCameraCurve(clip, k_DefaultCameraTargetName, typeof(Transform), "localRotation.w", buffers.rotationW);
                SetCameraCurve(clip, cameraPath, typeof(Transform), "localPosition.z", buffers.cameraDistanceZ);
                SetCameraCurve(clip, cameraPath, typeof(Camera), k_CameraFieldOfViewProperty, buffers.fieldOfView);
            }

            ReportProgress(progress, Stage.CameraConversion, frameCount, frameCount);

            using (UMTTiming.Measure(timingCallback, "Finalization"))
            {
                ReportProgress(progress, Stage.Finalization, 0, 0);
                clip.EnsureQuaternionContinuity();
            }

            buffers.Dispose();
            sortedFrames.Dispose();
            ReportProgress(progress, Stage.Complete, 1, 1);
            return clip;
        }

        private static void SetCameraCurve(AnimationClip clip, string path, Type type, string propertyName, NativeArray<Keyframe> keyframes)
        {
            Keyframe[] managedKeyframes = keyframes.ToArray();
            ApplyLinearTangents(managedKeyframes);
            clip.SetCurve(path, type, propertyName, new AnimationCurve(managedKeyframes));
        }

        private static uint GetLastCameraFrame(VMDAnimation animation)
        {
            uint lastFrame = 0;
            foreach (VMDCameraFrame frame in animation.cameraFrames)
            {
                lastFrame = Math.Max(lastFrame, frame.frame);
            }
            return lastFrame;
        }

        private static NativeArray<VMDCameraFrame> BuildSortedCameraFrames(VMDAnimation animation, Allocator allocator)
        {

            NativeArray<VMDCameraFrame> result = new NativeArray<VMDCameraFrame>(animation.cameraFrames.Length, allocator);

            for (int i = 0; i < animation.cameraFrames.Length; ++i)
            {
                result[i] = animation.cameraFrames[i];
            }

            result.Sort(new CameraFrameComparer());

            return result;
        }

        struct CameraFrameComparer : IComparer<VMDCameraFrame>
        {
            public int Compare(VMDCameraFrame x, VMDCameraFrame y)
            {
                return x.frame.CompareTo(y.frame);
            }
        }


        private struct CameraCurveBuffers : IDisposable
        {
            public NativeArray<Keyframe> positionX;
            public NativeArray<Keyframe> positionY;
            public NativeArray<Keyframe> positionZ;
            public NativeArray<Keyframe> rotationX;
            public NativeArray<Keyframe> rotationY;
            public NativeArray<Keyframe> rotationZ;
            public NativeArray<Keyframe> rotationW;
            public NativeArray<Keyframe> cameraDistanceZ;
            public NativeArray<Keyframe> fieldOfView;

            public CameraCurveBuffers(int frameCount, Allocator allocator)
            {
                positionX = new NativeArray<Keyframe>(frameCount, allocator);
                positionY = new NativeArray<Keyframe>(frameCount, allocator);
                positionZ = new NativeArray<Keyframe>(frameCount, allocator);
                rotationX = new NativeArray<Keyframe>(frameCount, allocator);
                rotationY = new NativeArray<Keyframe>(frameCount, allocator);
                rotationZ = new NativeArray<Keyframe>(frameCount, allocator);
                rotationW = new NativeArray<Keyframe>(frameCount, allocator);
                cameraDistanceZ = new NativeArray<Keyframe>(frameCount, allocator);
                fieldOfView = new NativeArray<Keyframe>(frameCount, allocator);
            }

            public void Dispose()
            {
                if (positionX.IsCreated)
                {
                    positionX.Dispose();
                }
                if (positionY.IsCreated)
                {
                    positionY.Dispose();
                }
                if (positionZ.IsCreated)
                {
                    positionZ.Dispose();
                }
                if (rotationX.IsCreated)
                {
                    rotationX.Dispose();
                }
                if (rotationY.IsCreated)
                {
                    rotationY.Dispose();
                }
                if (rotationZ.IsCreated)
                {
                    rotationZ.Dispose();
                }
                if (rotationW.IsCreated)
                {
                    rotationW.Dispose();
                }
                if (cameraDistanceZ.IsCreated)
                {
                    cameraDistanceZ.Dispose();
                }
                if (fieldOfView.IsCreated)
                {
                    fieldOfView.Dispose();
                }
            }
        }
    }
}
