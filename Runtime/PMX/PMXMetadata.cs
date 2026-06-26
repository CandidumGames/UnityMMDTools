using UMT;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace UMT
{
    /// <summary>
    /// Summary metadata DTO built from a parsed <see cref="PMXModel"/> and serializable to JSON.
    /// Captures header, model info, counts, and per-element summaries for diagnostics and import logs.
    /// </summary>
    public sealed class PMXMetadata
    {
        /// <summary>PMX file header copied from the source model.</summary>
        public PMXHeader header { get; set; }
        /// <summary>Model information copied from the source model.</summary>
        public PMXModelInfo modelInfo { get; set; }
        /// <summary>Element counts (vertices, faces, materials, bones, and so on).</summary>
        public PMXMetadataCounts counts { get; set; }
        /// <summary>Texture file paths referenced by the model.</summary>
        public List<string> textures { get; set; }
        /// <summary>Per-material metadata summaries.</summary>
        public List<PMXMaterialMetadata> materials { get; set; }
        /// <summary>Per-bone metadata summaries.</summary>
        public List<PMXBoneMetadata> bones { get; set; }
        /// <summary>Per-morph metadata summaries.</summary>
        public List<PMXMorphMetadata> morphs { get; set; }
        /// <summary>Per-display-frame metadata summaries.</summary>
        public List<PMXDisplayFrameMetadata> displayFrames { get; set; }
        /// <summary>Per-rigid-body metadata summaries.</summary>
        public List<PMXRigidBodyMetadata> rigidBodies { get; set; }
        /// <summary>Per-joint metadata summaries.</summary>
        public List<PMXJointMetadata> joints { get; set; }

        /// <summary>Builds the metadata DTO from a parsed PMX model.</summary>
        /// <param name="model">Parsed PMX model to summarize.</param>
        public PMXMetadata(PMXModel model)
        {
            header = model.header;
            modelInfo = model.modelInfo;
            counts = new PMXMetadataCounts
            {
                vertices = model.vertices.Length,
                faceIndices = model.indices.Length,
                triangles = model.indices.Length / 3,
                textures = model.texturePaths.Length,
                materials = model.materials.Length,
                bones = model.bones.Length,
                morphs = model.morphs.Length,
                displayFrames = model.displayFrames.Length,
                rigidBodies = model.rigidBodies.Length,
                joints = model.joints.Length,
            };

            textures = new List<string>(model.texturePaths.Length);
            for (int i = 0; i < model.texturePaths.Length; ++i)
            {
                textures.Add(model.texturePaths[i].ToString());
            }

            materials = new List<PMXMaterialMetadata>(model.materials.Length);
            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial material = model.materials[i];
                materials.Add(new PMXMaterialMetadata
                {
                    index = i,
                    originalName = material.originalName.ToString(),
                    originalNameEN = material.originalNameEN.ToString(),
                    textureIndex = material.textureIndex,
                    sphereTextureIndex = material.sphereTextureIndex,
                    sphereTextureMode = material.sphereTextureMode,
                    toonTextureReferenceFlag = material.toonTextureReferenceFlag,
                    toonTextureIndex = material.toonTextureIndex,
                    builtinToonIndex = material.builtinToonIndex,
                    drawingFlags = material.drawingFlags.ToString(),
                    faceIndexCount = material.faceIndexCount,
                    triangleCount = material.faceIndexCount / 3,
                });
            }

            bones = new List<PMXBoneMetadata>(model.bones.Length);
            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone bone = model.bones[i];
                bones.Add(new PMXBoneMetadata
                {
                    index = i,
                    originalName = bone.originalName.ToString(),
                    originalNameEN = bone.originalNameEN.ToString(),
                    parentBoneIndex = bone.parentBoneIndex,
                    transformLevel = bone.transformLevel,
                    flags = bone.flags.ToString(),
                    hasIK = bone.ik != null,
                    ikLinkCount = bone.ik?.links.Length ?? 0,
                });
            }

            morphs = new List<PMXMorphMetadata>(model.morphs.Length);
            for (int i = 0; i < model.morphs.Length; ++i)
            {
                PMXMorph morph = model.morphs[i];
                morphs.Add(new PMXMorphMetadata
                {
                    index = i,
                    originalName = morph.originalName.ToString(),
                    originalNameEN = morph.originalNameEN.ToString(),
                    panel = morph.panel,
                    type = morph.type.ToString(),
                    offsetCount = morph.offsets?.Length ?? 0,
                });
            }

            displayFrames = new List<PMXDisplayFrameMetadata>(model.displayFrames.Length);
            for (int i = 0; i < model.displayFrames.Length; ++i)
            {
                PMXDisplayFrame displayFrame = model.displayFrames[i];
                displayFrames.Add(new PMXDisplayFrameMetadata
                {
                    index = i,
                    originalName = displayFrame.originalName.ToString(),
                    originalNameEN = displayFrame.originalNameEN.ToString(),
                    isSpecialFrame = displayFrame.isSpecialFrame,
                    elementCount = displayFrame.elements?.Length ?? 0,
                });
            }

            rigidBodies = new List<PMXRigidBodyMetadata>(model.rigidBodies.Length);
            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody rigidBody = model.rigidBodies[i];
                rigidBodies.Add(new PMXRigidBodyMetadata
                {
                    index = i,
                    originalName = rigidBody.originalName.ToString(),
                    originalNameEN = rigidBody.originalNameEN.ToString(),
                    relatedBoneIndex = rigidBody.relatedBoneIndex,
                    shape = rigidBody.shape,
                    mode = rigidBody.mode,
                });
            }

            joints = new List<PMXJointMetadata>(model.joints.Length);
            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint joint = model.joints[i];
                joints.Add(new PMXJointMetadata
                {
                    index = i,
                    originalName = joint.originalName.ToString(),
                    originalNameEN = joint.originalNameEN.ToString(),
                    type = joint.type,
                    rigidBodyAIndex = joint.rigidBodyAIndex,
                    rigidBodyBIndex = joint.rigidBodyBIndex,
                });
            }
        }

        /// <summary>Serializes this metadata to indented JSON, writing enum values as their names.</summary>
        /// <returns>The indented JSON representation of this metadata.</returns>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new StringEnumConverter() },
            });
        }
    }

    /// <summary>Element counts summarizing the size of a parsed PMX model.</summary>
    public sealed class PMXMetadataCounts
    {
        /// <summary>Number of vertices.</summary>
        public int vertices { get; set; }
        /// <summary>Number of face indices.</summary>
        public int faceIndices { get; set; }
        /// <summary>Number of triangles (face indices divided by three).</summary>
        public int triangles { get; set; }
        /// <summary>Number of texture paths.</summary>
        public int textures { get; set; }
        /// <summary>Number of materials.</summary>
        public int materials { get; set; }
        /// <summary>Number of bones.</summary>
        public int bones { get; set; }
        /// <summary>Number of morphs.</summary>
        public int morphs { get; set; }
        /// <summary>Number of display frames.</summary>
        public int displayFrames { get; set; }
        /// <summary>Number of rigid bodies.</summary>
        public int rigidBodies { get; set; }
        /// <summary>Number of joints.</summary>
        public int joints { get; set; }
    }

    /// <summary>Metadata summary for a single PMX material.</summary>
    public sealed class PMXMaterialMetadata
    {
        /// <summary>Material index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) material name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English material name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>Main texture index, or -1 if none.</summary>
        public int textureIndex { get; set; }
        /// <summary>Sphere texture index, or -1 if none.</summary>
        public int sphereTextureIndex { get; set; }
        /// <summary>Sphere texture blend mode.</summary>
        public PMXMaterial.SphereTextureMode sphereTextureMode { get; set; }
        /// <summary>True when the toon texture is a built-in shared toon.</summary>
        public bool toonTextureReferenceFlag { get; set; }
        /// <summary>Toon texture index, or -1 when using a built-in toon.</summary>
        public int toonTextureIndex { get; set; }
        /// <summary>Built-in shared toon index, or -1 when using a model toon texture.</summary>
        public int builtinToonIndex { get; set; }
        /// <summary>Drawing flags rendered as their flag names.</summary>
        public string drawingFlags { get; set; }
        /// <summary>Number of face indices consumed by this material.</summary>
        public int faceIndexCount { get; set; }
        /// <summary>Number of triangles consumed by this material.</summary>
        public int triangleCount { get; set; }
    }

    /// <summary>Metadata summary for a single PMX bone.</summary>
    public sealed class PMXBoneMetadata
    {
        /// <summary>Bone index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) bone name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English bone name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>Index of the parent bone, or -1 for a root bone.</summary>
        public int parentBoneIndex { get; set; }
        /// <summary>Deformation/transform order layer.</summary>
        public int transformLevel { get; set; }
        /// <summary>Bone flags rendered as their flag names.</summary>
        public string flags { get; set; }
        /// <summary>True when the bone drives an IK chain.</summary>
        public bool hasIK { get; set; }
        /// <summary>Number of links in the bone's IK chain, or 0 if none.</summary>
        public int ikLinkCount { get; set; }
    }

    /// <summary>Metadata summary for a single PMX morph.</summary>
    public sealed class PMXMorphMetadata
    {
        /// <summary>Morph index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) morph name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English morph name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>Editor display panel group.</summary>
        public byte panel { get; set; }
        /// <summary>Morph type name.</summary>
        public string type { get; set; }
        /// <summary>Number of offset entries in the morph.</summary>
        public int offsetCount { get; set; }
    }

    /// <summary>Metadata summary for a single PMX display frame.</summary>
    public sealed class PMXDisplayFrameMetadata
    {
        /// <summary>Display frame index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) display frame name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English display frame name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>True for reserved special frames.</summary>
        public bool isSpecialFrame { get; set; }
        /// <summary>Number of elements in the frame.</summary>
        public int elementCount { get; set; }
    }

    /// <summary>Metadata summary for a single PMX rigid body.</summary>
    public sealed class PMXRigidBodyMetadata
    {
        /// <summary>Rigid body index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) rigid body name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English rigid body name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>Index of the associated bone, or -1 if none.</summary>
        public int relatedBoneIndex { get; set; }
        /// <summary>Collision shape.</summary>
        public PMXRigidBody.Shape shape { get; set; }
        /// <summary>Physics behavior mode.</summary>
        public PMXRigidBody.Mode mode { get; set; }
    }

    /// <summary>Metadata summary for a single PMX joint.</summary>
    public sealed class PMXJointMetadata
    {
        /// <summary>Joint index within the model.</summary>
        public int index { get; set; }
        /// <summary>Original Japanese (local) joint name.</summary>
        public string originalName { get; set; }
        /// <summary>Original English joint name.</summary>
        public string originalNameEN { get; set; }
        /// <summary>Constraint type.</summary>
        public PMXJoint.Type type { get; set; }
        /// <summary>Index of the first connected rigid body.</summary>
        public int rigidBodyAIndex { get; set; }
        /// <summary>Index of the second connected rigid body.</summary>
        public int rigidBodyBIndex { get; set; }
    }
}
