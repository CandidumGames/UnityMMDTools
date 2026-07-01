using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {
        // The 6 non-baked bone channels, in the order stored in VMDClipData.curves[6 * boneIndex + channel]: localPosition x/y/z, localEulerAnglesRaw x/y/z.
        private const int k_NonBakedBoneChannelCount = 6;

        private static void AddBoneCurves(VMDClipData bones, VMDAnimation animation, PMXModel model, ref MMDTransformManager.SolverContext transformContext, string[] bonePaths, ref IndexResolver resolver, float frameRate, ProgressCallback progress)
        {
            uint lastFrame = GetLastBoneFrame(animation);
            int frameCount = checked((int)lastFrame + 1);
            ReportProgress(progress, Stage.BoneConversion, 0, frameCount);

            ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData = ref transformContext.boneSolverData;
            int boneCount = boneSolverData.Length;

            NativeList<ResolvedBoneFrame> resolvedBoneFrames = BuildSortedResolvedBoneFrames(animation, ref resolver, in boneSolverData, boneCount, frameCount, Allocator.Persistent);
            NativeArray<bool> sourceBoneSelection = new NativeArray<bool>(boneCount, Allocator.Persistent);
            NativeArray<int> sourceTrackIndexByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            NativeList<int> sourceBoneIndices = new NativeList<int>(Allocator.Persistent);
            AnimationMath.BuildSourceBoneTracks(in resolvedBoneFrames, ref sourceBoneSelection, ref sourceTrackIndexByBone, ref sourceBoneIndices);

            NativeArray<BoneSample> boneSamples = new NativeArray<BoneSample>(checked(sourceBoneIndices.Length * frameCount), Allocator.Persistent);
            AnimationMath.FillCompactBoneSamples(in resolvedBoneFrames, in sourceTrackIndexByBone, ref boneSamples, frameCount);
            AnimationMath.SeedAndFixSparseBoneSamples(in boneSolverData, in sourceBoneIndices, ref boneSamples, frameCount);

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
                    AnimationMath.BuildSparseBoneKeyframes(in sparseSamples, sampleCount, frameRate, ref positionX, ref positionY, ref positionZ, ref eulerX, ref eulerY, ref eulerZ);
                }

                int channelStart = checked(boneIndex * k_NonBakedBoneChannelCount);
                bones.paths[boneIndex] = path;
                if (writePosition)
                {
                    bones.curves[channelStart + 0] = BuildSparseCurve(positionX, sampleCount, true);
                    bones.curves[channelStart + 1] = BuildSparseCurve(positionY, sampleCount, true);
                    bones.curves[channelStart + 2] = BuildSparseCurve(positionZ, sampleCount, true);
                }

                if (writeRotation)
                {
                    bones.curves[channelStart + 3] = BuildSparseCurve(eulerX, sampleCount, true);
                    bones.curves[channelStart + 4] = BuildSparseCurve(eulerY, sampleCount, true);
                    bones.curves[channelStart + 5] = BuildSparseCurve(eulerZ, sampleCount, true);
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

        private static AnimationCurve BuildSparseCurve(NativeArray<Keyframe> keyframes, int count, bool preserveTangents)
        {
            Keyframe[] managedKeyframes = keyframes.GetSubArray(0, count).ToArray();
            if (!preserveTangents)
            {
                ApplyLinearTangents(managedKeyframes);
            }

            return new AnimationCurve(managedKeyframes);
        }
    }
}
