using UnityEditor;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Editor-only adapter that reconstructs a Unity <see cref="AnimationClip"/> from the raw curve data produced by
    /// <see cref="VMDAnimationClipConverter"/>. The converter no longer builds clips directly because
    /// <see cref="AnimationClip.SetCurve"/> is editor-only for non-legacy clips; this is the single place that calls
    /// <c>SetCurve</c> / <see cref="AnimationUtility.SetEditorCurve"/> / <see cref="AnimationClip.EnsureQuaternionContinuity"/>,
    /// so the editor importers keep producing identical <c>.anim</c> sub-assets while runtime playback consumes the
    /// curve data directly.
    /// </summary>
    public static class VMDClipDataBuilder
    {
        // Baked bone channels: VMDClipData.curves[7 * boneIndex + c].
        private static readonly string[] k_BakedBoneProperties =
        {
            "localPosition.x", "localPosition.y", "localPosition.z",
            "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w",
        };

        // Non-baked bone channels: VMDClipData.curves[6 * boneIndex + c]. Euler channels use SetEditorCurve.
        private static readonly string[] k_NonBakedBoneProperties =
        {
            "localPosition.x", "localPosition.y", "localPosition.z",
            "localEulerAnglesRaw.x", "localEulerAnglesRaw.y", "localEulerAnglesRaw.z",
        };

        /// <summary>
        /// Builds a non-legacy <see cref="AnimationClip"/> from model curve data (bones, morphs, and IK toggles for the
        /// non-baked path), ensuring quaternion continuity for baked rotation channels.
        /// </summary>
        /// <param name="data">The model curve data to reconstruct.</param>
        /// <param name="frameRate">Frame rate to stamp on the clip.</param>
        public static AnimationClip BuildAnimationClip(VMDModelClipData data, float frameRate)
        {
            AnimationClip clip = new AnimationClip
            {
                frameRate = frameRate,
                legacy = false,
            };

            if (data.baked)
            {
                ApplyBoneCurves(clip, data.bones, k_BakedBoneProperties, useEditorCurveForEuler: false);
            }
            else
            {
                ApplyBoneCurves(clip, data.bones, k_NonBakedBoneProperties, useEditorCurveForEuler: true);
                ApplyIKToggleCurves(clip, data.ikToggles);
            }

            ApplyMorphCurves(clip, data.morphs);

            clip.EnsureQuaternionContinuity();
            return clip;
        }

        /// <summary>
        /// Builds a non-legacy camera-rig <see cref="AnimationClip"/> from camera curve data, ensuring quaternion
        /// continuity for the look-at target rotation.
        /// </summary>
        /// <param name="data">The camera curve data to reconstruct.</param>
        /// <param name="frameRate">Frame rate to stamp on the clip.</param>
        public static AnimationClip BuildCameraAnimationClip(VMDCameraClipData data, float frameRate)
        {
            AnimationClip clip = new AnimationClip
            {
                frameRate = frameRate,
                legacy = false,
            };

            string targetPath = VMDAnimationClipConverter.k_DefaultCameraTargetName;
            string cameraPath = VMDAnimationClipConverter.k_DefaultCameraTargetName + "/" + VMDAnimationClipConverter.k_DefaultCameraChildName;

            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localPosition.x", data.targetPositionX);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localPosition.y", data.targetPositionY);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localPosition.z", data.targetPositionZ);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localRotation.x", data.targetRotationX);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localRotation.y", data.targetRotationY);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localRotation.z", data.targetRotationZ);
            SetCurveIfPresent(clip, targetPath, typeof(Transform), "localRotation.w", data.targetRotationW);
            SetCurveIfPresent(clip, cameraPath, typeof(Transform), "localPosition.z", data.cameraLocalPositionZ);
            SetCurveIfPresent(clip, cameraPath, typeof(Camera), VMDAnimationClipConverter.k_CameraFieldOfViewProperty, data.fieldOfView);

            clip.EnsureQuaternionContinuity();
            return clip;
        }

        private static void ApplyBoneCurves(AnimationClip clip, VMDClipData bones, string[] properties, bool useEditorCurveForEuler)
        {
            int channels = properties.Length;
            for (int boneIndex = 0; boneIndex < bones.paths.Length; ++boneIndex)
            {
                string path = bones.paths[boneIndex];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                int channelStart = boneIndex * channels;
                for (int c = 0; c < channels; ++c)
                {
                    AnimationCurve curve = bones.curves[channelStart + c];
                    if (curve == null)
                    {
                        continue;
                    }

                    // localEulerAnglesRaw is an editor-only binding that must be set through AnimationUtility.
                    if (useEditorCurveForEuler && properties[c].StartsWith("localEulerAnglesRaw"))
                    {
                        EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), properties[c]);
                        AnimationUtility.SetEditorCurve(clip, binding, curve);
                    }
                    else
                    {
                        clip.SetCurve(path, typeof(Transform), properties[c], curve);
                    }
                }
            }
        }

        private static void ApplyIKToggleCurves(AnimationClip clip, VMDClipData ikToggles)
        {
            if (ikToggles == null)
            {
                return;
            }

            for (int i = 0; i < ikToggles.paths.Length; ++i)
            {
                AnimationCurve curve = ikToggles.curves[i];
                if (curve == null)
                {
                    continue;
                }
                clip.SetCurve(ikToggles.paths[i], typeof(MMDBoneTransform), VMDAnimationClipConverter.k_IKEnabledProperty, curve);
            }
        }

        private static void ApplyMorphCurves(AnimationClip clip, VMDMorphClipData morphs)
        {
            if (morphs == null)
            {
                return;
            }

            for (int i = 0; i < morphs.paths.Length; ++i)
            {
                AnimationCurve curve = morphs.curves[i];
                if (curve == null)
                {
                    continue;
                }
                clip.SetCurve(morphs.paths[i], typeof(SkinnedMeshRenderer), $"blendShape.{morphs.names[i]}", curve);
            }
        }

        private static void SetCurveIfPresent(AnimationClip clip, string path, System.Type type, string propertyName, AnimationCurve curve)
        {
            if (curve == null)
            {
                return;
            }
            clip.SetCurve(path, type, propertyName, curve);
        }
    }
}
