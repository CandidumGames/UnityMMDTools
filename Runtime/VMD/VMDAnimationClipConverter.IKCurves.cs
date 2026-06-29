using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {

        private static void AddIKCurves(
            VMDClipData ikToggles,
            VMDAnimation animation,
            ref MMDTransformManager.SolverContext transformContext,
            string[] bonePaths,
            ref IndexResolver resolver,
            float frameRate)
        {
            uint lastFrame = GetLastIKFrame(animation);
            int frameCount = checked((int)lastFrame + 1);
            ref NativeArray<int> ikControllerByBoneIndices =
                ref transformContext.ikControllerByBoneIndices;
            int boneCount = transformContext.boneSolverData.Length;

            NativeList<ResolvedIKToggleFrame> resolvedIKToggleFrames = BuildSortedResolvedIKToggleFrames(animation, ref resolver, in ikControllerByBoneIndices, boneCount, frameCount, Allocator.Persistent);
            NativeList<int> ikBoneIndices = new NativeList<int>(Allocator.Persistent);
            NativeArray<int> ikTrackIndexByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            AnimationMath.BuildIKToggleTracks(
                in resolvedIKToggleFrames,
                ref ikTrackIndexByBone,
                ref ikBoneIndices);

            NativeArray<IKToggleFrameSample> ikSamplesByTrack = new NativeArray<IKToggleFrameSample>(checked(ikBoneIndices.Length * frameCount), Allocator.Persistent);
            AnimationMath.FillCompactIKToggleSamples(
                in resolvedIKToggleFrames,
                in ikTrackIndexByBone,
                ref ikSamplesByTrack,
                frameCount);

            List<string> togglePaths = new List<string>();
            List<AnimationCurve> toggleCurves = new List<AnimationCurve>();
            for (int trackIndex = 0; trackIndex < ikBoneIndices.Length; ++trackIndex)
            {
                int boneIndex = ikBoneIndices[trackIndex];
                string path = bonePaths[boneIndex];

                Keyframe[] keyframes = new Keyframe[frameCount];
                int sampleStart = trackIndex * frameCount;
                for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                {
                    bool enabled = ikSamplesByTrack[sampleStart + frameIndex].enabled;
                    keyframes[frameIndex] = SteppedKeyframe(frameIndex / frameRate, enabled ? 1.0f : 0.0f);
                }

                togglePaths.Add(path);
                toggleCurves.Add(new AnimationCurve(keyframes));
            }

            ikToggles.paths = togglePaths.ToArray();
            ikToggles.curves = toggleCurves.ToArray();

            ikSamplesByTrack.Dispose();
            ikBoneIndices.Dispose();
            ikTrackIndexByBone.Dispose();
            resolvedIKToggleFrames.Dispose();
        }

        private static int GetIKToggleFrameCount(VMDAnimation animation)
        {
            int count = 0;
            for (int showIKFrameIndex = 0; showIKFrameIndex < animation.showIKFrames.Length; ++showIKFrameIndex)
            {
                count += animation.showIKFrames[showIKFrameIndex].ikToggles.Length;
            }
            return count;
        }

        private readonly struct IKToggleFrameSample
        {
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool hasKey;
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool enabled;

            public IKToggleFrameSample(bool enabled)
            {
                hasKey = true;
                this.enabled = enabled;
            }
        }

    }
}
