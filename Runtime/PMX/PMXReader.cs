using System;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Parses a PMX binary stream into a <see cref="PMXModel"/>. Reads the header, model info, vertices,
    /// faces, textures, materials, bones, morphs, display frames, rigid bodies, and joints, scaling
    /// position-like values from MMD units to Unity units during parsing.
    /// </summary>
    public static class PMXReader
    {
        /// <summary>Reads and validates a complete PMX model from the given stream.</summary>
        /// <param name="stream">Stream positioned at the start of the PMX data.</param>
        /// <param name="strictVersion">When true, requires the file version to match the supported PMX version.</param>
        /// <returns>The parsed PMX model as a new <see cref="ScriptableObject"/>.</returns>
        /// <exception cref="InvalidDataException">Thrown when the stream contains invalid or unsupported PMX data.</exception>
        public static PMXModel Read(Stream stream, bool strictVersion = true)
        {
            using MMDBinaryReader reader = new MMDBinaryReader(stream, true);
            PMXModel model = ScriptableObject.CreateInstance<PMXModel>();

            ReadHeader(reader, ref model.header, strictVersion);
            ReadModelInfo(reader, model);
            ReadVertices(reader, model);
            ReadFaces(reader, model);
            ReadTextures(reader, model);
            ReadMaterials(reader, model);
            ReadBones(reader, model);
            ReadMorphs(reader, model);
            ReadDisplayFrames(reader, model);
            ReadRigidBodies(reader, model);
            ReadJoints(reader, model);
            Validate(model);

            return model;
        }

        private static void ReadHeader(MMDBinaryReader reader, ref PMXHeader header, bool strictVersion)
        {
            header.signature = reader.ReadAscii32Bytes(MMDConstants.k_PMXHeaderSignatureByteCount);
            header.version = reader.ReadF32();
            header.dataCount = reader.ReadU8();
            header.textEncoding = (PMXHeader.TextEncoding)reader.ReadU8();
            header.additionalUVCount = reader.ReadU8();
            header.vertexIndexSize = reader.ReadU8();
            header.textureIndexSize = reader.ReadU8();
            header.materialIndexSize = reader.ReadU8();
            header.boneIndexSize = reader.ReadU8();
            header.morphIndexSize = reader.ReadU8();
            header.rigidBodyIndexSize = reader.ReadU8();

            if (header.signature != MMDConstants.k_PMXHeaderSignature)
            {
                throw new InvalidDataException($"Invalid PMX signature '{header.signature}'.");
            }

            if (strictVersion && !Mathf.Approximately(header.version, MMDConstants.k_SupportedPMXVersion))
            {
                throw new InvalidDataException($"Expected PMX {MMDConstants.k_SupportedPMXVersion:0.###}. Found {header.version:0.###}.");
            }

            if (header.dataCount < MMDConstants.k_PMXHeaderDataCount)
            {
                throw new InvalidDataException($"Invalid PMX data count {header.dataCount}. Expected at least {MMDConstants.k_PMXHeaderDataCount}.");
            }

            if (header.textEncoding != PMXHeader.TextEncoding.UTF16LE && header.textEncoding != PMXHeader.TextEncoding.UTF8)
            {
                throw new InvalidDataException($"Unsupported PMX text encoding {(byte)header.textEncoding}.");
            }

            if (header.additionalUVCount > MMDConstants.k_MaxPMXAdditionalUVCount)
            {
                throw new InvalidDataException($"Invalid PMX additional uv count {header.additionalUVCount}.");
            }

            ValidateIndexSize(header.vertexIndexSize);
            ValidateIndexSize(header.textureIndexSize);
            ValidateIndexSize(header.materialIndexSize);
            ValidateIndexSize(header.boneIndexSize);
            ValidateIndexSize(header.morphIndexSize);
            ValidateIndexSize(header.rigidBodyIndexSize);
        }

        private static void ReadModelInfo(MMDBinaryReader reader, PMXModel model)
        {
            model.modelInfo.name = reader.ReadPMXText(model.header.textEncoding);
            model.modelInfo.nameEN = reader.ReadPMXText(model.header.textEncoding);
            model.modelInfo.comment = reader.ReadPMXText(model.header.textEncoding);
            model.modelInfo.commentEN = reader.ReadPMXText(model.header.textEncoding);
        }

        private static void ReadVertices(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.vertices = new PMXVertex[count];
            for (int i = 0; i < count; ++i)
            {
                PMXVertex vertex = new PMXVertex
                {
                    position = ReadPosition(reader),
                    normal = ReadRotatedVector3(reader),
                    uv = reader.ReadVec2(),
                };

                int additionalUVCount = model.header.additionalUVCount;
                if (additionalUVCount > 0)
                {
                    vertex.additionalUV1 = additionalUVCount >= 1 ? reader.ReadVec4() : float4.zero;
                    vertex.additionalUV2 = additionalUVCount >= 2 ? reader.ReadVec4() : float4.zero;
                    vertex.additionalUV3 = additionalUVCount >= 3 ? reader.ReadVec4() : float4.zero;
                    vertex.additionalUV4 = additionalUVCount >= 4 ? reader.ReadVec4() : float4.zero;
                }

                vertex.weight = ReadWeight(reader, model.header);
                vertex.edgeScale = reader.ReadF32();
                model.vertices[i] = vertex;
            }
        }

        private static PMXWeight ReadWeight(MMDBinaryReader reader, PMXHeader header)
        {
            byte typeValue = reader.ReadU8();
            PMXWeight weight = new PMXWeight 
            { 
                type = (PMXWeight.Type)typeValue,
                boneIndex0 = -1,
                boneIndex1 = -1,
                boneIndex2 = -1,
                boneIndex3 = -1,
                weight0 = 0.0f,
                weight1 = 0.0f,
                weight2 = 0.0f,
                weight3 = 0.0f,
            };

            switch (weight.type)
            {
                case PMXWeight.Type.BDEF1:
                    weight.boneIndex0 = reader.ReadIndex(header.boneIndexSize);
                    weight.weight0 = 1.0f;
                    break;
                case PMXWeight.Type.BDEF2:
                    {
                        weight.boneIndex0 = reader.ReadIndex(header.boneIndexSize);
                        weight.boneIndex1 = reader.ReadIndex(header.boneIndexSize);
                        weight.weight0 = reader.ReadF32();
                        weight.weight1 = 1.0f - weight.weight0;
                        break;
                    }
                case PMXWeight.Type.BDEF4:
                    weight.boneIndex0 = reader.ReadIndex(header.boneIndexSize);
                    weight.boneIndex1 = reader.ReadIndex(header.boneIndexSize);
                    weight.boneIndex2 = reader.ReadIndex(header.boneIndexSize);
                    weight.boneIndex3 = reader.ReadIndex(header.boneIndexSize);
                    weight.weight0 = reader.ReadF32();
                    weight.weight1 = reader.ReadF32();
                    weight.weight2 = reader.ReadF32();
                    weight.weight3 = reader.ReadF32();
                    break;
                case PMXWeight.Type.SDEF:
                    {
                        weight.boneIndex0 = reader.ReadIndex(header.boneIndexSize);
                        weight.boneIndex1 = reader.ReadIndex(header.boneIndexSize);
                        weight.weight0 = reader.ReadF32();
                        weight.weight1 = 1.0f - weight.weight0;
                        weight.sdefC = ReadScaledPosition(reader);
                        weight.sdefR0 = ReadScaledPosition(reader);
                        weight.sdefR1 = ReadScaledPosition(reader);
                        break;
                    }
                default:
                    throw new InvalidDataException($"Unsupported PMX weight type {typeValue}.");
            }

            return weight;
        }

        private static void ReadFaces(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            if (count % 3 != 0)
            {
                throw new InvalidDataException($"PMX face index count {count} is not divisible by 3.");
            }

            model.indices = new uint[count];
            for (int i = 0; i < count; ++i)
            {
                model.indices[i] = reader.ReadVertexIndex(model.header.vertexIndexSize);
            }
        }

        private static void ReadTextures(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.texturePaths = new FixedString128Bytes[count];
            for (int i = 0; i < count; ++i)
            {
                model.texturePaths[i] = reader.ReadPMXText(model.header.textEncoding);
            }
        }

        private static void ReadMaterials(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.materials = new PMXMaterial[count];
            for (int i = 0; i < count; ++i)
            {
                PMXMaterial material = new PMXMaterial
                {
                    originalName = reader.ReadPMXText(model.header.textEncoding),
                    originalNameEN = reader.ReadPMXText(model.header.textEncoding),
                    diffuse = reader.ReadColor4(),
                    specular = reader.ReadVec3(),
                    specularStrength = reader.ReadF32(),
                    ambient = reader.ReadVec3(),
                    drawingFlags = (PMXMaterial.DrawingFlags)reader.ReadU8(),
                    edgeColor = reader.ReadColor4(),
                    edgeSize = reader.ReadF32(),
                    textureIndex = reader.ReadIndex(model.header.textureIndexSize),
                    sphereTextureIndex = reader.ReadIndex(model.header.textureIndexSize),
                    sphereTextureMode = (PMXMaterial.SphereTextureMode)reader.ReadU8(),
                    toonTextureReferenceFlag = reader.ReadU8() != 0,
                };

                if (!material.toonTextureReferenceFlag)
                {
                    material.toonTextureIndex = reader.ReadIndex(model.header.textureIndexSize);
                    material.builtinToonIndex = -1;
                }
                else
                {
                    material.toonTextureIndex = -1;
                    material.builtinToonIndex = reader.ReadU8();
                }

                material.memo = reader.ReadPMXText(model.header.textEncoding);
                material.faceIndexCount = reader.ReadI32();
                if (material.faceIndexCount < 0 || material.faceIndexCount % 3 != 0)
                {
                    throw new InvalidDataException($"Invalid face index count {material.faceIndexCount} on material {i}.");
                }

                model.materials[i] = material;
            }
        }

        private static void ReadBones(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.bones = new PMXBone[count];
            for (int i = 0; i < count; ++i)
            {
                PMXBone bone = new PMXBone
                {
                    originalName = reader.ReadPMXText(model.header.textEncoding),
                    originalNameEN = reader.ReadPMXText(model.header.textEncoding),
                    position = ReadPosition(reader),
                    parentBoneIndex = reader.ReadIndex(model.header.boneIndexSize),
                    transformLevel = reader.ReadI32(),
                    flags = (PMXBone.Flags)reader.ReadU16(),
                    connectionBoneIndex = -1,
                    constraintTargetIndex = -1,
                };

                if ((bone.flags & PMXBone.Flags.HasConnection) != 0)
                {
                    bone.connectionBoneIndex = reader.ReadIndex(model.header.boneIndexSize);
                }
                else
                {
                    bone.positionOffset = ReadScaledPosition(reader);
                }

                if ((bone.flags & (PMXBone.Flags.RotationConstraint | PMXBone.Flags.TranslationConstraint)) != 0)
                {
                    bone.constraintTargetIndex = reader.ReadIndex(model.header.boneIndexSize);
                    bone.constraintInfluence  = reader.ReadF32();
                }

                if ((bone.flags & PMXBone.Flags.FixAxis) != 0)
                {
                    bone.axis = ReadRotatedVector3(reader);
                }

                if ((bone.flags & PMXBone.Flags.LocalAxis) != 0)
                {
                    bone.localXAxis = ReadRotatedVector3(reader);
                    bone.localZAxis = ReadRotatedVector3(reader);
                }

                if ((bone.flags & PMXBone.Flags.ExternalParent) != 0)
                {
                    bone.externalKey = reader.ReadI32();
                }

                if ((bone.flags & PMXBone.Flags.IK) != 0)
                {
                    bone.ik = new PMXIK
                    {
                        targetBoneIndex = reader.ReadIndex(model.header.boneIndexSize),
                        iterations = reader.ReadI32(),
                        angleLimit = reader.ReadF32(),
                    };
                    int linkCount = ReadNonNegativeCount(reader);
                    bone.ik.links = new PMXIKLink[linkCount];
                    for (int linkIndex = 0; linkIndex < linkCount; ++linkIndex)
                    {
                        PMXIKLink link = new PMXIKLink
                        {
                            boneIndex = reader.ReadIndex(model.header.boneIndexSize),
                            hasAngleLimit = reader.ReadU8() != 0,
                        };
                        if (link.hasAngleLimit)
                        {
                            ReadLimitPair(reader, out link.lowerLimit, out link.upperLimit, false);
                        }
                        bone.ik.links[linkIndex] = link;
                    }
                }

                model.bones[i] = bone;
            }
        }

        private static void ReadMorphs(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.morphs = new PMXMorph[count];
            for (int i = 0; i < count; ++i)
            {
                PMXMorph morph = new PMXMorph
                {
                    originalName = reader.ReadPMXText(model.header.textEncoding),
                    originalNameEN = reader.ReadPMXText(model.header.textEncoding),
                    panel = reader.ReadU8(),
                    type = (PMXMorph.Type)reader.ReadU8(),
                };
                int offsetCount = ReadNonNegativeCount(reader);
                morph.offsets = new PMXMorphOffset[offsetCount];
                for (int offsetIndex = 0; offsetIndex < offsetCount; ++offsetIndex)
                {
                    morph.offsets[offsetIndex] = ReadMorphOffset(reader, model.header, morph.type);
                }
                model.morphs[i] = morph;
            }
        }

        private static PMXMorphOffset ReadMorphOffset(MMDBinaryReader reader, PMXHeader header, PMXMorph.Type type)
        {
            switch (type)
            {
                case PMXMorph.Type.Group:
                case PMXMorph.Type.Flip:
                    return new PMXGroupMorphData
                    {
                        morphIndex = reader.ReadIndex(header.morphIndexSize),
                        rate = reader.ReadF32(),
                    };
                case PMXMorph.Type.Vertex:
                    return new PMXVertexMorphData
                    {
                        vertexIndex = reader.ReadVertexIndex(header.vertexIndexSize),
                        positionOffset = ReadScaledPosition(reader),
                    };
                case PMXMorph.Type.Bone:
                    return new PMXBoneMorphData
                    {
                        boneIndex = reader.ReadIndex(header.boneIndexSize),
                        translation = ReadScaledPosition(reader),
                        rotation = ReadRotationQuaternion(reader),
                    };
                case PMXMorph.Type.UV:
                case PMXMorph.Type.AdditionalUV1:
                case PMXMorph.Type.AdditionalUV2:
                case PMXMorph.Type.AdditionalUV3:
                case PMXMorph.Type.AdditionalUV4:
                    return new PMXUVMorphData
                    {
                        vertexIndex = reader.ReadVertexIndex(header.vertexIndexSize),
                        uvOffset = reader.ReadVec4(),
                    };
                case PMXMorph.Type.Material:
                    return new PMXMaterialMorphData
                    {
                        materialIndex = reader.ReadIndex(header.materialIndexSize),
                        offsetMethod = (PMXMaterialMorphData.Method)reader.ReadU8(),
                        diffuse = reader.ReadColor4(),
                        specular = reader.ReadVec3(),
                        specularStrength = reader.ReadF32(),
                        ambient = reader.ReadVec3(),
                        edgeColor = reader.ReadColor4(),
                        edgeSize = reader.ReadF32(),
                        textureTint = reader.ReadVec4(),
                        sphereTint = reader.ReadVec4(),
                        toonTint = reader.ReadVec4(),
                    };
                case PMXMorph.Type.Impulse:
                    return new PMXImpulseMorphData
                    {
                        rigidBodyIndex = reader.ReadIndex(header.rigidBodyIndexSize),
                        isLocal = reader.ReadU8() != 0,
                        velocity = ReadRotatedVector3(reader),
                        torque = ReadRotatedVector3(reader),
                    };
                default:
                    throw new InvalidDataException($"Unsupported PMX morph type {(byte)type}.");
            }
        }

        private static void ReadDisplayFrames(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.displayFrames = new PMXDisplayFrame[count];
            for (int i = 0; i < count; ++i)
            {
                PMXDisplayFrame frame = new PMXDisplayFrame
                {
                    originalName = reader.ReadPMXText(model.header.textEncoding),
                    originalNameEN = reader.ReadPMXText(model.header.textEncoding),
                    isSpecialFrame = reader.ReadU8() != 0,
                };
                int elementCount = ReadNonNegativeCount(reader);
                frame.elements = new PMXDisplayFrameElement[elementCount];
                for (int elementIndex = 0; elementIndex < elementCount; ++elementIndex)
                {
                    byte targetType = reader.ReadU8();
                    frame.elements[elementIndex] = new PMXDisplayFrameElement
                    {
                        targetType = (PMXDisplayFrameElement.Type)targetType,
                        targetIndex = targetType == 0
                            ? reader.ReadIndex(model.header.boneIndexSize)
                            : reader.ReadIndex(model.header.morphIndexSize),
                    };
                }
                model.displayFrames[i] = frame;
            }
        }

        private static void ReadRigidBodies(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.rigidBodies = new PMXRigidBody[count];
            for (int i = 0; i < count; ++i)
            {
                model.rigidBodies[i] = new PMXRigidBody
                {
                    originalName = reader.ReadPMXText(model.header.textEncoding),
                    originalNameEN = reader.ReadPMXText(model.header.textEncoding),
                    relatedBoneIndex = reader.ReadIndex(model.header.boneIndexSize),
                    groupIndex = reader.ReadU8(),
                    collisionGroupMask = reader.ReadI16(),
                    shape = (PMXRigidBody.Shape)reader.ReadU8(),
                    size = ReadSize(reader),
                    position = ReadPosition(reader),
                    rotation = ReadRotatedVector3(reader),
                    mass = reader.ReadF32(),
                    linearDamping = reader.ReadF32(),
                    angularDamping = reader.ReadF32(),
                    restitution = reader.ReadF32(),
                    friction = reader.ReadF32(),
                    mode = (PMXRigidBody.Mode)reader.ReadU8(),
                };
            }
        }

        private static void ReadJoints(MMDBinaryReader reader, PMXModel model)
        {
            int count = ReadNonNegativeCount(reader);
            model.joints = new PMXJoint[count];
            for (int i = 0; i < count; ++i)
            {
                FixedString128Bytes name = reader.ReadPMXText(model.header.textEncoding);
                FixedString128Bytes nameEN = reader.ReadPMXText(model.header.textEncoding);
                byte type = reader.ReadU8();
                int rigidBodyAIndex = reader.ReadIndex(model.header.rigidBodyIndexSize);
                int rigidBodyBIndex = reader.ReadIndex(model.header.rigidBodyIndexSize);
                float3 position = ReadPosition(reader);
                float3 rotation = ReadRotatedVector3(reader);
                ReadLimitPair(reader, out float3 translationLimitMin, out float3 translationLimitMax, true);
                ReadLimitPair(reader, out float3 rotationLimitMin, out float3 rotationLimitMax, false);
                model.joints[i] = new PMXJoint
                {
                    originalName = name,
                    originalNameEN = nameEN,
                    type = (PMXJoint.Type)type,
                    rigidBodyAIndex = rigidBodyAIndex,
                    rigidBodyBIndex = rigidBodyBIndex,
                    position = position,
                    rotation = rotation,
                    translationLimitMin = translationLimitMin,
                    translationLimitMax = translationLimitMax,
                    rotationLimitMin = rotationLimitMin,
                    rotationLimitMax = rotationLimitMax,
                    springTranslation = reader.ReadVec3(),
                    springRotation = reader.ReadVec3(),
                };
            }
        }

        private static float3 ReadPosition(MMDBinaryReader reader)
        {
            return ReadRotatedVector3(reader) * MMDConstants.k_MMDUnitToUnityUnit;
        }

        private static float3 ReadScaledPosition(MMDBinaryReader reader)
        {
            return ReadPosition(reader);
        }

        private static float3 ReadSize(MMDBinaryReader reader)
        {
            return reader.ReadVec3() * MMDConstants.k_MMDUnitToUnityUnit;
        }

        private static float3 ReadRotatedVector3(MMDBinaryReader reader)
        {
            return RotateY180(reader.ReadVec3());
        }

        private static quaternion ReadRotationQuaternion(MMDBinaryReader reader)
        {
            float4 rotation = reader.ReadVec4();
            return new quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w);
        }

        private static void ReadLimitPair(MMDBinaryReader reader, out float3 lowerLimit, out float3 upperLimit, bool scale)
        {
            float3 rawLowerLimit = reader.ReadVec3();
            float3 rawUpperLimit = reader.ReadVec3();
            float multiplier = scale ? MMDConstants.k_MMDUnitToUnityUnit : 1.0f;

            lowerLimit = new float3(
                -rawUpperLimit.x * multiplier,
                rawLowerLimit.y * multiplier,
                -rawUpperLimit.z * multiplier);
            upperLimit = new float3(
                -rawLowerLimit.x * multiplier,
                rawUpperLimit.y * multiplier,
                -rawLowerLimit.z * multiplier);
        }

        private static float3 RotateY180(float3 value)
        {
            return new float3(-value.x, value.y, -value.z);
        }

        private static void Validate(PMXModel model)
        {
            for (int i = 0; i < model.indices.Length; ++i)
            {
                if (model.indices[i] < 0 || model.indices[i] >= model.vertices.Length)
                {
                    throw new InvalidDataException($"Vertex index {model.indices[i]} at face index {i} is out of range.");
                }
            }

            int materialFaceTotal = 0;
            foreach (PMXMaterial material in model.materials)
            {
                materialFaceTotal += material.faceIndexCount;
            }
            if (materialFaceTotal != model.indices.Length)
            {
                throw new InvalidDataException($"Material face index total {materialFaceTotal} does not match face index count {model.indices.Length}.");
            }
        }

        private static int ReadNonNegativeCount(MMDBinaryReader reader)
        {
            int count = reader.ReadI32();
            if (count < 0)
            {
                throw new InvalidDataException($"Invalid negative count {count}.");
            }
            return count;
        }

        private static void ValidateIndexSize(byte size)
        {
            if (size != 1 && size != 2 && size != 4)
            {
                throw new InvalidDataException($"Invalid PMX index size {size}.");
            }
        }
    }
}
