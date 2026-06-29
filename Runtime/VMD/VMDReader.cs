using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Parses MikuMikuDance VMD (Vocaloid Motion Data) binary data into a <see cref="VMDAnimation"/>.
    /// Recognizes the V1 and V2 signatures, decodes names with CP932, and reads bone and morph frames plus the
    /// optional camera, light, self-shadow, and show/IK sections when present.
    /// </summary>
    public static class VMDReader
    {
        private const string k_SignatureV1 = "Vocaloid Motion Data file";
        private const string k_SignatureV2 = "Vocaloid Motion Data 0002";
        private const int k_SignatureByteCount = 30;
        private const int k_V1ModelNameByteCount = 10;
        private const int k_V2ModelNameByteCount = 20;
        private const int k_BoneNameByteCount = 15;
        private const int k_MorphNameByteCount = 15;
        private const int k_IKNameByteCount = 20;
        private const int k_BoneInterpolationByteCount = 64;
        private const int k_CameraInterpolationByteCount = 24;

        /// <summary>
        /// Parses VMD animation data from an in-memory byte buffer.
        /// </summary>
        /// <param name="bytes">The raw VMD file bytes.</param>
        /// <returns>The parsed <see cref="VMDAnimation"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is null or empty.</exception>
        /// <exception cref="InvalidDataException">Thrown when the VMD signature is invalid.</exception>
        public static VMDAnimation Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("VMD bytes are required.", nameof(bytes));
            }

            using MemoryStream stream = new MemoryStream(bytes, false);
            return Read(stream);
        }

        /// <summary>
        /// Parses VMD animation data from a stream. Reads the signature/header, bone and morph frames, then the
        /// optional camera, light, self-shadow, and show/IK sections while data remains.
        /// </summary>
        /// <param name="stream">The stream positioned at the start of the VMD data.</param>
        /// <returns>The parsed <see cref="VMDAnimation"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        /// <exception cref="InvalidDataException">Thrown when the VMD signature is invalid.</exception>
        public static VMDAnimation Read(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            VMDAnimation animation = ScriptableObject.CreateInstance<VMDAnimation>();
            using MMDBinaryReader reader = new MMDBinaryReader(stream, true);

            FixedString32Bytes signature = reader.ReadAscii32Bytes(k_SignatureByteCount);
            if (signature == k_SignatureV1)
            {
                animation.version = VMDAnimation.Version.V1;
                animation.modelName = reader.ReadCP932(k_V1ModelNameByteCount);
            }
            else if (signature == k_SignatureV2)
            {
                animation.version = VMDAnimation.Version.V2;
                animation.modelName = reader.ReadCP932(k_V2ModelNameByteCount);
            }
            else
            {
                throw new InvalidDataException($"Invalid VMD signature '{signature}'.");
            }

            animation.boneFrames = ReadBoneFrames(reader);
            animation.morphFrames = ReadMorphFrames(reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.cameraFrames = ReadCameraFrames(reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.lightFrames = ReadLightFrames(reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.selfShadowFrames = ReadSelfShadowFrames(reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.showIKFrames = ReadShowIKFrames(reader);

            return animation;
        }

        /// <summary>
        /// Parses VMD animation data from an in-memory byte buffer asynchronously.
        /// </summary>
        /// <param name="frameBudget">The frame budget used to yield control back to the Unity main thread during long-running operations.</param>
        /// <param name="bytes">The raw VMD file bytes.</param>
        /// <returns>The parsed <see cref="VMDAnimation"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is null or empty.</exception>
        /// <exception cref="InvalidDataException">Thrown when the VMD signature is invalid.</exception>
        public static async Awaitable<VMDAnimation> ReadAsync(UMTFrameBudget frameBudget, Task<byte[]> bytesTask)
        {
            byte[] bytes = await bytesTask;
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("VMD bytes are required.", nameof(bytes));
            }

            using MemoryStream stream = new MemoryStream(bytes, false);
            return await ReadAsync(frameBudget, stream);
        }

        /// <summary>
        /// Parses VMD animation data from a stream asynchronously. Reads the signature/header, bone and morph frames, then the
        /// optional camera, light, self-shadow, and show/IK sections while data remains.
        /// </summary>
        /// <param name="frameBudget">The frame budget used to yield control back to the Unity main thread during long-running operations.</param>
        /// <param name="stream">The stream positioned at the start of the VMD data.</param>
        /// <returns>The parsed <see cref="VMDAnimation"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        /// <exception cref="InvalidDataException">Thrown when the VMD signature is invalid.</exception>
        public static async Awaitable<VMDAnimation> ReadAsync(UMTFrameBudget frameBudget, Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            VMDAnimation animation = ScriptableObject.CreateInstance<VMDAnimation>();
            using MMDBinaryReader reader = new MMDBinaryReader(stream, true);

            FixedString32Bytes signature = reader.ReadAscii32Bytes(k_SignatureByteCount);
            if (signature == k_SignatureV1)
            {
                animation.version = VMDAnimation.Version.V1;
                animation.modelName = reader.ReadCP932(k_V1ModelNameByteCount);
            }
            else if (signature == k_SignatureV2)
            {
                animation.version = VMDAnimation.Version.V2;
                animation.modelName = reader.ReadCP932(k_V2ModelNameByteCount);
            }
            else
            {
                throw new InvalidDataException($"Invalid VMD signature '{signature}'.");
            }
            await frameBudget.YieldIfNeeded();

            animation.boneFrames = await ReadBoneFramesAsync(frameBudget, reader);
            animation.morphFrames = await ReadMorphFramesAsync(frameBudget, reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.cameraFrames = await ReadCameraFramesAsync(frameBudget, reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.lightFrames = await ReadLightFramesAsync(frameBudget, reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.selfShadowFrames = await ReadSelfShadowFramesAsync(frameBudget, reader);
            if (!reader.HasRemaining(sizeof(uint)))
            {
                return animation;
            }

            animation.showIKFrames = await ReadShowIKFramesAsync(frameBudget, reader);

            return animation;
        }

        private static VMDBoneFrame[] ReadBoneFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDBoneFrame[] frames = new VMDBoneFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadBoneFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDBoneFrame[]> ReadBoneFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDBoneFrame[] frames = new VMDBoneFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadBoneFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDBoneFrame ReadBoneFrame(MMDBinaryReader reader)
        {
            return new VMDBoneFrame
            {
                boneName = reader.ReadCP932(k_BoneNameByteCount),
                frame = reader.ReadU32(),
                position = reader.ReadVec3(),
                rotation = reader.ReadQuaternion(),
                interpolation = ReadBoneInterpolation(reader),
            };
        }

        private static VMDMorphFrame[] ReadMorphFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDMorphFrame[] frames = new VMDMorphFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadMorphFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDMorphFrame[]> ReadMorphFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDMorphFrame[] frames = new VMDMorphFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadMorphFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDMorphFrame ReadMorphFrame(MMDBinaryReader reader)
        {
            return new VMDMorphFrame
            {
                morphName = reader.ReadCP932(k_MorphNameByteCount),
                frame = reader.ReadU32(),
                weight = reader.ReadF32(),
            };
        }

        private static VMDCameraFrame[] ReadCameraFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDCameraFrame[] frames = new VMDCameraFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadCameraFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDCameraFrame[]> ReadCameraFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDCameraFrame[] frames = new VMDCameraFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadCameraFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDCameraFrame ReadCameraFrame(MMDBinaryReader reader)
        {
            return new VMDCameraFrame
            {
                frame = reader.ReadU32(),
                distance = reader.ReadF32(),
                targetPosition = reader.ReadVec3(),
                rotation = reader.ReadVec3(),
                interpolation = ReadCameraInterpolation(reader),
                viewAngle = reader.ReadU32(),
                perspectiveOff = reader.ReadU8() != 0,
            };
        }

        private static VMDLightFrame[] ReadLightFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDLightFrame[] frames = new VMDLightFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadLightFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDLightFrame[]> ReadLightFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDLightFrame[] frames = new VMDLightFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadLightFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDLightFrame ReadLightFrame(MMDBinaryReader reader)
        {
            return new VMDLightFrame
            {
                frame = reader.ReadU32(),
                color = new Color(reader.ReadF32(), reader.ReadF32(), reader.ReadF32(), 1.0f),
                position = reader.ReadVec3(),
            };
        }

        private static VMDSelfShadowFrame[] ReadSelfShadowFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDSelfShadowFrame[] frames = new VMDSelfShadowFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadSelfShadowFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDSelfShadowFrame[]> ReadSelfShadowFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDSelfShadowFrame[] frames = new VMDSelfShadowFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadSelfShadowFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDSelfShadowFrame ReadSelfShadowFrame(MMDBinaryReader reader)
        {
            return new VMDSelfShadowFrame
            {
                frame = reader.ReadU32(),
                mode = reader.ReadU8(),
                distance = reader.ReadF32(),
            };
        }

        private static VMDShowIKFrame[] ReadShowIKFrames(MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDShowIKFrame[] frames = new VMDShowIKFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadShowIKFrame(reader);
            }
            return frames;
        }

        private static async Awaitable<VMDShowIKFrame[]> ReadShowIKFramesAsync(UMTFrameBudget frameBudget, MMDBinaryReader reader)
        {
            uint count = reader.ReadU32();
            VMDShowIKFrame[] frames = new VMDShowIKFrame[count];
            for (uint i = 0; i < count; ++i)
            {
                frames[i] = ReadShowIKFrame(reader);
                await frameBudget.YieldIfNeeded();
            }
            return frames;
        }

        private static VMDShowIKFrame ReadShowIKFrame(MMDBinaryReader reader)
        {
            uint frame = reader.ReadU32();
            bool show = reader.ReadU8() != 0;
            uint ikCount = reader.ReadU32();
            VMDIKToggleFrame[] ikToggles = new VMDIKToggleFrame[ikCount];
            for (uint ikIndex = 0; ikIndex < ikCount; ++ikIndex)
            {
                ikToggles[ikIndex] = new VMDIKToggleFrame
                {
                    boneName = reader.ReadCP932(k_IKNameByteCount),
                    frame = frame,
                    enabled = reader.ReadU8() != 0,
                };
            }

            return new VMDShowIKFrame
            {
                frame = frame,
                show = show,
                ikToggles = ikToggles,
            };
        }

        private static VMDBoneInterpolation ReadBoneInterpolation(MMDBinaryReader reader)
        {
            byte[] rawInterpolation = reader.ReadBytes(k_BoneInterpolationByteCount);
            VMDBoneInterpolation interpolation = new VMDBoneInterpolation()
            {
                positionX = ReadBoneInterpolationChannel(rawInterpolation, 0),
                positionY = ReadBoneInterpolationChannel(rawInterpolation, 1),
                positionZ = ReadBoneInterpolationChannel(rawInterpolation, 2),
                rotation = ReadBoneInterpolationChannel(rawInterpolation, 3),
            };
            return interpolation;
        }

        private static VMDBezierInterpolation ReadBoneInterpolationChannel(byte[] rawInterpolation, int channel)
        {
            int offset = GetBoneInterpolationChannelOffset(channel);
            return ReadInterpolation(rawInterpolation, offset, offset + 4, offset + 8, offset + 12);
        }

        private static VMDCameraInterpolation ReadCameraInterpolation(MMDBinaryReader reader)
        {
            byte[] rawInterpolation = reader.ReadBytes(k_CameraInterpolationByteCount);
            VMDCameraInterpolation interpolation = new VMDCameraInterpolation()
            {
                movement = ReadCameraMoveInterpolation(rawInterpolation),
                rotation = ReadInterpolation(rawInterpolation, 12, 14, 13, 15),
                distance = ReadInterpolation(rawInterpolation, 16, 18, 17, 19),
                viewAngle = ReadInterpolation(rawInterpolation, 20, 22, 21, 23),
            };
            return interpolation;
        }

        private static VMDBezierInterpolation ReadCameraMoveInterpolation(byte[] rawInterpolation)
        {
            VMDBezierInterpolation interpolation = ReadInterpolation(rawInterpolation, 0, 2, 1, 3);
            int startDistance = GetDefaultControlPointSquaredDistance(rawInterpolation[0], rawInterpolation[2], 20);
            int endDistance = GetDefaultControlPointSquaredDistance(rawInterpolation[1], rawInterpolation[3], 107);

            UpdateCameraMoveInterpolation(rawInterpolation, 4, 6, 5, 7, ref interpolation, ref startDistance, ref endDistance);
            UpdateCameraMoveInterpolation(rawInterpolation, 8, 10, 9, 11, ref interpolation, ref startDistance, ref endDistance);
            return interpolation;
        }

        private static void UpdateCameraMoveInterpolation(
            byte[] rawInterpolation,
            int startXIndex,
            int startYIndex,
            int endXIndex,
            int endYIndex,
            ref VMDBezierInterpolation interpolation,
            ref int startDistance,
            ref int endDistance)
        {
            int candidateStartDistance = GetDefaultControlPointSquaredDistance(rawInterpolation[startXIndex], rawInterpolation[startYIndex], 20);
            if (candidateStartDistance > startDistance)
            {
                interpolation.x1 = rawInterpolation[startXIndex] / 127.0f;
                interpolation.y1 = rawInterpolation[startYIndex] / 127.0f;
                startDistance = candidateStartDistance;
            }

            int candidateEndDistance = GetDefaultControlPointSquaredDistance(rawInterpolation[endXIndex], rawInterpolation[endYIndex], 107);
            if (candidateEndDistance > endDistance)
            {
                interpolation.x2 = rawInterpolation[endXIndex] / 127.0f;
                interpolation.y2 = rawInterpolation[endYIndex] / 127.0f;
                endDistance = candidateEndDistance;
            }
        }

        private static int GetDefaultControlPointSquaredDistance(byte x, byte y, int defaultValue)
        {
            int deltaX = x - defaultValue;
            int deltaY = y - defaultValue;
            return deltaX * deltaX + deltaY * deltaY;
        }

        private static VMDBezierInterpolation ReadInterpolation(byte[] rawInterpolation, int x1Index, int y1Index, int x2Index, int y2Index)
        {
            return new VMDBezierInterpolation()
            {
                x1 = rawInterpolation[x1Index] / 127.0f,
                y1 = rawInterpolation[y1Index] / 127.0f,
                x2 = rawInterpolation[x2Index] / 127.0f,
                y2 = rawInterpolation[y2Index] / 127.0f,
            };
        }

        private static int GetBoneInterpolationChannelOffset(int channel)
        {
            if (channel < 2)
            {
                return channel;
            }

            return channel + 15;
        }
    }
}
