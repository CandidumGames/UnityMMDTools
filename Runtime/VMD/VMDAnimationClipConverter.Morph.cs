using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {

        private static void AddMorphCurves(
            AnimationClip clip,
            VMDAnimation animation,
            PMXModel model,
            string[][] morphRendererPaths,
            ref IndexResolver resolver,
            float frameRate,
            ProgressCallback progress)
        {
            uint lastFrame = GetLastMorphFrame(animation);
            int frameCount = checked((int)lastFrame + 1);
            int morphCount = model.morphs.Length;
            ReportProgress(progress, Stage.MorphConversion, 0, frameCount);
            NativeArray<VMDMorphFrame> nativeMorphFrames = new NativeArray<VMDMorphFrame>(animation.morphFrames, Allocator.Persistent);
            NativeArray<bool> vertexMorphSelection = new NativeArray<bool>(morphCount, Allocator.Persistent);
            NativeArray<bool> morphSelection = new NativeArray<bool>(morphCount, Allocator.Persistent);
            NativeArray<MorphSample> samplesByMorph = new NativeArray<MorphSample>(checked(morphCount * frameCount), Allocator.Persistent);
            NativeList<int> curveMorphIndices = new NativeList<int>(Allocator.Persistent);
            NativeArray<float2> keyframesByMorph = new NativeArray<float2>(checked(morphCount * frameCount), Allocator.Persistent);
            NativeArray<int> keyframeCountsByMorph = new NativeArray<int>(morphCount, Allocator.Persistent);
            NativeList<NativeGroupMorphOffset> groupMorphOffsets = new NativeList<NativeGroupMorphOffset>(Allocator.Persistent);

            for (int morphIndex = 0; morphIndex < morphCount; ++morphIndex)
            {
                vertexMorphSelection[morphIndex] = model.morphs[morphIndex].type == PMXMorph.Type.Vertex;
            }
            BuildGroupMorphOffsets(model, ref groupMorphOffsets);

            AnimationMath.PrepareMorphData(
                in nativeMorphFrames,
                ref resolver,
                in vertexMorphSelection,
                in groupMorphOffsets,
                ref morphSelection,
                ref samplesByMorph,
                ref curveMorphIndices,
                ref keyframesByMorph,
                ref keyframeCountsByMorph,
                morphCount,
                frameCount,
                frameRate);

            for (int trackIndex = 0; trackIndex < curveMorphIndices.Length; ++trackIndex)
            {
                int morphIndex = curveMorphIndices[trackIndex];
                string morphName = model.morphs[morphIndex].renamedName.ToString();
                int keyframeCount = keyframeCountsByMorph[morphIndex];
                Keyframe[] keyframes = new Keyframe[keyframeCount];
                int keyframeStartIndex = morphIndex * frameCount;
                for (int keyframeIndex = 0; keyframeIndex < keyframeCount; ++keyframeIndex)
                {
                    float2 nativeKeyframe = keyframesByMorph[keyframeStartIndex + keyframeIndex];
                    keyframes[keyframeIndex] = new Keyframe(nativeKeyframe.x, nativeKeyframe.y);
                }

                ApplyLinearTangents(keyframes);

                AnimationCurve curve = new AnimationCurve(keyframes);
                string[] rendererPaths = morphRendererPaths[morphIndex];
                for (int rendererIndex = 0; rendererIndex < rendererPaths.Length; ++rendererIndex)
                {
                    clip.SetCurve(
                        rendererPaths[rendererIndex],
                        typeof(SkinnedMeshRenderer),
                        $"blendShape.{morphName}",
                        curve);
                }
            }

            ReportProgress(progress, Stage.MorphConversion, frameCount, frameCount);
            groupMorphOffsets.Dispose();
            keyframeCountsByMorph.Dispose();
            keyframesByMorph.Dispose();
            curveMorphIndices.Dispose();
            samplesByMorph.Dispose();
            morphSelection.Dispose();
            vertexMorphSelection.Dispose();
            nativeMorphFrames.Dispose();
        }

        private static void BuildGroupMorphOffsets(PMXModel model, ref NativeList<NativeGroupMorphOffset> result)
        {
            for (int sourceMorphIndex = 0; sourceMorphIndex < model.morphs.Length; ++sourceMorphIndex)
            {
                PMXMorph sourceMorph = model.morphs[sourceMorphIndex];
                if (sourceMorph.type != PMXMorph.Type.Group)
                {
                    continue;
                }

                for (int offsetIndex = 0; offsetIndex < sourceMorph.offsets.Length; ++offsetIndex)
                {
                    if (sourceMorph.offsets[offsetIndex] is not PMXGroupMorphData groupOffset)
                    {
                        continue;
                    }

                    int targetMorphIndex = groupOffset.morphIndex;
                    if (targetMorphIndex < 0 ||
                        targetMorphIndex >= model.morphs.Length ||
                        model.morphs[targetMorphIndex].type != PMXMorph.Type.Vertex)
                    {
                        continue;
                    }

                    result.Add(new NativeGroupMorphOffset(sourceMorphIndex, targetMorphIndex, groupOffset.rate));
                }
            }
        }

        private readonly struct MorphSample
        {
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool hasKey;
            public readonly float weight;

            public MorphSample(float weight, bool hasKey)
            {
                this.hasKey = hasKey;
                this.weight = weight;
            }
        }

        private readonly struct NativeGroupMorphOffset
        {
            public readonly int sourceMorphIndex;
            public readonly int targetMorphIndex;
            public readonly float rate;

            public NativeGroupMorphOffset(int sourceMorphIndex, int targetMorphIndex, float rate)
            {
                this.sourceMorphIndex = sourceMorphIndex;
                this.targetMorphIndex = targetMorphIndex;
                this.rate = rate;
            }
        }

    }
}
