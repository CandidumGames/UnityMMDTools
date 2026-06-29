using System;
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

        private static bool CanWritePositionCurves(PMXModel model, int boneIndex)
        {
            return boneIndex >= 0 &&
                boneIndex < model.bones.Length &&
                (model.bones[boneIndex].flags & PMXBone.Flags.Translatable) != 0;
        }

        private static bool CanWriteRotationCurves(PMXModel model, int boneIndex)
        {
            return boneIndex >= 0 &&
                boneIndex < model.bones.Length &&
                (model.bones[boneIndex].flags & PMXBone.Flags.Rotatable) != 0;
        }

        private static uint GetLastBoneFrame(VMDAnimation animation)
        {
            uint lastFrame = 0;
            foreach (VMDBoneFrame frame in animation.boneFrames)
            {
                lastFrame = Math.Max(lastFrame, frame.frame);
            }
            return lastFrame;
        }

        private static uint GetLastMorphFrame(VMDAnimation animation)
        {
            uint lastFrame = 0;
            foreach (VMDMorphFrame frame in animation.morphFrames)
            {
                lastFrame = Math.Max(lastFrame, frame.frame);
            }
            return lastFrame;
        }

        private static uint GetLastIKFrame(VMDAnimation animation)
        {
            uint lastFrame = 0;
            foreach (VMDShowIKFrame frame in animation.showIKFrames)
            {
                lastFrame = Math.Max(lastFrame, frame.frame);
            }
            return lastFrame;
        }

        private static uint GetLastBakeFrame(VMDAnimation animation)
        {
            return Math.Max(GetLastBoneFrame(animation), GetLastIKFrame(animation));
        }

        private static void ApplyLinearTangents(Keyframe[] keyframes)
        {
            for (int i = 1; i < keyframes.Length; ++i)
            {
                ApplyLinearTangents(keyframes, i - 1, i);
            }
        }

        private static void ApplyLinearTangents(Keyframe[] keyframes, int previousIndex, int nextIndex)
        {
            Keyframe previous = keyframes[previousIndex];
            Keyframe next = keyframes[nextIndex];
            float tangent = (next.value - previous.value) / (next.time - previous.time);
            previous.outTangent = tangent;
            next.inTangent = tangent;
            keyframes[previousIndex] = previous;
            keyframes[nextIndex] = next;
        }

        private static BoneSample ToBoneSample(VMDBoneFrame frame, float3 initialLocalPosition, quaternion initialLocalRotation)
        {
            float3 framePosition = new float3(frame.position.x, frame.position.y, frame.position.z);
            quaternion frameRotation = new quaternion(frame.rotation.value.x, frame.rotation.value.y, frame.rotation.value.z, frame.rotation.value.w);
            AnimationMath.ConvertPosition(in framePosition, out float3 convertedPosition);
            AnimationMath.ConvertRotation(in frameRotation, out quaternion convertedRotation);
            return new BoneSample(
                frame.frame,
                initialLocalPosition + convertedPosition,
                math.normalize(math.mul(convertedRotation, initialLocalRotation)),
                true,
                frame.interpolation);
        }

        private static Keyframe SteppedKeyframe(float time, float value)
        {
            return new Keyframe(time, value, float.PositiveInfinity, float.PositiveInfinity);
        }

        private struct IndexResolver : IDisposable
        {
            public NativeHashMap<FixedString32Bytes, int> boneNameToIndex;
            public NativeHashMap<FixedString32Bytes, int> morphNameToIndex;

            public IndexResolver(PMXModel model, Allocator allocator)
            {
                boneNameToIndex = new NativeHashMap<FixedString32Bytes, int>(model.bones.Length, allocator);
                morphNameToIndex = new NativeHashMap<FixedString32Bytes, int>(model.morphs.Length, allocator);

                for (int i = 0; i < model.bones.Length; ++i)
                {
                    PMXBone bone = model.bones[i];
                    FixedString32Bytes name = default;
                    name.CopyFromTruncated(bone.originalName);
                    if (!boneNameToIndex.ContainsKey(name))
                    {
                        boneNameToIndex.Add(name, i);
                    }
                }

                for (int i = 0; i < model.morphs.Length; ++i)
                {
                    PMXMorph morph = model.morphs[i];
                    FixedString32Bytes name = default;
                    name.CopyFromTruncated(morph.originalName);
                    if (!morphNameToIndex.ContainsKey(name))
                    {
                        morphNameToIndex.Add(name, i);
                    }
                }
            }

            public int ResolveBoneIndex(FixedString32Bytes name)
            {
                if (boneNameToIndex.TryGetValue(name, out int index))
                {
                    return index;
                }
                return -1;
            }

            public int ResolveMorphIndex(FixedString32Bytes name)
            {
                if (morphNameToIndex.TryGetValue(name, out int index))
                {
                    return index;
                }
                return -1;
            }

            public void Dispose()
            {
                if (boneNameToIndex.IsCreated)
                {
                    boneNameToIndex.Dispose();
                }

                if (morphNameToIndex.IsCreated)
                {
                    morphNameToIndex.Dispose();
                }
            }
        }

    }
}
