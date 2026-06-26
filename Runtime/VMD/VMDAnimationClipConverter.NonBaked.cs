using Unity.Collections;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {
        private static void AddBoneCurves(
            AnimationClip clip,
            VMDAnimation animation,
            PMXModel model,
            ref MMDTransformManager.SolverContext transformContext,
            string[] bonePaths,
            ref IndexResolver resolver,
            float frameRate,
            ProgressCallback progress)
        {
            uint lastFrame = GetLastBoneFrame(animation);
            int frameCount = checked((int)lastFrame + 1);
            ReportProgress(progress, Stage.BoneConversion, 0, frameCount);

            ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData =
                ref transformContext.boneSolverData;
            int boneCount = boneSolverData.Length;

            NativeList<ResolvedBoneFrame> resolvedBoneFrames = BuildSortedResolvedBoneFrames(animation, ref resolver, in boneSolverData, boneCount, frameCount, Allocator.Persistent);
            NativeArray<bool> sourceBoneSelection = new NativeArray<bool>(boneCount, Allocator.Persistent);
            NativeArray<int> sourceTrackIndexByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            NativeList<int> sourceBoneIndices = new NativeList<int>(Allocator.Persistent);
            AnimationMath.BuildSourceBoneTracks(
                in resolvedBoneFrames,
                ref sourceBoneSelection,
                ref sourceTrackIndexByBone,
                ref sourceBoneIndices);

            NativeArray<BoneSample> boneSamples = new NativeArray<BoneSample>(checked(sourceBoneIndices.Length * frameCount), Allocator.Persistent);
            AnimationMath.FillCompactBoneSamples(
                in resolvedBoneFrames,
                in sourceTrackIndexByBone,
                ref boneSamples,
                frameCount);
            AnimationMath.SeedAndFixSparseBoneSamples(
                in boneSolverData,
                in sourceBoneIndices,
                ref boneSamples,
                frameCount);

            NativeArray<Keyframe> positionX = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);
            NativeArray<Keyframe> positionY = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);
            NativeArray<Keyframe> positionZ = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);
            NativeArray<Keyframe> eulerX = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);
            NativeArray<Keyframe> eulerY = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);
            NativeArray<Keyframe> eulerZ = new NativeArray<Keyframe>(frameCount, Allocator.TempJob);

            for (int trackIndex = 0; trackIndex < sourceBoneIndices.Length; ++trackIndex)
            {
                int boneIndex = sourceBoneIndices[trackIndex];
                int sampleStart = trackIndex * frameCount;

                int sampleCount = 0;
                for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                {
                    if (boneSamples[sampleStart + frameIndex].hasKey)
                    {
                        ++sampleCount;
                    }
                }

                if (sampleCount == 0)
                {
                    continue;
                }

                NativeArray<BoneSample> sparseSamples = new NativeArray<BoneSample>(sampleCount, Allocator.Temp);
                int writeIndex = 0;
                for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                {
                    BoneSample sample = boneSamples[sampleStart + frameIndex];
                    if (sample.hasKey)
                    {
                        sparseSamples[writeIndex] = sample;
                        ++writeIndex;
                    }
                }

                string path = bonePaths[boneIndex];
                bool writePosition = CanWritePositionCurves(model, boneIndex);
                bool writeRotation = CanWriteRotationCurves(model, boneIndex);
                if (writePosition || writeRotation)
                {
                    AnimationMath.BuildSparseBoneKeyframes(
                        in sparseSamples,
                        sampleCount,
                        frameRate,
                        ref positionX,
                        ref positionY,
                        ref positionZ,
                        ref eulerX,
                        ref eulerY,
                        ref eulerZ);
                }

                if (writePosition)
                {
                    SetSparseNativeCurve(clip, path, "localPosition.x", positionX, sampleCount, true);
                    SetSparseNativeCurve(clip, path, "localPosition.y", positionY, sampleCount, true);
                    SetSparseNativeCurve(clip, path, "localPosition.z", positionZ, sampleCount, true);
                }

                if (writeRotation)
                {
                    SetSparseNativeEditorCurve(clip, path, "localEulerAnglesRaw.x", eulerX, sampleCount, true);
                    SetSparseNativeEditorCurve(clip, path, "localEulerAnglesRaw.y", eulerY, sampleCount, true);
                    SetSparseNativeEditorCurve(clip, path, "localEulerAnglesRaw.z", eulerZ, sampleCount, true);
                }

                sparseSamples.Dispose();
            }

            positionX.Dispose();
            positionY.Dispose();
            positionZ.Dispose();
            eulerX.Dispose();
            eulerY.Dispose();
            eulerZ.Dispose();
            boneSamples.Dispose();
            sourceBoneIndices.Dispose();
            sourceTrackIndexByBone.Dispose();
            sourceBoneSelection.Dispose();
            resolvedBoneFrames.Dispose();

            ReportProgress(progress, Stage.BoneConversion, frameCount, frameCount);
        }

        private static void SetSparseNativeCurve(AnimationClip clip, string path, string propertyName, NativeArray<Keyframe> keyframes, int count, bool preserveTangents)
        {
            Keyframe[] managedKeyframes = keyframes.GetSubArray(0, count).ToArray();
            if (!preserveTangents)
            {
                ApplyLinearTangents(managedKeyframes);
            }

            clip.SetCurve(path, typeof(Transform), propertyName, new AnimationCurve(managedKeyframes));
        }

        private static void SetSparseNativeEditorCurve(AnimationClip clip, string path, string propertyName, NativeArray<Keyframe> keyframes, int count, bool preserveTangents)
        {
            Keyframe[] managedKeyframes = keyframes.GetSubArray(0, count).ToArray();
            if (!preserveTangents)
            {
                ApplyLinearTangents(managedKeyframes);
            }

            AnimationCurve curve = new AnimationCurve(managedKeyframes);
#if UNITY_EDITOR
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), propertyName);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
#else
            clip.SetCurve(path, typeof(Transform), propertyName, curve);
#endif
        }
    }
}
