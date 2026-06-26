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

        private static void AddBakedBoneCurves(
            AnimationClip clip,
            VMDAnimation animation,
            ref MMDTransformManager.SolverContext transformContext,
            ref MMDPhysicsManager.PhysicsSolverContext physicsContext,
            string[] bonePaths,
            bool bakePhysics,
            ref IndexResolver resolver,
            VMDAnimationClipOptions options,
            ProgressCallback progress)
        {
            int setupFrameCount = bakePhysics && options.physicsWarmUpDuration > 0.0f
                ? Mathf.Max(0, Mathf.RoundToInt(options.physicsWarmUpDuration * options.frameRate))
                : 0;

            uint lastFrame = GetLastBakeFrame(animation);
            int frameCount = checked((int)lastFrame + 1);
            ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData =
                ref transformContext.boneSolverData;

            ref NativeArray<int> ikControllerByBoneIndices =
                ref transformContext.ikControllerByBoneIndices;
            ref NativeArray<MMDTransformManager.IKControllerData> ikControllers =
                ref transformContext.ikControllers;
            ref NativeArray<MMDTransformManager.IKLinkData> ikLinks =
                ref transformContext.ikLinks;
            int boneCount = boneSolverData.Length;
            ReportProgress(progress, Stage.BoneConversion, 0, frameCount);

            NativeArray<bool> sourceBoneSelection = new NativeArray<bool>(boneCount, Allocator.Persistent);
            NativeArray<bool> curveBoneSelection = new NativeArray<bool>(boneCount, Allocator.Persistent);
            NativeArray<bool> physicsControlledBoneSelection = new NativeArray<bool>(boneCount, Allocator.Persistent);
            NativeArray<int> sourceTrackIndexByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            NativeArray<int> lastFrameWithSampleByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            NativeList<int> sourceBoneIndices = new NativeList<int>(Allocator.Persistent);
            NativeList<int> curveBoneIndices = new NativeList<int>(Allocator.Persistent);
            NativeList<ResolvedBoneFrame> resolvedBoneFrames = BuildSortedResolvedBoneFrames(animation, ref resolver, in boneSolverData, boneCount, frameCount, Allocator.Persistent);
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
            AnimationMath.InterpolateCompactBoneSamples(
                in boneSolverData,
                in sourceBoneIndices,
                ref boneSamples,
                frameCount,
                ref lastFrameWithSampleByBone);

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

            MMDPhysicsManager.BuildPhysicsControlledBoneSelection(
                in physicsContext,
                ref physicsControlledBoneSelection);

            AnimationMath.ResolveBakedCurveBones(
                in boneSolverData,
                in sourceBoneSelection,
                in ikControllerByBoneIndices,
                in ikControllers,
                in ikLinks,
                in physicsControlledBoneSelection,
                ref curveBoneSelection,
                ref lastFrameWithSampleByBone,
                ref curveBoneIndices,
                boneCount,
                checked((int)lastFrame),
                bakePhysics);

            int simLastFrame = checked((int)lastFrame);
            if (setupFrameCount > 0)
            {
                int sourceFrameCount = frameCount;
                int simFrameCount = setupFrameCount + frameCount;

                NativeArray<BoneSample> combinedBoneSamples = new NativeArray<BoneSample>(checked(sourceBoneIndices.Length * simFrameCount), Allocator.Persistent);
                AnimationMath.PrependSetupBoneSamples(
                    in boneSolverData,
                    in sourceBoneIndices,
                    in boneSamples,
                    ref combinedBoneSamples,
                    sourceFrameCount,
                    setupFrameCount,
                    simFrameCount);
                boneSamples.Dispose();
                boneSamples = combinedBoneSamples;

                NativeArray<IKToggleFrameSample> combinedIKSamples = new NativeArray<IKToggleFrameSample>(checked(ikBoneIndices.Length * simFrameCount), Allocator.Persistent);
                AnimationMath.PrependSetupIKSamples(
                    in ikSamplesByTrack,
                    ref combinedIKSamples,
                    ikBoneIndices.Length,
                    sourceFrameCount,
                    setupFrameCount,
                    simFrameCount);
                ikSamplesByTrack.Dispose();
                ikSamplesByTrack = combinedIKSamples;

                for (int i = 0; i < lastFrameWithSampleByBone.Length; ++i)
                {
                    lastFrameWithSampleByBone[i] += setupFrameCount;
                }

                frameCount = simFrameCount;
                simLastFrame = setupFrameCount + checked((int)lastFrame);
            }

            NativeArray<int> keyframeStartByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            NativeArray<int> keyframeCountByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
            int totalCurveKeyframeCount = 0;
            for (int i = 0; i < curveBoneIndices.Length; ++i)
            {
                int boneIndex = curveBoneIndices[i];
                keyframeStartByBone[boneIndex] = totalCurveKeyframeCount;
                keyframeCountByBone[boneIndex] = lastFrameWithSampleByBone[boneIndex] + 1;
                totalCurveKeyframeCount = checked(totalCurveKeyframeCount + keyframeCountByBone[boneIndex]);
            }
            BakedBoneCurveBuffers curveBuffers = new BakedBoneCurveBuffers(keyframeStartByBone, keyframeCountByBone, totalCurveKeyframeCount, Allocator.Persistent);

            int lastReportedPercent = 0;
            for (int frame = 0; frame <= simLastFrame; ++frame)
            {
                AnimationMath.TransformBonesForBakeFrame(
                    ref transformContext,
                    ref physicsContext,
                    ref boneSolverData,
                    ref ikControllers,
                    in sourceTrackIndexByBone,
                    in physicsControlledBoneSelection,
                    in boneSamples,
                    in ikControllerByBoneIndices,
                    in ikTrackIndexByBone,
                    in ikSamplesByTrack,
                    frameCount,
                    frame,
                    options.frameRate,
                    bakePhysics);
                AnimationMath.WriteBakedBoneCurvesForFrame(
                    in boneSolverData,
                    in curveBoneIndices,
                    in lastFrameWithSampleByBone,
                    ref curveBuffers,
                    frame,
                    options.frameRate);

                int completedFrames = frame + 1;
                int progressPercent = completedFrames * 100 / frameCount;
                if (progressPercent > lastReportedPercent)
                {
                    ReportProgress(progress, Stage.BoneConversion, completedFrames, frameCount);
                    lastReportedPercent = progressPercent;
                }
            }

            for (int curveBoneIndexIndex = 0; curveBoneIndexIndex < curveBoneIndices.Length; ++curveBoneIndexIndex)
            {
                int boneIndex = curveBoneIndices[curveBoneIndexIndex];
                string path = bonePaths[boneIndex];
                bool physicsControlled = bakePhysics && physicsControlledBoneSelection[boneIndex];
                MMDBoneTransform.BoneSolverData runtimeData = boneSolverData[boneIndex];
                bool writePosition = CanWriteBakedPositionCurves(
                    in runtimeData,
                    sourceBoneSelection,
                    boneIndex,
                    physicsControlled);
                bool writeRotation = physicsControlled || CanWriteRotationCurves(in runtimeData);
                SetBakedBoneCurves(
                    clip,
                    path,
                    curveBuffers,
                    boneIndex,
                    writePosition,
                    writeRotation,
                    setupFrameCount,
                    options.frameRate);
            }
            curveBuffers.Dispose();
            ikSamplesByTrack.Dispose();
            ikBoneIndices.Dispose();
            resolvedIKToggleFrames.Dispose();
            curveBoneIndices.Dispose();
            sourceBoneIndices.Dispose();
            lastFrameWithSampleByBone.Dispose();
            boneSamples.Dispose();
            resolvedBoneFrames.Dispose();
            ikTrackIndexByBone.Dispose();
            sourceTrackIndexByBone.Dispose();
            physicsControlledBoneSelection.Dispose();
            curveBoneSelection.Dispose();
            sourceBoneSelection.Dispose();
        }

        private static void SetBakedBoneCurves(
            AnimationClip clip,
            string path,
            BakedBoneCurveBuffers curveBuffers,
            int boneIndex,
            bool writePosition,
            bool writeRotation,
            int setupFrameCount,
            float frameRate,
            bool preserveTangents = false)
        {
            int startIndex = curveBuffers.keyframeStartByBone[boneIndex];
            int keyframeCount = curveBuffers.keyframeCountByBone[boneIndex];
            int emitStart = startIndex + setupFrameCount;
            int emitCount = keyframeCount - setupFrameCount;
            float timeOffset = setupFrameCount / frameRate;
            SetNativeCurveIfPresent(clip, path, "localPosition.x", curveBuffers.positionX.GetSubArray(emitStart, emitCount), writePosition, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localPosition.y", curveBuffers.positionY.GetSubArray(emitStart, emitCount), writePosition, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localPosition.z", curveBuffers.positionZ.GetSubArray(emitStart, emitCount), writePosition, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localRotation.x", curveBuffers.rotationX.GetSubArray(emitStart, emitCount), writeRotation, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localRotation.y", curveBuffers.rotationY.GetSubArray(emitStart, emitCount), writeRotation, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localRotation.z", curveBuffers.rotationZ.GetSubArray(emitStart, emitCount), writeRotation, timeOffset, preserveTangents);
            SetNativeCurveIfPresent(clip, path, "localRotation.w", curveBuffers.rotationW.GetSubArray(emitStart, emitCount), writeRotation, timeOffset, preserveTangents);
        }

        private static void SetNativeCurveIfPresent(AnimationClip clip, string path, string propertyName, NativeArray<Keyframe> keyframes, bool shouldSet, float timeOffset, bool preserveTangents)
        {
            if (!shouldSet)
            {
                return;
            }

            Keyframe[] managedKeyframes = keyframes.ToArray();
            if (timeOffset != 0.0f)
            {
                for (int i = 0; i < managedKeyframes.Length; ++i)
                {
                    managedKeyframes[i].time -= timeOffset;
                }
            }
            if (!preserveTangents)
            {
                ApplyLinearTangents(managedKeyframes);
            }
            clip.SetCurve(path, typeof(Transform), propertyName, new AnimationCurve(managedKeyframes));
        }

        private static bool CanWriteBakedPositionCurves(in MMDBoneTransform.BoneSolverData runtimeData, NativeArray<bool> sourceBoneSelection, int boneIndex, bool physicsControlled)
        {
            if (physicsControlled)
            {
                return true;
            }

            if (!runtimeData.translatable)
            {
                return false;
            }

            return (boneIndex < sourceBoneSelection.Length && sourceBoneSelection[boneIndex]) ||
                runtimeData.translationConstraint;
        }

        private static bool CanWriteRotationCurves(in MMDBoneTransform.BoneSolverData runtimeData)
        {
            return runtimeData.rotatable;
        }

        private struct BakedBoneCurveBuffers : IDisposable
        {
            public NativeArray<int> keyframeStartByBone;
            public NativeArray<int> keyframeCountByBone;
            public NativeArray<Keyframe> positionX;
            public NativeArray<Keyframe> positionY;
            public NativeArray<Keyframe> positionZ;
            public NativeArray<Keyframe> rotationX;
            public NativeArray<Keyframe> rotationY;
            public NativeArray<Keyframe> rotationZ;
            public NativeArray<Keyframe> rotationW;

            public BakedBoneCurveBuffers(
                NativeArray<int> keyframeStartByBone,
                NativeArray<int> keyframeCountByBone,
                int totalKeyframeCount,
                Allocator allocator)
            {
                this.keyframeStartByBone = keyframeStartByBone;
                this.keyframeCountByBone = keyframeCountByBone;
                positionX = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                positionY = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                positionZ = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                rotationX = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                rotationY = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                rotationZ = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
                rotationW = new NativeArray<Keyframe>(totalKeyframeCount, allocator);
            }

            public void Dispose()
            {
                if (keyframeStartByBone.IsCreated)
                {
                    keyframeStartByBone.Dispose();
                }
                if (keyframeCountByBone.IsCreated)
                {
                    keyframeCountByBone.Dispose();
                }
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
            }
        }

    }
}
