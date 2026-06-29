using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    public static partial class VMDAnimationClipConverter
    {

        private static NativeList<ResolvedBoneFrame> BuildSortedResolvedBoneFrames(
            VMDAnimation animation,
            ref IndexResolver resolver,
            in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
            int boneCount,
            int frameCount,
            Allocator allocator)
        {
            NativeList<ResolvedBoneFrame> result = new NativeList<ResolvedBoneFrame>(animation.boneFrames.Length, allocator);

            for (int sourceOrder = 0; sourceOrder < animation.boneFrames.Length; ++sourceOrder)
            {
                BuildResolvedBoneFrame(
                    sourceOrder,
                    ref result,
                    animation,
                    ref resolver,
                    boneSolverData,
                    boneCount,
                    frameCount,
                    allocator);
            }
            result.Sort();
            return result;
        }

        private static async Awaitable<NativeList<ResolvedBoneFrame>> BuildSortedResolvedBoneFramesAsync(
            UMTFrameBudget frameBudget,
            VMDAnimation animation,
            IndexResolver resolver,
            NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
            int boneCount,
            int frameCount,
            Allocator allocator)
        {
            NativeList<ResolvedBoneFrame> result = new NativeList<ResolvedBoneFrame>(animation.boneFrames.Length, allocator);

            for (int sourceOrder = 0; sourceOrder < animation.boneFrames.Length; ++sourceOrder)
            {
                BuildResolvedBoneFrame(
                    sourceOrder,
                    ref result,
                    animation,
                    ref resolver,
                    boneSolverData,
                    boneCount,
                    frameCount,
                    allocator);
                if (sourceOrder % 100 == 0)
                {
                    await frameBudget.YieldIfNeeded();
                }
            }
            SortJob<ResolvedBoneFrame, NativeSortExtension.DefaultComparer<ResolvedBoneFrame>> sortJob = result.SortJob();
            JobHandle handle = sortJob.Schedule();
            for (long i = 0; !handle.IsCompleted; ++i)
            {
                if (i % 100 == 0)
                {
                    await frameBudget.YieldIfNeeded();
                }
            }
            handle.Complete();
            return result;
        }

        private static void BuildResolvedBoneFrame(
            int sourceOrder,
            ref NativeList<ResolvedBoneFrame> result,
            VMDAnimation animation,
            ref IndexResolver resolver,
            in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
            int boneCount,
            int frameCount,
            Allocator allocator)
        {
            VMDBoneFrame frame = animation.boneFrames[sourceOrder];
            int boneIndex = resolver.ResolveBoneIndex(frame.boneName);
            if (boneIndex < 0 || boneIndex >= boneCount)
            {
                return;
            }

            int frameIndex = checked((int)frame.frame);
            if (frameIndex >= frameCount)
            {
                return;
            }

            result.Add(new ResolvedBoneFrame(
                boneIndex,
                frame.frame,
                sourceOrder,
                ToBoneSample(
                    frame,
                    boneSolverData[boneIndex].initialLocalPosition,
                    boneSolverData[boneIndex].initialLocalRotation)));
        }

        private static NativeList<ResolvedIKToggleFrame> BuildSortedResolvedIKToggleFrames(
            VMDAnimation animation,
            ref IndexResolver resolver,
            in NativeArray<int> ikControllerByBoneIndex,
            int boneCount,
            int frameCount,
            Allocator allocator)
        {
            int toggleFrameCount = GetIKToggleFrameCount(animation);
            ResolvedIKToggleFrame[] sortedFrames = new ResolvedIKToggleFrame[toggleFrameCount];
            int resultCount = 0;
            int sourceOrder = 0;
            for (int showIKFrameIndex = 0; showIKFrameIndex < animation.showIKFrames.Length; ++showIKFrameIndex)
            {
                VMDShowIKFrame showIKFrame = animation.showIKFrames[showIKFrameIndex];
                for (int toggleIndex = 0; toggleIndex < showIKFrame.ikToggles.Length; ++toggleIndex)
                {
                    VMDIKToggleFrame toggle = showIKFrame.ikToggles[toggleIndex];
                    int boneIndex = resolver.ResolveBoneIndex(toggle.boneName);
                    if (boneIndex < 0 ||
                        boneIndex >= boneCount ||
                        boneIndex >= ikControllerByBoneIndex.Length ||
                        ikControllerByBoneIndex[boneIndex] < 0)
                    {
                        ++sourceOrder;
                        continue;
                    }

                    int frameIndex = checked((int)toggle.frame);
                    if (frameIndex >= frameCount)
                    {
                        ++sourceOrder;
                        continue;
                    }

                    sortedFrames[resultCount] = new ResolvedIKToggleFrame(
                        boneIndex,
                        toggle.frame,
                        sourceOrder,
                        toggle.enabled);
                    ++resultCount;
                    ++sourceOrder;
                }
            }

            if (resultCount != sortedFrames.Length)
            {
                Array.Resize(ref sortedFrames, resultCount);
            }
            Array.Sort(sortedFrames);

            NativeList<ResolvedIKToggleFrame> result = new NativeList<ResolvedIKToggleFrame>(resultCount, allocator);
            for (int i = 0; i < sortedFrames.Length; ++i)
            {
                result.Add(sortedFrames[i]);
            }
            return result;
        }

        [BurstCompile]
        private static class AnimationMath
        {
            /// <summary>
            /// Assigns a compact track index to each bone that has resolved source frames, marking selected bones and
            /// collecting their indices in first-seen order.
            /// </summary>
            /// <param name="frames">Sorted resolved bone frames to scan.</param>
            /// <param name="sourceBoneSelection">Output flags marking bones that have source frames.</param>
            /// <param name="sourceTrackIndexByBone">Output map from bone index to compact track index (-1 if none).</param>
            /// <param name="sourceBoneIndices">Output list of bone indices that have source tracks.</param>
            [BurstCompile]
            internal static void BuildSourceBoneTracks(
                in NativeList<ResolvedBoneFrame> frames,
                ref NativeArray<bool> sourceBoneSelection,
                ref NativeArray<int> sourceTrackIndexByBone,
                ref NativeList<int> sourceBoneIndices)
            {
                FillIndexArray(ref sourceTrackIndexByBone, -1);
                sourceBoneIndices.Clear();
                for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
                {
                    int boneIndex = frames[frameIndex].boneIndex;
                    if (boneIndex < 0 ||
                        boneIndex >= sourceTrackIndexByBone.Length ||
                        sourceTrackIndexByBone[boneIndex] >= 0)
                    {
                        continue;
                    }

                    sourceTrackIndexByBone[boneIndex] = sourceBoneIndices.Length;
                    sourceBoneSelection[boneIndex] = true;
                    sourceBoneIndices.Add(boneIndex);
                }
            }

            /// <summary>
            /// Scatters each resolved bone frame into the compact per-track sample buffer at its frame index.
            /// </summary>
            /// <param name="frames">Sorted resolved bone frames to place.</param>
            /// <param name="sourceTrackIndexByBone">Map from bone index to compact track index.</param>
            /// <param name="boneSamples">Output flattened (track, frame) sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            [BurstCompile]
            internal static void FillCompactBoneSamples(
                in NativeList<ResolvedBoneFrame> frames,
                in NativeArray<int> sourceTrackIndexByBone,
                ref NativeArray<BoneSample> boneSamples,
                int frameCount)
            {
                for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
                {
                    FillCompactBoneSample(frameIndex, frames, sourceTrackIndexByBone, ref boneSamples, frameCount);
                }
            }

            /// <summary>
            /// Asynchronously scatters each resolved bone frame into the compact per-track sample buffer at its frame index.
            /// </summary>
            /// <param name="frameBudget">Frame budget for yielding.</param>
            /// <param name="frames">Sorted resolved bone frames to place.</param>
            /// <param name="sourceTrackIndexByBone">Map from bone index to compact track index.</param>
            /// <param name="boneSamples">Output flattened (track, frame) sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            internal static async Awaitable FillCompactBoneSamplesAsync(
                UMTFrameBudget frameBudget,
                NativeList<ResolvedBoneFrame> frames,
                NativeArray<int> sourceTrackIndexByBone,
                NativeArray<BoneSample> boneSamples,
                int frameCount)
            {
                for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
                {
                    FillCompactBoneSample(frameIndex, frames, sourceTrackIndexByBone, ref boneSamples, frameCount);
                    if (frameIndex % 100 == 0)
                    {
                        await frameBudget.YieldIfNeeded();
                    }
                }
            }

            [BurstCompile]
            private static void FillCompactBoneSample(
                int frameIndex,
                in NativeList<ResolvedBoneFrame> frames,
                in NativeArray<int> sourceTrackIndexByBone,
                ref NativeArray<BoneSample> boneSamples,
                int frameCount)
            {
                ResolvedBoneFrame frame = frames[frameIndex];
                int trackIndex = sourceTrackIndexByBone[frame.boneIndex];
                if (trackIndex < 0)
                {
                    return;
                }

                boneSamples[trackIndex * frameCount + checked((int)frame.frame)] = frame.sample;
            }

            /// <summary>
            /// Fills every gap in each compact bone track by Bezier/slerp interpolation between surrounding keys,
            /// seeding frame 0 from the initial pose and recording the last keyed frame per bone.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data providing initial local transforms.</param>
            /// <param name="sourceBoneIndices">Bone indices that have source tracks.</param>
            /// <param name="boneSamples">Flattened (track, frame) sample buffer to fill in place.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            /// <param name="lastFrameWithSampleByBone">Output last keyed frame index per bone.</param>
            [BurstCompile]
            internal static void InterpolateCompactBoneSamples(
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeList<int> sourceBoneIndices,
                ref NativeArray<BoneSample> boneSamples,
                int frameCount,
                ref NativeArray<int> lastFrameWithSampleByBone)
            {
                for (int trackIndex = 0; trackIndex < sourceBoneIndices.Length; ++trackIndex)
                {
                    int boneIndex = sourceBoneIndices[trackIndex];
                    int sampleStartIndex = trackIndex * frameCount;
                    if (!boneSamples[sampleStartIndex].hasKey)
                    {
                        boneSamples[sampleStartIndex] = new BoneSample(
                            boneSolverData[boneIndex].initialLocalPosition,
                            boneSolverData[boneIndex].initialLocalRotation,
                            true);
                    }

                    ApplyShortestRotationPath(ref boneSamples, sampleStartIndex, frameCount);
                    int previousKeyIndex = 0;
                    int nextKeyIndex = FindNextBoneKey(in boneSamples, sampleStartIndex, frameCount, 1);
                    for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                    {
                        int sampleIndex = sampleStartIndex + frameIndex;
                        if (boneSamples[sampleIndex].hasKey)
                        {
                            previousKeyIndex = frameIndex;
                            nextKeyIndex = FindNextBoneKey(in boneSamples, sampleStartIndex, frameCount, frameIndex + 1);
                        }
                        else if (nextKeyIndex >= 0)
                        {
                            BoneSample previousSample = boneSamples[sampleStartIndex + previousKeyIndex];
                            BoneSample nextSample = boneSamples[sampleStartIndex + nextKeyIndex];
                            InterpolateBoneSample(
                                in previousSample,
                                in nextSample,
                                previousKeyIndex,
                                nextKeyIndex,
                                frameIndex,
                                out BoneSample interpolatedSample);
                            boneSamples[sampleIndex] = interpolatedSample;
                        }
                        else
                        {
                            boneSamples[sampleIndex] = boneSamples[sampleStartIndex + previousKeyIndex];
                        }
                    }

                    lastFrameWithSampleByBone[boneIndex] = previousKeyIndex;
                }
            }

            /// <summary>
            /// Asynchronously fills every gap in each compact bone track by Bezier/slerp interpolation between surrounding keys,
            /// seeding frame 0 from the initial pose and recording the last keyed frame per bone.
            /// </summary>
            /// <param name="frameBudget">Frame budget for yielding.</param>
            /// <param name="boneSolverData">Per-bone solver data providing initial local transforms.</param>
            /// <param name="sourceBoneIndices">Bone indices that have source tracks.</param>
            /// <param name="boneSamples">Flattened (track, frame) sample buffer to fill in place.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            /// <param name="lastFrameWithSampleByBone">Output last keyed frame index per bone.</param>
            internal static async Awaitable InterpolateCompactBoneSamplesAsync(
                UMTFrameBudget frameBudget,
                NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                NativeList<int> sourceBoneIndices,
                NativeArray<BoneSample> boneSamples,
                int frameCount,
                NativeArray<int> lastFrameWithSampleByBone)
            {
                for (int trackIndex = 0; trackIndex < sourceBoneIndices.Length; ++trackIndex)
                {
                    InterpolateCompactBoneSample(
                        trackIndex,
                        in boneSolverData,
                        in sourceBoneIndices,
                        ref boneSamples,
                        frameCount,
                        ref lastFrameWithSampleByBone);
                    await frameBudget.YieldIfNeeded();
                }
            }

            [BurstCompile]
            private static void InterpolateCompactBoneSample(
                int trackIndex,
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeList<int> sourceBoneIndices,
                ref NativeArray<BoneSample> boneSamples,
                int frameCount,
                ref NativeArray<int> lastFrameWithSampleByBone)
            {
                int boneIndex = sourceBoneIndices[trackIndex];
                int sampleStartIndex = trackIndex * frameCount;
                if (!boneSamples[sampleStartIndex].hasKey)
                {
                    boneSamples[sampleStartIndex] = new BoneSample(
                        boneSolverData[boneIndex].initialLocalPosition,
                        boneSolverData[boneIndex].initialLocalRotation,
                        true);
                }

                ApplyShortestRotationPath(ref boneSamples, sampleStartIndex, frameCount);
                int previousKeyIndex = 0;
                int nextKeyIndex = FindNextBoneKey(in boneSamples, sampleStartIndex, frameCount, 1);
                for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                {
                    int sampleIndex = sampleStartIndex + frameIndex;
                    if (boneSamples[sampleIndex].hasKey)
                    {
                        previousKeyIndex = frameIndex;
                        nextKeyIndex = FindNextBoneKey(in boneSamples, sampleStartIndex, frameCount, frameIndex + 1);
                    }
                    else if (nextKeyIndex >= 0)
                    {
                        BoneSample previousSample = boneSamples[sampleStartIndex + previousKeyIndex];
                        BoneSample nextSample = boneSamples[sampleStartIndex + nextKeyIndex];
                        InterpolateBoneSample(
                            in previousSample,
                            in nextSample,
                            previousKeyIndex,
                            nextKeyIndex,
                            frameIndex,
                            out BoneSample interpolatedSample);
                        boneSamples[sampleIndex] = interpolatedSample;
                    }
                    else
                    {
                        boneSamples[sampleIndex] = boneSamples[sampleStartIndex + previousKeyIndex];
                    }
                }

                lastFrameWithSampleByBone[boneIndex] = previousKeyIndex;
            }

            /// <summary>
            /// Assigns a compact track index to each bone that has resolved IK toggle frames, collecting their indices
            /// in first-seen order.
            /// </summary>
            /// <param name="frames">Sorted resolved IK toggle frames to scan.</param>
            /// <param name="ikTrackIndexByBone">Output map from bone index to compact IK track index (-1 if none).</param>
            /// <param name="ikBoneIndices">Output list of bone indices that have IK toggle tracks.</param>
            [BurstCompile]
            internal static void BuildIKToggleTracks(
                in NativeList<ResolvedIKToggleFrame> frames,
                ref NativeArray<int> ikTrackIndexByBone,
                ref NativeList<int> ikBoneIndices)
            {
                FillIndexArray(ref ikTrackIndexByBone, -1);
                ikBoneIndices.Clear();
                for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
                {
                    int boneIndex = frames[frameIndex].boneIndex;
                    if (boneIndex < 0 ||
                        boneIndex >= ikTrackIndexByBone.Length ||
                        ikTrackIndexByBone[boneIndex] >= 0)
                    {
                        continue;
                    }

                    ikTrackIndexByBone[boneIndex] = ikBoneIndices.Length;
                    ikBoneIndices.Add(boneIndex);
                }
            }

            /// <summary>
            /// Scatters resolved IK toggle keys into compact per-track buffers, then forward-fills each track so every
            /// frame carries the most recent enabled state (defaulting to enabled before the first key).
            /// </summary>
            /// <param name="frames">Sorted resolved IK toggle frames to place.</param>
            /// <param name="ikTrackIndexByBone">Map from bone index to compact IK track index.</param>
            /// <param name="ikSamplesByTrack">Output flattened (track, frame) IK toggle sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            [BurstCompile]
            internal static void FillCompactIKToggleSamples(
                in NativeList<ResolvedIKToggleFrame> frames,
                in NativeArray<int> ikTrackIndexByBone,
                ref NativeArray<IKToggleFrameSample> ikSamplesByTrack,
                int frameCount)
            {
                for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
                {
                    ResolvedIKToggleFrame frame = frames[frameIndex];
                    int trackIndex = ikTrackIndexByBone[frame.boneIndex];
                    if (trackIndex < 0)
                    {
                        continue;
                    }

                    ikSamplesByTrack[trackIndex * frameCount + checked((int)frame.frame)] = new IKToggleFrameSample(frame.enabled);
                }

                int trackCount = frameCount == 0 ? 0 : ikSamplesByTrack.Length / frameCount;
                for (int trackIndex = 0; trackIndex < trackCount; ++trackIndex)
                {
                    bool enabled = true;
                    int sampleStartIndex = trackIndex * frameCount;
                    for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                    {
                        int sampleIndex = sampleStartIndex + frameIndex;
                        IKToggleFrameSample frameSample = ikSamplesByTrack[sampleIndex];
                        if (frameSample.hasKey)
                        {
                            enabled = frameSample.enabled;
                        }

                        ikSamplesByTrack[sampleIndex] = new IKToggleFrameSample(enabled);
                    }
                }
            }

            private static void AddSparseDependencyEdge(
                int dependencyBoneIndex,
                int affectedBoneIndex,
                ref NativeArray<int> firstDependencyEdgeByBone,
                ref NativeList<int2> dependencyEdges)
            {
                if (dependencyBoneIndex < 0 ||
                    dependencyBoneIndex >= firstDependencyEdgeByBone.Length ||
                    affectedBoneIndex < 0)
                {
                    return;
                }

                int edgeIndex = dependencyEdges.Length;
                dependencyEdges.Add(new int2(affectedBoneIndex, firstDependencyEdgeByBone[dependencyBoneIndex]));
                firstDependencyEdgeByBone[dependencyBoneIndex] = edgeIndex;
            }

            /// <summary>
            /// Determines which bones need baked FK curves by seeding from source-animated, IK-controlled, and
            /// physics-controlled bones, then propagating along constraint and IK dependency edges. Extends each
            /// affected bone's last-sample frame so dependent curves cover the full range.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data describing constraints.</param>
            /// <param name="sourceBoneSelection">Flags marking bones with source animation.</param>
            /// <param name="ikControllerByBoneIndex">Map from bone index to IK controller index (-1 if none).</param>
            /// <param name="ikControllers">IK controller data.</param>
            /// <param name="ikLinks">IK link data referenced by controllers.</param>
            /// <param name="physicsControlledBones">Flags marking physics-controlled bones.</param>
            /// <param name="curveBoneSelection">Output flags marking bones that require baked curves.</param>
            /// <param name="lastFrameWithSampleByBone">Per-bone last keyed frame, extended in place by dependency propagation.</param>
            /// <param name="curveBoneIndices">Output compacted list of bones requiring curves.</param>
            /// <param name="boneCount">Total number of bones.</param>
            /// <param name="lastFrame">Last animation frame index.</param>
            /// <param name="bakePhysics">Whether physics-controlled bones are included.</param>
            [BurstCompile]
            internal static void ResolveBakedCurveBones(
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeArray<bool> sourceBoneSelection,
                in NativeArray<int> ikControllerByBoneIndex,
                in NativeArray<MMDTransformManager.IKControllerData> ikControllers,
                in NativeArray<MMDTransformManager.IKLinkData> ikLinks,
                in NativeArray<bool> physicsControlledBones,
                ref NativeArray<bool> curveBoneSelection,
                ref NativeArray<int> lastFrameWithSampleByBone,
                ref NativeList<int> curveBoneIndices,
                int boneCount,
                int lastFrame,
                bool bakePhysics)
            {

                NativeArray<int> firstDependencyEdgeByBone = new NativeArray<int>(boneCount, Allocator.Persistent);
                NativeList<int2> dependencyEdges = new NativeList<int2>(Allocator.Persistent);
                NativeList<int> propagationQueue = new NativeList<int>(Allocator.Persistent);

                FillIndexArray(ref firstDependencyEdgeByBone, -1);
                dependencyEdges.Clear();
                for (int boneIndex = 0; boneIndex < boneCount; ++boneIndex)
                {
                    MMDBoneTransform.BoneSolverData runtimeData = boneSolverData[boneIndex];
                    if ((runtimeData.rotationConstraint || runtimeData.translationConstraint) &&
                        runtimeData.constraintTargetBoneIndex >= 0 &&
                        runtimeData.constraintTargetBoneIndex < boneCount)
                    {
                        AddSparseDependencyEdge(
                            runtimeData.constraintTargetBoneIndex,
                            boneIndex,
                            ref firstDependencyEdgeByBone,
                            ref dependencyEdges);
                    }
                }

                for (int boneIndex = 0; boneIndex < ikControllerByBoneIndex.Length; ++boneIndex)
                {
                    int controllerIndex = ikControllerByBoneIndex[boneIndex];
                    if (controllerIndex < 0 || controllerIndex >= ikControllers.Length)
                    {
                        continue;
                    }

                    MMDTransformManager.IKControllerData controller = ikControllers[controllerIndex];
                    if (controller.targetBoneIndex >= 0 && controller.targetBoneIndex < boneCount)
                    {
                        AddSparseDependencyEdge(
                            controller.controllerBoneIndex,
                            controller.targetBoneIndex,
                            ref firstDependencyEdgeByBone,
                            ref dependencyEdges);
                    }

                    for (int linkIndex = 0; linkIndex < controller.linkCount; ++linkIndex)
                    {
                        MMDTransformManager.IKLinkData link = ikLinks[controller.linkStartIndex + linkIndex];
                        if (link.boneIndex >= 0 && link.boneIndex < boneCount)
                        {
                            AddSparseDependencyEdge(
                                controller.controllerBoneIndex,
                                link.boneIndex,
                                ref firstDependencyEdgeByBone,
                                ref dependencyEdges);
                        }
                    }
                }

                for (int boneIndex = 0; boneIndex < boneCount && boneIndex < curveBoneSelection.Length; ++boneIndex)
                {
                    curveBoneSelection[boneIndex] = false;
                }

                propagationQueue.Clear();
                for (int boneIndex = 0; boneIndex < boneCount; ++boneIndex)
                {
                    if (boneIndex < sourceBoneSelection.Length && sourceBoneSelection[boneIndex])
                    {
                        SelectCurveBoneAndEnqueue(
                            boneIndex,
                            lastFrameWithSampleByBone[boneIndex],
                            ref curveBoneSelection,
                            ref lastFrameWithSampleByBone,
                            ref propagationQueue);
                    }

                    if (boneIndex < ikControllerByBoneIndex.Length && ikControllerByBoneIndex[boneIndex] >= 0)
                    {
                        SelectCurveBoneAndEnqueue(
                            boneIndex,
                            lastFrame,
                            ref curveBoneSelection,
                            ref lastFrameWithSampleByBone,
                            ref propagationQueue);
                    }

                    if (bakePhysics &&
                        boneIndex < physicsControlledBones.Length &&
                        physicsControlledBones[boneIndex])
                    {
                        SelectCurveBoneAndEnqueue(
                            boneIndex,
                            lastFrame,
                            ref curveBoneSelection,
                            ref lastFrameWithSampleByBone,
                            ref propagationQueue);
                    }
                }

                int queueReadIndex = 0;
                while (queueReadIndex < propagationQueue.Length)
                {
                    int dependencyBoneIndex = propagationQueue[queueReadIndex];
                    ++queueReadIndex;
                    if (dependencyBoneIndex < 0 || dependencyBoneIndex >= firstDependencyEdgeByBone.Length)
                    {
                        continue;
                    }

                    int dependencyLastFrame = lastFrameWithSampleByBone[dependencyBoneIndex];
                    int edgeIndex = firstDependencyEdgeByBone[dependencyBoneIndex];
                    while (edgeIndex >= 0)
                    {
                        int2 edge = dependencyEdges[edgeIndex];
                        int affectedBoneIndex = edge.x;
                        int nextEdgeIndex = edge.y;
                        SelectCurveBoneAndEnqueue(
                            affectedBoneIndex,
                            dependencyLastFrame,
                            ref curveBoneSelection,
                            ref lastFrameWithSampleByBone,
                            ref propagationQueue);
                        edgeIndex = nextEdgeIndex;
                    }
                }

                CompactSelectedIndices(in curveBoneSelection, ref curveBoneIndices);
                firstDependencyEdgeByBone.Dispose();
                dependencyEdges.Dispose();
                propagationQueue.Dispose();
            }

            private static void SelectCurveBoneAndEnqueue(
                int boneIndex,
                int lastFrame,
                ref NativeArray<bool> curveBoneSelection,
                ref NativeArray<int> lastFrameWithSampleByBone,
                ref NativeList<int> propagationQueue)
            {
                if (boneIndex < 0 ||
                    boneIndex >= curveBoneSelection.Length ||
                    boneIndex >= lastFrameWithSampleByBone.Length)
                {
                    return;
                }

                bool shouldEnqueue = false;
                if (!curveBoneSelection[boneIndex])
                {
                    curveBoneSelection[boneIndex] = true;
                    shouldEnqueue = true;
                }

                if (lastFrameWithSampleByBone[boneIndex] < lastFrame)
                {
                    lastFrameWithSampleByBone[boneIndex] = lastFrame;
                    shouldEnqueue = true;
                }

                if (shouldEnqueue)
                {
                    propagationQueue.Add(boneIndex);
                }
            }

            /// <summary>
            /// Applies the compact bone and IK samples for one frame, then runs the MMD transform/physics solver to
            /// produce solved transforms for that bake frame.
            /// </summary>
            /// <param name="transformContext">Transform solver context to drive.</param>
            /// <param name="physicsContext">Physics solver context to advance.</param>
            /// <param name="boneSolverData">Per-bone solver data updated in place.</param>
            /// <param name="ikControllers">IK controller data updated with this frame's enable states.</param>
            /// <param name="sourceTrackIndexByBone">Map from bone index to compact source track index.</param>
            /// <param name="physicsControlledBoneSelection">Flags marking physics-controlled bones.</param>
            /// <param name="boneSamples">Flattened (track, frame) bone sample buffer.</param>
            /// <param name="ikControllerByBoneIndex">Map from bone index to IK controller index.</param>
            /// <param name="ikTrackIndexByBone">Map from bone index to compact IK track index.</param>
            /// <param name="ikSamplesByTrack">Flattened (track, frame) IK toggle sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            /// <param name="frameIndex">The frame to evaluate.</param>
            /// <param name="frameRate">Clip frame rate, used to derive the physics time step.</param>
            /// <param name="bakePhysics">Whether physics is simulated for this bake.</param>
            [BurstCompile]
            internal static void TransformBonesForBakeFrame(
                ref MMDTransformManager.SolverContext transformContext,
                ref MMDPhysicsManager.PhysicsSolverContext physicsContext,
                ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                ref NativeArray<MMDTransformManager.IKControllerData> ikControllers,
                in NativeArray<int> sourceTrackIndexByBone,
                in NativeArray<bool> physicsControlledBoneSelection,
                in NativeArray<BoneSample> boneSamples,
                in NativeArray<int> ikControllerByBoneIndex,
                in NativeArray<int> ikTrackIndexByBone,
                in NativeArray<IKToggleFrameSample> ikSamplesByTrack,
                int frameCount,
                int frameIndex,
                float frameRate,
                bool bakePhysics)
            {
                ApplyCompactBoneSamples(
                    ref boneSolverData,
                    in sourceTrackIndexByBone,
                    in physicsControlledBoneSelection,
                    in boneSamples,
                    frameCount,
                    frameIndex,
                    bakePhysics);
                ApplyCompactIKSamples(
                    ref ikControllers,
                    in ikControllerByBoneIndex,
                    in ikTrackIndexByBone,
                    in ikSamplesByTrack,
                    frameCount,
                    frameIndex);

                float elapsedTime = frameIndex == 0 ? 0.0f : 1.0f / frameRate;
                MMDTransformManager.TransformBonesWithPhysics(
                    ref transformContext,
                    ref physicsContext,
                    elapsedTime);
            }

            /// <summary>
            /// Writes the solved local position and rotation of each curve bone into the baked keyframe buffers for one
            /// frame, skipping bones whose last sample frame has already passed.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data holding solved transforms.</param>
            /// <param name="curveBoneIndices">Bones that receive baked curves.</param>
            /// <param name="lastFrameWithSampleByBone">Per-bone last frame that should be keyed.</param>
            /// <param name="curveBuffers">Destination keyframe buffers.</param>
            /// <param name="frameIndex">The frame being written.</param>
            /// <param name="frameRate">Clip frame rate, used to compute keyframe time.</param>
            [BurstCompile]
            internal static void WriteBakedBoneCurvesForFrame(
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeList<int> curveBoneIndices,
                in NativeArray<int> lastFrameWithSampleByBone,
                ref BakedBoneCurveBuffers curveBuffers,
                int frameIndex,
                float frameRate)
            {
                float time = frameIndex / frameRate;
                for (int curveBoneIndexIndex = 0; curveBoneIndexIndex < curveBoneIndices.Length; ++curveBoneIndexIndex)
                {
                    int boneIndex = curveBoneIndices[curveBoneIndexIndex];
                    if (frameIndex > lastFrameWithSampleByBone[boneIndex])
                    {
                        continue;
                    }

                    MMDBoneTransform.BoneSolverData runtimeData = boneSolverData[boneIndex];
                    float3 position = runtimeData.hasSolvedTransform
                        ? runtimeData.solvedLocalPosition
                        : runtimeData.localPosition;
                    quaternion rotation = runtimeData.hasSolvedTransform
                        ? runtimeData.solvedLocalRotation
                        : runtimeData.localRotation;
                    int keyframeIndex = curveBuffers.keyframeStartByBone[boneIndex] + frameIndex;
                    curveBuffers.positionX[keyframeIndex] = new Keyframe(time, position.x);
                    curveBuffers.positionY[keyframeIndex] = new Keyframe(time, position.y);
                    curveBuffers.positionZ[keyframeIndex] = new Keyframe(time, position.z);
                    curveBuffers.rotationX[keyframeIndex] = new Keyframe(time, rotation.value.x);
                    curveBuffers.rotationY[keyframeIndex] = new Keyframe(time, rotation.value.y);
                    curveBuffers.rotationZ[keyframeIndex] = new Keyframe(time, rotation.value.z);
                    curveBuffers.rotationW[keyframeIndex] = new Keyframe(time, rotation.value.w);
                }
            }

            /// <summary>
            /// Copies the frame's compact bone samples into the solver data as local transforms, falling back to the
            /// initial pose for untracked bones and optionally skipping physics-controlled bones.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data updated in place.</param>
            /// <param name="sourceTrackIndexByBone">Map from bone index to compact source track index.</param>
            /// <param name="physicsControlledBoneSelection">Flags marking physics-controlled bones.</param>
            /// <param name="boneSamples">Flattened (track, frame) bone sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            /// <param name="frameIndex">The frame to apply.</param>
            /// <param name="skipPhysicsControlledBones">When true, physics-controlled bones are left untouched.</param>
            [BurstCompile]
            internal static void ApplyCompactBoneSamples(
                ref NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeArray<int> sourceTrackIndexByBone,
                in NativeArray<bool> physicsControlledBoneSelection,
                in NativeArray<BoneSample> boneSamples,
                int frameCount,
                int frameIndex,
                bool skipPhysicsControlledBones)
            {
                for (int boneIndex = 0; boneIndex < boneSolverData.Length; ++boneIndex)
                {
                    if (skipPhysicsControlledBones &&
                        boneIndex < physicsControlledBoneSelection.Length &&
                        physicsControlledBoneSelection[boneIndex])
                    {
                        continue;
                    }

                    MMDBoneTransform.BoneSolverData boneData = boneSolverData[boneIndex];
                    int trackIndex = boneIndex < sourceTrackIndexByBone.Length
                        ? sourceTrackIndexByBone[boneIndex]
                        : -1;
                    BoneSample sample = trackIndex >= 0
                        ? boneSamples[trackIndex * frameCount + frameIndex]
                        : new BoneSample(boneData.initialLocalPosition, boneData.initialLocalRotation);
                    boneData.localPosition = sample.position;
                    boneData.localRotation = sample.rotation;
                    boneData.hasSolvedTransform = false;
                    boneData.solvedByIK = false;
                    boneSolverData[boneIndex] = boneData;
                }
            }

            /// <summary>
            /// Sets each IK controller's enabled state for the given frame from its compact IK toggle track, defaulting
            /// to enabled when a bone has no toggle track.
            /// </summary>
            /// <param name="ikControllers">IK controller data updated in place.</param>
            /// <param name="ikControllerByBoneIndex">Map from bone index to IK controller index.</param>
            /// <param name="ikTrackIndexByBone">Map from bone index to compact IK track index.</param>
            /// <param name="ikSamplesByTrack">Flattened (track, frame) IK toggle sample buffer.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            /// <param name="frameIndex">The frame to apply.</param>
            [BurstCompile]
            internal static void ApplyCompactIKSamples(
                ref NativeArray<MMDTransformManager.IKControllerData> ikControllers,
                in NativeArray<int> ikControllerByBoneIndex,
                in NativeArray<int> ikTrackIndexByBone,
                in NativeArray<IKToggleFrameSample> ikSamplesByTrack,
                int frameCount,
                int frameIndex)
            {
                for (int boneIndex = 0; boneIndex < ikControllerByBoneIndex.Length; ++boneIndex)
                {
                    int ikControllerIndex = ikControllerByBoneIndex[boneIndex];
                    if (ikControllerIndex < 0 || ikControllerIndex >= ikControllers.Length)
                    {
                        continue;
                    }

                    MMDTransformManager.IKControllerData ikController = ikControllers[ikControllerIndex];
                    int trackIndex = boneIndex < ikTrackIndexByBone.Length
                        ? ikTrackIndexByBone[boneIndex]
                        : -1;
                    ikController.enabled = trackIndex >= 0
                        ? ikSamplesByTrack[trackIndex * frameCount + frameIndex].enabled
                        : true;
                    ikControllers[ikControllerIndex] = ikController;
                }
            }

            /// <summary>
            /// Builds a combined sample buffer that prepends a setup ramp (interpolating from the initial pose to the
            /// first source frame, using the shortest rotation path) before the original bone samples.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data providing initial transforms.</param>
            /// <param name="sourceBoneIndices">Bone indices that have source tracks.</param>
            /// <param name="sourceBoneSamples">Original bone samples to append after the setup ramp.</param>
            /// <param name="combinedBoneSamples">Output combined sample buffer (setup ramp plus source frames).</param>
            /// <param name="sourceFrameCount">Number of frames in the source buffer.</param>
            /// <param name="setupFrameCount">Number of setup (warm-up) frames to prepend.</param>
            /// <param name="combinedFrameCount">Number of frames per track in the combined buffer.</param>
            [BurstCompile]
            internal static void PrependSetupBoneSamples(
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeList<int> sourceBoneIndices,
                in NativeArray<BoneSample> sourceBoneSamples,
                ref NativeArray<BoneSample> combinedBoneSamples,
                int sourceFrameCount,
                int setupFrameCount,
                int combinedFrameCount)
            {
                for (int trackIndex = 0; trackIndex < sourceBoneIndices.Length; ++trackIndex)
                {
                    int boneIndex = sourceBoneIndices[trackIndex];
                    int sourceStart = trackIndex * sourceFrameCount;
                    int combinedStart = trackIndex * combinedFrameCount;

                    float3 initialPosition = boneSolverData[boneIndex].initialLocalPosition;
                    quaternion initialRotation = boneSolverData[boneIndex].initialLocalRotation;
                    BoneSample frame0Sample = sourceBoneSamples[sourceStart + 0];
                    quaternion targetRotation = frame0Sample.rotation;
                    if (math.dot(initialRotation, targetRotation) < 0.0f)
                    {
                        targetRotation = new quaternion(
                            -targetRotation.value.x,
                            -targetRotation.value.y,
                            -targetRotation.value.z,
                            -targetRotation.value.w);
                    }

                    for (int setupIndex = 0; setupIndex < setupFrameCount; ++setupIndex)
                    {
                        float t = setupIndex / (float)setupFrameCount;
                        float3 position = math.lerp(initialPosition, frame0Sample.position, t);
                        quaternion rotation = math.nlerp(initialRotation, targetRotation, t);
                        combinedBoneSamples[combinedStart + setupIndex] = new BoneSample(position, rotation, true);
                    }

                    for (int frameIndex = 0; frameIndex < sourceFrameCount; ++frameIndex)
                    {
                        combinedBoneSamples[combinedStart + setupFrameCount + frameIndex] = sourceBoneSamples[sourceStart + frameIndex];
                    }
                }
            }

            /// <summary>
            /// Builds a combined IK toggle buffer that prepends setup frames (repeating the first source frame's state)
            /// before the original IK toggle samples.
            /// </summary>
            /// <param name="sourceIKSamples">Original IK toggle samples to append after the setup frames.</param>
            /// <param name="combinedIKSamples">Output combined IK toggle buffer.</param>
            /// <param name="ikTrackCount">Number of IK toggle tracks.</param>
            /// <param name="sourceFrameCount">Number of frames in the source buffer.</param>
            /// <param name="setupFrameCount">Number of setup (warm-up) frames to prepend.</param>
            /// <param name="combinedFrameCount">Number of frames per track in the combined buffer.</param>
            [BurstCompile]
            internal static void PrependSetupIKSamples(
                in NativeArray<IKToggleFrameSample> sourceIKSamples,
                ref NativeArray<IKToggleFrameSample> combinedIKSamples,
                int ikTrackCount,
                int sourceFrameCount,
                int setupFrameCount,
                int combinedFrameCount)
            {
                for (int trackIndex = 0; trackIndex < ikTrackCount; ++trackIndex)
                {
                    int sourceStart = trackIndex * sourceFrameCount;
                    int combinedStart = trackIndex * combinedFrameCount;
                    IKToggleFrameSample frame0Sample = sourceIKSamples[sourceStart + 0];
                    for (int setupIndex = 0; setupIndex < setupFrameCount; ++setupIndex)
                    {
                        combinedIKSamples[combinedStart + setupIndex] = frame0Sample;
                    }
                    for (int frameIndex = 0; frameIndex < sourceFrameCount; ++frameIndex)
                    {
                        combinedIKSamples[combinedStart + setupFrameCount + frameIndex] = sourceIKSamples[sourceStart + frameIndex];
                    }
                }
            }

            private static void FillIndexArray(ref NativeArray<int> array, int value)
            {
                for (int i = 0; i < array.Length; ++i)
                {
                    array[i] = value;
                }
            }

            /// <summary>
            /// Runs the full morph sample pipeline: resolves direct vertex morph samples, expands group morph offsets,
            /// forward-fills gaps, compacts the selected morphs, and builds their curve keyframes.
            /// </summary>
            /// <param name="frames">VMD morph frames to process.</param>
            /// <param name="resolver">Resolver mapping morph names to indices.</param>
            /// <param name="vertexMorphSelection">Flags marking morphs of vertex type.</param>
            /// <param name="groupMorphOffsets">Group morph offsets that redirect to vertex morphs.</param>
            /// <param name="morphSelection">Output flags marking morphs that receive curves.</param>
            /// <param name="samplesByMorph">Output flattened (morph, frame) sample buffer.</param>
            /// <param name="curveMorphIndices">Output compacted list of morphs with curves.</param>
            /// <param name="keyframesByMorph">Output flattened (morph, key) keyframe buffer (time, value).</param>
            /// <param name="keyframeCountsByMorph">Output keyframe count per morph.</param>
            /// <param name="morphCount">Total number of morphs.</param>
            /// <param name="frameCount">Number of frames.</param>
            /// <param name="frameRate">Clip frame rate, used to compute keyframe times.</param>
            [BurstCompile]
            internal static void PrepareMorphData(
                in NativeArray<VMDMorphFrame> frames,
                ref IndexResolver resolver,
                in NativeArray<bool> vertexMorphSelection,
                in NativeList<NativeGroupMorphOffset> groupMorphOffsets,
                ref NativeArray<bool> morphSelection,
                ref NativeArray<MorphSample> samplesByMorph,
                ref NativeList<int> curveMorphIndices,
                ref NativeArray<float2> keyframesByMorph,
                ref NativeArray<int> keyframeCountsByMorph,
                int morphCount,
                int frameCount,
                float frameRate)
            {
                BuildMorphSamplesByMorph(
                    in frames,
                    ref resolver,
                    in vertexMorphSelection,
                    ref morphSelection,
                    ref samplesByMorph,
                    morphCount,
                    frameCount);
                BuildGroupMorphSamples(
                    in frames,
                    ref resolver,
                    in groupMorphOffsets,
                    ref morphSelection,
                    ref samplesByMorph,
                    morphCount,
                    frameCount);
                ResolveMorphSamples(
                    ref morphSelection,
                    ref samplesByMorph,
                    morphCount,
                    frameCount);
                CompactSelectedIndices(in morphSelection, ref curveMorphIndices);
                BuildMorphCurveKeyframes(
                    in curveMorphIndices,
                    in samplesByMorph,
                    ref keyframesByMorph,
                    ref keyframeCountsByMorph,
                    frameCount,
                    frameRate);
            }

            /// <summary>
            /// Scatters VMD morph frames that resolve to selected vertex morphs into the per-morph sample buffer,
            /// marking those morphs as selected.
            /// </summary>
            /// <param name="frames">VMD morph frames to scan.</param>
            /// <param name="resolver">Resolver mapping morph names to indices.</param>
            /// <param name="vertexMorphSelection">Flags marking morphs of vertex type.</param>
            /// <param name="morphSelection">Output flags marking morphs that have samples.</param>
            /// <param name="samplesByMorph">Output flattened (morph, frame) sample buffer.</param>
            /// <param name="morphCount">Total number of morphs.</param>
            /// <param name="frameCount">Number of frames.</param>
            [BurstCompile]
            internal static void BuildMorphSamplesByMorph(
                in NativeArray<VMDMorphFrame> frames,
                ref IndexResolver resolver,
                in NativeArray<bool> vertexMorphSelection,
                ref NativeArray<bool> morphSelection,
                ref NativeArray<MorphSample> samplesByMorph,
                int morphCount,
                int frameCount)
            {
                for (int vmdFrameIndex = 0; vmdFrameIndex < frames.Length; ++vmdFrameIndex)
                {
                    VMDMorphFrame frame = frames[vmdFrameIndex];
                    int morphIndex = resolver.ResolveMorphIndex(frame.morphName);
                    if (morphIndex < 0 ||
                        morphIndex >= morphCount ||
                        morphIndex >= vertexMorphSelection.Length ||
                        !vertexMorphSelection[morphIndex])
                    {
                        continue;
                    }

                    int frameIndex = checked((int)frame.frame);
                    if (frameIndex >= frameCount)
                    {
                        continue;
                    }

                    morphSelection[morphIndex] = true;
                    samplesByMorph[morphIndex * frameCount + frameIndex] = new MorphSample(frame.weight, true);
                }
            }

            /// <summary>
            /// Forward-fills gaps in each selected morph's sample track so every frame carries the most recent keyed
            /// weight, seeding frame 0 with zero when unkeyed.
            /// </summary>
            /// <param name="morphSelection">Flags marking morphs that have samples.</param>
            /// <param name="samplesByMorph">Flattened (morph, frame) sample buffer filled in place.</param>
            /// <param name="morphCount">Total number of morphs.</param>
            /// <param name="frameCount">Number of frames.</param>
            [BurstCompile]
            internal static void ResolveMorphSamples(
                ref NativeArray<bool> morphSelection,
                ref NativeArray<MorphSample> samplesByMorph,
                int morphCount,
                int frameCount)
            {
                for (int morphIndex = 0; morphIndex < morphCount && morphIndex < morphSelection.Length; ++morphIndex)
                {
                    if (!morphSelection[morphIndex])
                    {
                        continue;
                    }

                    int sampleStartIndex = morphIndex * frameCount;
                    if (!samplesByMorph[sampleStartIndex].hasKey)
                    {
                        samplesByMorph[sampleStartIndex] = new MorphSample(0.0f, true);
                    }

                    int previousKeyIndex = 0;
                    for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                    {
                        int sampleIndex = sampleStartIndex + frameIndex;
                        if (samplesByMorph[sampleIndex].hasKey)
                        {
                            previousKeyIndex = frameIndex;
                        }
                        else
                        {
                            samplesByMorph[sampleIndex] = new MorphSample(samplesByMorph[sampleStartIndex + previousKeyIndex].weight, false);
                        }
                    }
                }
            }

            /// <summary>
            /// Builds (time, value) keyframes for each selected morph from its keyed samples, scaling weights to the
            /// 0..100 blend-shape range, and records the keyframe count per morph.
            /// </summary>
            /// <param name="curveMorphIndices">Morphs that receive curves.</param>
            /// <param name="samplesByMorph">Flattened (morph, frame) sample buffer.</param>
            /// <param name="keyframesByMorph">Output flattened (morph, key) keyframe buffer (time, value).</param>
            /// <param name="keyframeCountsByMorph">Output keyframe count per morph.</param>
            /// <param name="frameCount">Number of frames.</param>
            /// <param name="frameRate">Clip frame rate, used to compute keyframe times.</param>
            [BurstCompile]
            internal static void BuildMorphCurveKeyframes(
                in NativeList<int> curveMorphIndices,
                in NativeArray<MorphSample> samplesByMorph,
                ref NativeArray<float2> keyframesByMorph,
                ref NativeArray<int> keyframeCountsByMorph,
                int frameCount,
                float frameRate)
            {
                for (int trackIndex = 0; trackIndex < curveMorphIndices.Length; ++trackIndex)
                {
                    int morphIndex = curveMorphIndices[trackIndex];
                    int sampleStartIndex = morphIndex * frameCount;
                    int keyframeCount = 0;
                    for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
                    {
                        MorphSample sample = samplesByMorph[sampleStartIndex + frameIndex];
                        if (!sample.hasKey)
                        {
                            continue;
                        }

                        keyframesByMorph[sampleStartIndex + keyframeCount] = new float2(
                            frameIndex / frameRate,
                            sample.weight * 100.0f);
                        ++keyframeCount;
                    }

                    keyframeCountsByMorph[morphIndex] = keyframeCount;
                }
            }

            /// <summary>
            /// Expands group morph frames into their target vertex morph samples, scaling each contribution by the
            /// group offset rate and marking the affected target morphs as selected.
            /// </summary>
            /// <param name="frames">VMD morph frames to scan.</param>
            /// <param name="resolver">Resolver mapping morph names to indices.</param>
            /// <param name="groupMorphOffsets">Group morph offsets that redirect to vertex morphs.</param>
            /// <param name="morphSelection">Output flags marking affected target morphs.</param>
            /// <param name="samplesByMorph">Output flattened (morph, frame) sample buffer.</param>
            /// <param name="morphCount">Total number of morphs.</param>
            /// <param name="frameCount">Number of frames.</param>
            [BurstCompile]
            internal static void BuildGroupMorphSamples(
                in NativeArray<VMDMorphFrame> frames,
                ref IndexResolver resolver,
                in NativeList<NativeGroupMorphOffset> groupMorphOffsets,
                ref NativeArray<bool> morphSelection,
                ref NativeArray<MorphSample> samplesByMorph,
                int morphCount,
                int frameCount)
            {
                for (int vmdFrameIndex = 0; vmdFrameIndex < frames.Length; ++vmdFrameIndex)
                {
                    VMDMorphFrame frame = frames[vmdFrameIndex];
                    int sourceMorphIndex = resolver.ResolveMorphIndex(frame.morphName);
                    if (sourceMorphIndex < 0)
                    {
                        continue;
                    }

                    int frameIndex = checked((int)frame.frame);
                    if (frameIndex >= frameCount)
                    {
                        continue;
                    }

                    for (int offsetIndex = 0; offsetIndex < groupMorphOffsets.Length; ++offsetIndex)
                    {
                        NativeGroupMorphOffset offset = groupMorphOffsets[offsetIndex];
                        if (offset.sourceMorphIndex != sourceMorphIndex)
                        {
                            continue;
                        }

                        int targetMorphIndex = offset.targetMorphIndex;
                        if (targetMorphIndex < 0 ||
                            targetMorphIndex >= morphCount ||
                            targetMorphIndex >= morphSelection.Length)
                        {
                            continue;
                        }

                        morphSelection[targetMorphIndex] = true;
                        samplesByMorph[targetMorphIndex * frameCount + frameIndex] = new MorphSample(frame.weight * offset.rate, true);
                    }
                }
            }

            /// <summary>
            /// Collects the indices of all set flags in <paramref name="selected"/> into <paramref name="result"/> in
            /// ascending order.
            /// </summary>
            /// <param name="selected">Selection flags to compact.</param>
            /// <param name="result">Output list cleared and filled with the selected indices.</param>
            [BurstCompile]
            internal static void CompactSelectedIndices(in NativeArray<bool> selected, ref NativeList<int> result)
            {
                result.Clear();
                for (int i = 0; i < selected.Length; ++i)
                {
                    if (selected[i])
                    {
                        result.Add(i);
                    }
                }
            }

            /// <summary>
            /// Interpolates a bone sample at <paramref name="frame"/> between two keyframes, applying the destination
            /// keyframe's per-channel Bezier interpolation to position (lerp) and rotation (slerp).
            /// </summary>
            /// <param name="previous">The previous keyed sample.</param>
            /// <param name="next">The next keyed sample, whose interpolation curves are used.</param>
            /// <param name="previousFrame">Frame index of the previous key.</param>
            /// <param name="nextFrame">Frame index of the next key.</param>
            /// <param name="frame">The frame to evaluate.</param>
            /// <param name="result">The interpolated bone sample.</param>
            [BurstCompile]
            internal static void InterpolateBoneSample(
                in BoneSample previous,
                in BoneSample next,
                int previousFrame,
                int nextFrame,
                int frame,
                out BoneSample result)
            {
                float range = nextFrame - previousFrame;
                float normalizedTime = (frame - previousFrame) / range;
                float3 position = new float3(
                    math.lerp(previous.position.x, next.position.x, EvaluateBoneInterpolation(next, 0, normalizedTime)),
                    math.lerp(previous.position.y, next.position.y, EvaluateBoneInterpolation(next, 1, normalizedTime)),
                    math.lerp(previous.position.z, next.position.z, EvaluateBoneInterpolation(next, 2, normalizedTime)));
                quaternion rotation = math.slerp(previous.rotation, next.rotation, EvaluateBoneInterpolation(next, 3, normalizedTime));
                result = new BoneSample(position, math.normalize(rotation), false);
            }

            private static void ApplyShortestRotationPath(ref NativeArray<BoneSample> samples, int startIndex, int sampleCount)
            {
                int previousKeyIndex = FindNextBoneKey(in samples, startIndex, sampleCount, 0);
                int nextKeyIndex = FindNextBoneKey(in samples, startIndex, sampleCount, previousKeyIndex + 1);
                while (nextKeyIndex >= 0)
                {
                    BoneSample previousSample = samples[startIndex + previousKeyIndex];
                    BoneSample nextSample = samples[startIndex + nextKeyIndex];
                    samples[startIndex + nextKeyIndex] = EnsureShortestRotationPath(previousSample, nextSample);
                    previousKeyIndex = nextKeyIndex;
                    nextKeyIndex = FindNextBoneKey(in samples, startIndex, sampleCount, previousKeyIndex + 1);
                }
            }

            private static BoneSample EnsureShortestRotationPath(BoneSample previous, BoneSample next)
            {
                if (math.dot(previous.rotation, next.rotation) >= 0.0f)
                {
                    return next;
                }

                quaternion flippedRotation = new quaternion(
                    -next.rotation.value.x,
                    -next.rotation.value.y,
                    -next.rotation.value.z,
                    -next.rotation.value.w);
                return new BoneSample(
                    next.frame,
                    next.position,
                    flippedRotation,
                    next.hasKey,
                    next.hasInterpolation,
                    next.interpolation);
            }

            private static int FindNextBoneKey(in NativeArray<BoneSample> samples, int startIndex, int sampleCount, int localStartIndex)
            {
                for (int i = localStartIndex; i < sampleCount; ++i)
                {
                    if (samples[startIndex + i].hasKey)
                    {
                        return i;
                    }
                }
                return -1;
            }

            private static float EvaluateBoneInterpolation(BoneSample sample, int channel, float normalizedTime)
            {
                if (!sample.hasInterpolation)
                {
                    return normalizedTime;
                }

                VMDBezierInterpolation interpolation = GetBoneInterpolationChannel(sample.interpolation, channel);
                return EvaluateBezier(interpolation, normalizedTime);
            }

            private static VMDBezierInterpolation GetBoneInterpolationChannel(VMDBoneInterpolation interpolation, int channel)
            {
                if (channel == 0)
                {
                    return interpolation.positionX;
                }

                if (channel == 1)
                {
                    return interpolation.positionY;
                }

                if (channel == 2)
                {
                    return interpolation.positionZ;
                }

                return interpolation.rotation;
            }

            private static float EvaluateBezier(VMDBezierInterpolation interpolation, float targetX)
            {
                float lower = 0.0f;
                float upper = 1.0f;
                float t = targetX;
                for (int i = 0; i < 12; ++i)
                {
                    float x = CubicBezier(0.0f, interpolation.x1, interpolation.x2, 1.0f, t);
                    if (x < targetX)
                    {
                        lower = t;
                    }
                    else
                    {
                        upper = t;
                    }
                    t = (lower + upper) * 0.5f;
                }
                return CubicBezier(0.0f, interpolation.y1, interpolation.y2, 1.0f, t);
            }

            private static float CubicBezier(float p0, float p1, float p2, float p3, float t)
            {
                float inverse = 1.0f - t;
                return inverse * inverse * inverse * p0 +
                    3.0f * inverse * inverse * t * p1 +
                    3.0f * inverse * t * t * p2 +
                    t * t * t * p3;
            }

            /// <summary>
            /// Converts a VMD-space position into Unity space by negating the X and Z axes and scaling by the
            /// MMD-to-Unity unit factor.
            /// </summary>
            /// <param name="position">The position in VMD space.</param>
            /// <param name="convertedPosition">The converted position in Unity space.</param>
            [BurstCompile]
            internal static void ConvertPosition(in float3 position, out float3 convertedPosition)
            {
                convertedPosition = new float3(-position.x, position.y, -position.z) * MMDConstants.k_MMDUnitToUnityUnit;
            }

            /// <summary>
            /// Converts a VMD-space rotation into Unity space by negating the X and Z quaternion components and
            /// normalizing the result.
            /// </summary>
            /// <param name="rotation">The rotation in VMD space.</param>
            /// <param name="convertedRotation">The converted, normalized rotation in Unity space.</param>
            [BurstCompile]
            internal static void ConvertRotation(in quaternion rotation, out quaternion convertedRotation)
            {
                convertedRotation = math.normalize(new quaternion(-rotation.value.x, rotation.value.y, -rotation.value.z, rotation.value.w));
            }

            /// <summary>
            /// Seeds frame 0 of each sparse bone track from the initial pose when unkeyed, then adjusts subsequent
            /// keyed rotations to follow the shortest rotation path. Used by the non-baked (sparse) curve path.
            /// </summary>
            /// <param name="boneSolverData">Per-bone solver data providing initial transforms.</param>
            /// <param name="sourceBoneIndices">Bone indices that have source tracks.</param>
            /// <param name="boneSamples">Flattened (track, frame) sample buffer modified in place.</param>
            /// <param name="frameCount">Number of frames per track.</param>
            [BurstCompile]
            internal static void SeedAndFixSparseBoneSamples(
                in NativeArray<MMDBoneTransform.BoneSolverData> boneSolverData,
                in NativeList<int> sourceBoneIndices,
                ref NativeArray<BoneSample> boneSamples,
                int frameCount)
            {
                for (int trackIndex = 0; trackIndex < sourceBoneIndices.Length; ++trackIndex)
                {
                    int boneIndex = sourceBoneIndices[trackIndex];
                    int sampleStartIndex = trackIndex * frameCount;
                    if (!boneSamples[sampleStartIndex].hasKey)
                    {
                        boneSamples[sampleStartIndex] = new BoneSample(
                            0,
                            boneSolverData[boneIndex].initialLocalPosition,
                            boneSolverData[boneIndex].initialLocalRotation,
                            false);
                    }

                    ApplyShortestRotationPath(ref boneSamples, sampleStartIndex, frameCount);
                }
            }

            /// <summary>
            /// Builds sparse local position and Euler rotation keyframes from keyed bone samples, unwrapping Euler
            /// angles for continuity and applying Bezier or linear tangents per channel based on each key's interpolation.
            /// </summary>
            /// <param name="sparseSamples">The keyed bone samples in frame order.</param>
            /// <param name="sampleCount">Number of valid samples.</param>
            /// <param name="frameRate">Clip frame rate, used to compute keyframe times.</param>
            /// <param name="positionX">Output X position keyframes.</param>
            /// <param name="positionY">Output Y position keyframes.</param>
            /// <param name="positionZ">Output Z position keyframes.</param>
            /// <param name="eulerX">Output X Euler rotation keyframes.</param>
            /// <param name="eulerY">Output Y Euler rotation keyframes.</param>
            /// <param name="eulerZ">Output Z Euler rotation keyframes.</param>
            [BurstCompile]
            internal static void BuildSparseBoneKeyframes(
                in NativeArray<BoneSample> sparseSamples,
                int sampleCount,
                float frameRate,
                ref NativeArray<Keyframe> positionX,
                ref NativeArray<Keyframe> positionY,
                ref NativeArray<Keyframe> positionZ,
                ref NativeArray<Keyframe> eulerX,
                ref NativeArray<Keyframe> eulerY,
                ref NativeArray<Keyframe> eulerZ)
            {
                float3 previousEuler = new float3(0.0f, 0.0f, 0.0f);
                for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
                {
                    BoneSample sample = sparseSamples[sampleIndex];
                    float time = sample.frame / frameRate;
                    positionX[sampleIndex] = new Keyframe(time, sample.position.x);
                    positionY[sampleIndex] = new Keyframe(time, sample.position.y);
                    positionZ[sampleIndex] = new Keyframe(time, sample.position.z);

                    float3 euler = math.Euler(sample.rotation, math.RotationOrder.ZXY);
                    if (sampleIndex > 0)
                    {
                        euler.x = previousEuler.x + DeltaAngle(previousEuler.x, euler.x);
                        euler.y = previousEuler.y + DeltaAngle(previousEuler.y, euler.y);
                        euler.z = previousEuler.z + DeltaAngle(previousEuler.z, euler.z);
                    }

                    eulerX[sampleIndex] = new Keyframe(time, euler.x);
                    eulerY[sampleIndex] = new Keyframe(time, euler.y);
                    eulerZ[sampleIndex] = new Keyframe(time, euler.z);
                    previousEuler = euler;
                }

                for (int keyIndex = 1; keyIndex < sampleCount; ++keyIndex)
                {
                    BoneSample nextSample = sparseSamples[keyIndex];
                    if (!nextSample.hasInterpolation)
                    {
                        ApplyLinearKeyframeTangents(ref positionX, keyIndex - 1, keyIndex);
                        ApplyLinearKeyframeTangents(ref positionY, keyIndex - 1, keyIndex);
                        ApplyLinearKeyframeTangents(ref positionZ, keyIndex - 1, keyIndex);
                        ApplyLinearKeyframeTangents(ref eulerX, keyIndex - 1, keyIndex);
                        ApplyLinearKeyframeTangents(ref eulerY, keyIndex - 1, keyIndex);
                        ApplyLinearKeyframeTangents(ref eulerZ, keyIndex - 1, keyIndex);
                        continue;
                    }

                    ApplyBezierChannelTangents(ref positionX, keyIndex, nextSample, 0);
                    ApplyBezierChannelTangents(ref positionY, keyIndex, nextSample, 1);
                    ApplyBezierChannelTangents(ref positionZ, keyIndex, nextSample, 2);
                    ApplyBezierChannelTangents(ref eulerX, keyIndex, nextSample, 3);
                    ApplyBezierChannelTangents(ref eulerY, keyIndex, nextSample, 3);
                    ApplyBezierChannelTangents(ref eulerZ, keyIndex, nextSample, 3);
                }
            }

            private static void ApplyBezierChannelTangents(
                ref NativeArray<Keyframe> keyframes,
                int keyIndex,
                BoneSample nextSample,
                int channel)
            {
                Keyframe previousKey = keyframes[keyIndex - 1];
                Keyframe nextKey = keyframes[keyIndex];
                VMDBezierInterpolation interpolation = GetBoneInterpolationChannel(nextSample.interpolation, channel);
                ApplyBezierTangents(ref previousKey, ref nextKey, interpolation);
                keyframes[keyIndex - 1] = previousKey;
                keyframes[keyIndex] = nextKey;
            }

            private static void ApplyBezierTangents(ref Keyframe previous, ref Keyframe next, VMDBezierInterpolation interpolation)
            {
                float duration = next.time - previous.time;
                float valueDelta = next.value - previous.value;
                if (duration == 0.0f)
                {
                    previous.outTangent = 0.0f;
                    next.inTangent = 0.0f;
                    return;
                }

                previous.outTangent = ComputeBezierStartTangent(interpolation, valueDelta, duration);
                next.inTangent = ComputeBezierEndTangent(interpolation, valueDelta, duration);
                previous.weightedMode = WeightedMode.Both;
                next.weightedMode = WeightedMode.Both;
                previous.outWeight = math.max(math.EPSILON, interpolation.x1);
                next.inWeight = math.max(math.EPSILON, 1.0f - interpolation.x2);
            }

            private static void ApplyLinearKeyframeTangents(ref NativeArray<Keyframe> keyframes, int previousIndex, int nextIndex)
            {
                Keyframe previous = keyframes[previousIndex];
                Keyframe next = keyframes[nextIndex];
                float tangent = (next.value - previous.value) / (next.time - previous.time);
                previous.outTangent = tangent;
                next.inTangent = tangent;
                keyframes[previousIndex] = previous;
                keyframes[nextIndex] = next;
            }

            private static float ComputeBezierStartTangent(VMDBezierInterpolation interpolation, float valueDelta, float duration)
            {
                if (interpolation.x1 == 0.0f)
                {
                    return 0.0f;
                }

                return interpolation.y1 / interpolation.x1 * valueDelta / duration;
            }

            private static float ComputeBezierEndTangent(VMDBezierInterpolation interpolation, float valueDelta, float duration)
            {
                if (interpolation.x2 == 1.0f)
                {
                    return 0.0f;
                }

                return (1.0f - interpolation.y2) / (1.0f - interpolation.x2) * valueDelta / duration;
            }

            private static float DeltaAngle(float current, float target)
            {
                float delta = (target - current) - 360.0f * math.floor((target - current) / 360.0f);
                if (delta > 180.0f)
                {
                    delta -= 360.0f;
                }

                return delta;
            }

            /// <summary>
            /// Bakes one keyframe per output sample for the camera rig. The VMD camera timeline is native 30 fps;
            /// when <paramref name="outputFrameRate"/> is a higher integer multiple (60 or 120) the timeline is
            /// sub-sampled, producing <c>outputFrameRate / 30</c> samples per VMD frame. The camera center position and
            /// orientation, the camera child's local Z distance, and the field of view are interpolated between the
            /// surrounding VMD keys using each destination key's per-channel Bezier curves, then converted into Unity
            /// space. Sub-frame samples that fall inside a segment whose bracketing keys are exactly one VMD frame apart
            /// snap to the integer frame, preserving MMD hard cuts.
            /// </summary>
            /// <param name="frames">VMD camera keys sorted ascending by frame number.</param>
            /// <param name="frameCount">Number of output samples to produce (last VMD frame * upsample + 1).</param>
            /// <param name="scale">MMD-to-Unity unit scale applied to positions and distance.</param>
            /// <param name="outputFrameRate">Output clip frame rate; an integer multiple of the 30 fps VMD timeline.</param>
            /// <param name="buffers">Destination per-channel keyframe buffers.</param>
            [BurstCompile]
            internal static void BakeCameraFrames(
                in NativeArray<VMDCameraFrame> frames,
                int frameCount,
                float scale,
                float outputFrameRate,
                ref CameraCurveBuffers buffers)
            {
                int keyCount = frames.Length;
                int upsample = (int)(outputFrameRate / MMDConstants.k_VMDNativeFrameRate);
                int previousKeyIndex = 0;
                for (int i = 0; i < frameCount; ++i)
                {
                    uint flooredFrame = (uint)(i / upsample);
                    float framePos = (float)i / upsample;
                    while (previousKeyIndex + 1 < keyCount && frames[previousKeyIndex + 1].frame <= flooredFrame)
                    {
                        ++previousKeyIndex;
                    }

                    VMDCameraFrame previous = frames[previousKeyIndex];
                    float3 targetPosition;
                    float3 rotationEuler;
                    float distance;
                    float fieldOfView;
                    if (previousKeyIndex + 1 >= keyCount)
                    {
                        targetPosition = previous.targetPosition;
                        rotationEuler = previous.rotation;
                        distance = previous.distance;
                        fieldOfView = previous.viewAngle;
                    }
                    else
                    {
                        VMDCameraFrame next = frames[previousKeyIndex + 1];
                        float range = next.frame - previous.frame;
                        // Hard cut: keys one VMD frame apart suppress sub-frame interpolation (snap to the integer frame), matching MMD's behaviour.
                        float samplePos = (next.frame - previous.frame == 1) ? flooredFrame : framePos;
                        float normalizedTime = range <= 0.0f ? 0.0f : (samplePos - previous.frame) / range;
                        VMDCameraInterpolation interpolation = next.interpolation;

                        float movementT = EvaluateBezier(interpolation.movement, normalizedTime);
                        float rotationT = EvaluateBezier(interpolation.rotation, normalizedTime);
                        float distanceT = EvaluateBezier(interpolation.distance, normalizedTime);
                        float fovT = EvaluateBezier(interpolation.viewAngle, normalizedTime);

                        targetPosition = math.lerp(previous.targetPosition, next.targetPosition, movementT);
                        rotationEuler = new float3(
                            previous.rotation.x + DeltaAngleRadians(previous.rotation.x, next.rotation.x) * rotationT,
                            previous.rotation.y + DeltaAngleRadians(previous.rotation.y, next.rotation.y) * rotationT,
                            previous.rotation.z + DeltaAngleRadians(previous.rotation.z, next.rotation.z) * rotationT);
                        distance = math.lerp(previous.distance, next.distance, distanceT);
                        fieldOfView = math.lerp(previous.viewAngle, next.viewAngle, fovT);
                    }

                    float3 centerPosition = new float3(-targetPosition.x, targetPosition.y, -targetPosition.z) * scale;
                    quaternion centerRotation = EulerToUnityCameraRotation(rotationEuler);
                    float cameraDistanceZ = -math.abs(distance) * scale;

                    float time = i / outputFrameRate;
                    buffers.positionX[i] = new Keyframe(time, centerPosition.x);
                    buffers.positionY[i] = new Keyframe(time, centerPosition.y);
                    buffers.positionZ[i] = new Keyframe(time, centerPosition.z);
                    buffers.rotationX[i] = new Keyframe(time, centerRotation.value.x);
                    buffers.rotationY[i] = new Keyframe(time, centerRotation.value.y);
                    buffers.rotationZ[i] = new Keyframe(time, centerRotation.value.z);
                    buffers.rotationW[i] = new Keyframe(time, centerRotation.value.w);
                    buffers.cameraDistanceZ[i] = new Keyframe(time, cameraDistanceZ);
                    buffers.fieldOfView[i] = new Keyframe(time, fieldOfView);
                }
            }

            /// <summary>
            /// Builds the Unity camera-center rotation from the raw VMD camera Euler angles (radians).
            /// The basis is then rebased into Unity space by negating the X and Z axes. The camera child, offset along
            /// its local -Z, looks back toward the center.
            /// </summary>
            /// <param name="euler">Raw VMD camera Euler angles (rawRx, rawRy, rawRz) in radians.</param>
            /// <returns>The camera-center orientation in Unity space.</returns>
            private static quaternion EulerToUnityCameraRotation(float3 euler)
            {
                float pitch = -euler.x;
                float yaw = euler.y + math.PI;
                float roll = euler.z;

                math.sincos(-pitch, out float sinX, out float cosX);
                math.sincos(-yaw, out float sinY, out float cosY);
                math.sincos(roll, out float sinZ, out float cosZ);

                float3 upBasis = CameraBasis(new float3(0.0f, 1.0f, 0.0f), sinX, cosX, sinY, cosY, sinZ, cosZ);
                float3 forwardBasis = CameraBasis(new float3(0.0f, 0.0f, 1.0f), sinX, cosX, sinY, cosY, sinZ, cosZ);

                // MMD view basis (world): up = UnitY * M, forward = -(UnitZ * M); rebase to Unity by negating X and Z.
                float3 up = new float3(-upBasis.x, upBasis.y, -upBasis.z);
                float3 forward = new float3(forwardBasis.x, -forwardBasis.y, forwardBasis.z);

                return quaternion.LookRotation(forward, up);
            }

            /// <summary>
            /// Transforms a basis vector through the DirectX row-vector camera matrix
            /// RotationZ(roll) * RotationX(-pitch) * RotationY(-yaw), applied left to right.
            /// </summary>
            private static float3 CameraBasis(float3 v, float sinX, float cosX, float sinY, float cosY, float sinZ, float cosZ)
            {
                v = new float3(v.x * cosZ - v.y * sinZ, v.x * sinZ + v.y * cosZ, v.z);
                v = new float3(v.x, v.y * cosX - v.z * sinX, v.y * sinX + v.z * cosX);
                v = new float3(v.x * cosY + v.z * sinY, v.y, -v.x * sinY + v.z * cosY);
                return v;
            }

            private static float DeltaAngleRadians(float current, float target)
            {
                float delta = (target - current) - 2.0f * math.PI * math.floor((target - current) / (2.0f * math.PI));
                if (delta > math.PI)
                {
                    delta -= 2.0f * math.PI;
                }

                return delta;
            }
        }

        private readonly struct BoneSample
        {
            public readonly uint frame;
            public readonly float3 position;
            public readonly quaternion rotation;
            public readonly VMDBoneInterpolation interpolation;
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool hasKey;
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool hasInterpolation;

            public BoneSample(float3 position, quaternion rotation)
                : this(0, position, rotation, true, new VMDBoneInterpolation())
            {
                hasInterpolation = false;
            }

            public BoneSample(uint frame, float3 position, quaternion rotation, bool hasInterpolation)
                : this(frame, position, rotation, true, new VMDBoneInterpolation())
            {
                this.hasInterpolation = hasInterpolation;
            }

            public BoneSample(float3 position, quaternion rotation, bool hasKey)
                : this(0, position, rotation, hasKey, new VMDBoneInterpolation())
            {
                hasInterpolation = false;
            }

            public BoneSample(float3 position, quaternion rotation, bool hasKey, VMDBoneInterpolation interpolation)
                : this(0, position, rotation, hasKey, interpolation)
            {
            }

            public BoneSample(uint frame, float3 position, quaternion rotation, bool hasKey, VMDBoneInterpolation interpolation)
            {
                this.frame = frame;
                this.hasKey = hasKey;
                hasInterpolation = true;
                this.position = position;
                this.rotation = rotation;
                this.interpolation = interpolation;
            }

            public BoneSample(uint frame, float3 position, quaternion rotation, bool hasKey, bool hasInterpolation, VMDBoneInterpolation interpolation)
            {
                this.frame = frame;
                this.hasKey = hasKey;
                this.hasInterpolation = hasInterpolation;
                this.position = position;
                this.rotation = rotation;
                this.interpolation = interpolation;
            }
        }

        private readonly struct ResolvedBoneFrame : IComparable<ResolvedBoneFrame>
        {
            public readonly int boneIndex;
            public readonly uint frame;
            public readonly int sourceOrder;
            public readonly BoneSample sample;

            public ResolvedBoneFrame(int boneIndex, uint frame, int sourceOrder, BoneSample sample)
            {
                this.boneIndex = boneIndex;
                this.frame = frame;
                this.sourceOrder = sourceOrder;
                this.sample = sample;
            }

            public int CompareTo(ResolvedBoneFrame other)
            {
                int frameComparison = frame.CompareTo(other.frame);
                if (frameComparison != 0)
                {
                    return frameComparison;
                }

                return sourceOrder.CompareTo(other.sourceOrder);
            }
        }

        private readonly struct ResolvedIKToggleFrame : IComparable<ResolvedIKToggleFrame>
        {
            public readonly int boneIndex;
            public readonly uint frame;
            public readonly int sourceOrder;
            [MarshalAs(UnmanagedType.U1)]
            public readonly bool enabled;

            public ResolvedIKToggleFrame(int boneIndex, uint frame, int sourceOrder, bool enabled)
            {
                this.boneIndex = boneIndex;
                this.frame = frame;
                this.sourceOrder = sourceOrder;
                this.enabled = enabled;
            }

            public int CompareTo(ResolvedIKToggleFrame other)
            {
                int frameComparison = frame.CompareTo(other.frame);
                if (frameComparison != 0)
                {
                    return frameComparison;
                }

                return sourceOrder.CompareTo(other.sourceOrder);
            }
        }

    }
}
