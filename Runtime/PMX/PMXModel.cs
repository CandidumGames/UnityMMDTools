using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// <see cref="ScriptableObject"/> holding a fully parsed PMX (MikuMikuDance) model: header, model info,
    /// vertices, face indices, texture paths, materials, bones, morphs, display frames, rigid bodies, and joints.
    /// Position-like values are already scaled from MMD units to Unity units during parsing.
    /// </summary>
    public class PMXModel : ScriptableObject
    {
        /// <summary>PMX file header: signature, version, text encoding, and per-section index byte sizes.</summary>
        public PMXHeader header;
        /// <summary>Model information block: Japanese/English names and comments.</summary>
        public PMXModelInfo modelInfo;
        /// <summary>All model vertices (position, normal, UV, additional UVs, weight, edge scale).</summary>
        public PMXVertex[] vertices = Array.Empty<PMXVertex>();
        /// <summary>Triangle face vertex indices; length is a multiple of three.</summary>
        public uint[] indices = Array.Empty<uint>();
        /// <summary>Relative texture file paths referenced by materials, indexed by texture index.</summary>
        public FixedString128Bytes[] texturePaths = Array.Empty<FixedString128Bytes>();
        /// <summary>Material definitions in draw order; each consumes a contiguous run of <see cref="indices"/>.</summary>
        public PMXMaterial[] materials = Array.Empty<PMXMaterial>();
        /// <summary>Bone definitions including hierarchy, IK, constraints, and axis data.</summary>
        public PMXBone[] bones = Array.Empty<PMXBone>();
        /// <summary>Morph (blend shape / deformation) definitions.</summary>
        public PMXMorph[] morphs = Array.Empty<PMXMorph>();
        /// <summary>Display (panel) frame groupings of bones and morphs.</summary>
        public PMXDisplayFrame[] displayFrames = Array.Empty<PMXDisplayFrame>();
        /// <summary>Rigid body definitions for the MMD physics pipeline.</summary>
        public PMXRigidBody[] rigidBodies = Array.Empty<PMXRigidBody>();
        /// <summary>Joint (constraint) definitions linking rigid bodies.</summary>
        public PMXJoint[] joints = Array.Empty<PMXJoint>();
    }

    /// <summary>PMX file header describing the file version, text encoding, and the byte size of each index type.</summary>
    [Serializable]
    public struct PMXHeader
    {
        /// <summary>Text encoding used for PMX strings in the file.</summary>
        public enum TextEncoding : byte
        {
            /// <summary>UTF-16 little-endian encoding.</summary>
            UTF16LE = 0,
            /// <summary>UTF-8 encoding.</summary>
            UTF8 = 1,
        }

        /// <summary>4-byte ASCII file signature; expected to be "PMX ".</summary>
        public FixedString32Bytes signature;
        /// <summary>PMX format version number (for example 2.0).</summary>
        public float version;
        /// <summary>Number of subsequent header data bytes describing index sizes.</summary>
        public byte dataCount;
        /// <summary>Encoding used to decode PMX text fields.</summary>
        public TextEncoding textEncoding;
        /// <summary>Number of additional per-vertex UV channels (0 to 4).</summary>
        public byte additionalUVCount;
        /// <summary>Byte size (1, 2, or 4) of a vertex index.</summary>
        public byte vertexIndexSize;
        /// <summary>Byte size (1, 2, or 4) of a texture index.</summary>
        public byte textureIndexSize;
        /// <summary>Byte size (1, 2, or 4) of a material index.</summary>
        public byte materialIndexSize;
        /// <summary>Byte size (1, 2, or 4) of a bone index.</summary>
        public byte boneIndexSize;
        /// <summary>Byte size (1, 2, or 4) of a morph index.</summary>
        public byte morphIndexSize;
        /// <summary>Byte size (1, 2, or 4) of a rigid body index.</summary>
        public byte rigidBodyIndexSize;
    }

    /// <summary>PMX model information: localized names and comment text.</summary>
    [Serializable]
    public struct PMXModelInfo
    {
        /// <summary>Japanese (local) model name.</summary>
        public FixedString128Bytes name;
        /// <summary>English model name.</summary>
        public FixedString128Bytes nameEN;
        /// <summary>Japanese (local) model comment.</summary>
        public FixedString128Bytes comment;
        /// <summary>English model comment.</summary>
        public FixedString128Bytes commentEN;
    }

    /// <summary>A single PMX vertex with position, normal, UVs, skinning weight, and edge scale.</summary>
    [Serializable]
    public struct PMXVertex
    {
        /// <summary>Vertex position in Unity space (already scaled from MMD units).</summary>
        public float3 position;
        /// <summary>Vertex normal direction.</summary>
        public float3 normal;
        /// <summary>Primary texture coordinate.</summary>
        public float2 uv;
        /// <summary>First additional UV channel; zero when unused.</summary>
        public float4 additionalUV1;
        /// <summary>Second additional UV channel; zero when unused.</summary>
        public float4 additionalUV2;
        /// <summary>Third additional UV channel; zero when unused.</summary>
        public float4 additionalUV3;
        /// <summary>Fourth additional UV channel; zero when unused.</summary>
        public float4 additionalUV4;
        /// <summary>Bone skinning weight (BDEF/SDEF/QDEF data).</summary>
        public PMXWeight weight;
        /// <summary>Per-vertex edge (outline) thickness multiplier.</summary>
        public float edgeScale;
    }

    /// <summary>PMX vertex skinning weight; carries up to four bone influences plus SDEF correction vectors.</summary>
    [Serializable]
    public struct PMXWeight
    {
        /// <summary>Deformation method used for this weight.</summary>
        public enum Type : byte
        {
            /// <summary>Single bone, full weight.</summary>
            BDEF1 = 0,
            /// <summary>Two bones with complementary weights.</summary>
            BDEF2 = 1,
            /// <summary>Four bones with explicit weights.</summary>
            BDEF4 = 2,
            /// <summary>Spherical deformation with two bones plus correction vectors.</summary>
            SDEF = 3,
            /// <summary>Dual-quaternion deformation with four bones.</summary>
            QDEF = 4,
        }

        /// <summary>Weight deformation type that determines which bone indices and weights are used.</summary>
        public Type type;
        /// <summary>First influencing bone index, or -1 if unused.</summary>
        public int boneIndex0;
        /// <summary>Second influencing bone index, or -1 if unused.</summary>
        public int boneIndex1;
        /// <summary>Third influencing bone index, or -1 if unused.</summary>
        public int boneIndex2;
        /// <summary>Fourth influencing bone index, or -1 if unused.</summary>
        public int boneIndex3;
        /// <summary>Weight for <see cref="boneIndex0"/>.</summary>
        public float weight0;
        /// <summary>Weight for <see cref="boneIndex1"/>.</summary>
        public float weight1;
        /// <summary>Weight for <see cref="boneIndex2"/>.</summary>
        public float weight2;
        /// <summary>Weight for <see cref="boneIndex3"/>.</summary>
        public float weight3;
        /// <summary>SDEF center position; only meaningful for SDEF weights.</summary>
        public float3 sdefC;
        /// <summary>SDEF R0 correction vector; only meaningful for SDEF weights.</summary>
        public float3 sdefR0;
        /// <summary>SDEF R1 correction vector; only meaningful for SDEF weights.</summary>
        public float3 sdefR1;
    }

    /// <summary>A PMX material describing colors, textures, drawing flags, edge (outline), and toon shading data.</summary>
    [Serializable]
    public struct PMXMaterial
    {
        /// <summary>Bit flags controlling how a material is drawn.</summary>
        [Flags]
        public enum DrawingFlags : byte
        {
            /// <summary>Disable back-face culling (render both sides).</summary>
            DoubleSided = 0x01,
            /// <summary>Cast shadows onto the ground.</summary>
            CastShadow = 0x02,
            /// <summary>Cast onto the shadow map.</summary>
            CastShadowMap = 0x04,
            /// <summary>Receive the model's self-shadow map.</summary>
            ReceiveSelfShadowMap = 0x08,
            /// <summary>Draw the edge (outline) for this material.</summary>
            DrawEdge = 0x10,
        }

        /// <summary>Blend mode applied to the sphere (matcap) texture.</summary>
        public enum SphereTextureMode : byte
        {
            /// <summary>No sphere texture.</summary>
            None = 0,
            /// <summary>Multiply the sphere texture.</summary>
            Mul = 1,
            /// <summary>Add the sphere texture.</summary>
            Add = 2,
            /// <summary>Use the sphere texture as a sub-texture sampled by additional UV.</summary>
            SubTexture = 3,
        }

        /// <summary>Renamed, ASCII-safe material name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) material name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English material name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>Diffuse color including alpha.</summary>
        public Color diffuse;
        /// <summary>Specular color (RGB).</summary>
        public float3 specular;
        /// <summary>Specular intensity / shininess.</summary>
        public float specularStrength;
        /// <summary>Ambient color (RGB).</summary>
        public float3 ambient;
        /// <summary>Material drawing flags.</summary>
        public DrawingFlags drawingFlags;
        /// <summary>Edge (outline) color including alpha.</summary>
        public Color edgeColor;
        /// <summary>Edge (outline) thickness.</summary>
        public float edgeSize;
        /// <summary>Index into the texture path table for the main texture, or -1 if none.</summary>
        public int textureIndex;
        /// <summary>Index into the texture path table for the sphere (matcap) texture, or -1 if none.</summary>
        public int sphereTextureIndex;
        /// <summary>Blend mode used for the sphere texture.</summary>
        public SphereTextureMode sphereTextureMode;
        /// <summary>True when the toon texture is a built-in shared toon rather than a model texture.</summary>
        public bool toonTextureReferenceFlag;
        /// <summary>Index into the texture path table for the toon texture, or -1 when using a built-in toon.</summary>
        public int toonTextureIndex;
        /// <summary>Built-in shared toon index, or -1 when using a model toon texture.</summary>
        public int builtinToonIndex;
        /// <summary>Free-form memo / comment text for the material.</summary>
        public FixedString128Bytes memo;
        /// <summary>Number of face indices consumed by this material (a multiple of three).</summary>
        public int faceIndexCount;
    }

    /// <summary>A PMX bone: hierarchy, transform order, optional IK, constraints, fixed/local axes, and external parent data.</summary>
    [Serializable]
    public struct PMXBone
    {
        /// <summary>Bit flags describing a bone's capabilities and which optional data blocks follow it.</summary>
        [Flags]
        public enum Flags : ushort
        {
            /// <summary>No flags set.</summary>
            None = 0x0000,
            /// <summary>The connection tip is given by another bone index rather than an offset.</summary>
            HasConnection = 0x0001,
            /// <summary>The bone may be rotated.</summary>
            Rotatable = 0x0002,
            /// <summary>The bone may be translated.</summary>
            Translatable = 0x0004,
            /// <summary>The bone is visible in the editor.</summary>
            Visible = 0x0008,
            /// <summary>The bone is user-operable.</summary>
            Operable = 0x0010,
            /// <summary>The bone drives an IK chain.</summary>
            IK = 0x0020,
            /// <summary>The constraint operates in local space.</summary>
            LocalConstraint = 0x0080,
            /// <summary>The bone inherits rotation from a target bone.</summary>
            RotationConstraint = 0x0100,
            /// <summary>The bone inherits translation from a target bone.</summary>
            TranslationConstraint = 0x0200,
            /// <summary>The bone is restricted to a fixed axis.</summary>
            FixAxis = 0x0400,
            /// <summary>The bone defines its own local coordinate axes.</summary>
            LocalAxis = 0x0800,
            /// <summary>The bone is transformed after physics simulation.</summary>
            AfterPhysics = 0x1000,
            /// <summary>The bone is parented to an external key.</summary>
            ExternalParent = 0x2000
        }

        /// <summary>Renamed, ASCII-safe and unique bone name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) bone name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English bone name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>Bone head position in Unity space (already scaled from MMD units).</summary>
        public float3 position;
        /// <summary>Index of the parent bone, or -1 for a root bone.</summary>
        public int parentBoneIndex;
        /// <summary>Deformation/transform order layer used to sort bone evaluation.</summary>
        public int transformLevel;
        /// <summary>Bone capability flags.</summary>
        public Flags flags;
        /// <summary>Connection tip bone index when <see cref="Flags.HasConnection"/> is set, otherwise -1.</summary>
        public int connectionBoneIndex;
        /// <summary>Connection tip offset when <see cref="Flags.HasConnection"/> is not set.</summary>
        public float3 positionOffset;
        /// <summary>Source bone index for a rotation/translation constraint, or -1 if none.</summary>
        public int constraintTargetIndex;
        /// <summary>Influence ratio applied to the inherited constraint transform.</summary>
        public float constraintInfluence ;
        /// <summary>Fixed rotation axis used when <see cref="Flags.FixAxis"/> is set.</summary>
        public float3 axis;
        /// <summary>Local X axis used when <see cref="Flags.LocalAxis"/> is set.</summary>
        public float3 localXAxis;
        /// <summary>Local Z axis used when <see cref="Flags.LocalAxis"/> is set.</summary>
        public float3 localZAxis;
        /// <summary>External parent key used when <see cref="Flags.ExternalParent"/> is set.</summary>
        public int externalKey;
        /// <summary>IK chain data when <see cref="Flags.IK"/> is set, otherwise null.</summary>
        public PMXIK ik;
    }

    /// <summary>IK (inverse kinematics) chain data attached to an IK driver bone.</summary>
    [Serializable]
    public sealed class PMXIK
    {
        /// <summary>Index of the bone the IK chain attempts to move to the target.</summary>
        public int targetBoneIndex;
        /// <summary>Maximum number of solver iterations.</summary>
        public int iterations;
        /// <summary>Per-iteration angle limit in radians.</summary>
        public float angleLimit;
        /// <summary>Ordered IK chain links from the effector toward the root.</summary>
        public PMXIKLink[] links = Array.Empty<PMXIKLink>();
    }

    /// <summary>A single link (joint) within an IK chain, with optional per-link angle limits.</summary>
    [Serializable]
    public struct PMXIKLink
    {
        /// <summary>Index of the bone controlled by this link.</summary>
        public int boneIndex;
        /// <summary>True when this link constrains rotation to the limit range.</summary>
        public bool hasAngleLimit;
        /// <summary>Lower Euler rotation limit (radians), valid when <see cref="hasAngleLimit"/> is true.</summary>
        public float3 lowerLimit;
        /// <summary>Upper Euler rotation limit (radians), valid when <see cref="hasAngleLimit"/> is true.</summary>
        public float3 upperLimit;
    }

    /// <summary>A PMX morph (deformation target) whose <see cref="type"/> selects the kind of stored offsets.</summary>
    [Serializable]
    public struct PMXMorph
    {
        /// <summary>Kind of data a morph applies.</summary>
        public enum Type : byte
        {
            /// <summary>Combines other morphs by rate.</summary>
            Group = 0,
            /// <summary>Offsets vertex positions (a Unity blend shape).</summary>
            Vertex = 1,
            /// <summary>Offsets bone translation and rotation.</summary>
            Bone = 2,
            /// <summary>Offsets the primary UV channel.</summary>
            UV = 3,
            /// <summary>Offsets additional UV channel 1.</summary>
            AdditionalUV1 = 4,
            /// <summary>Offsets additional UV channel 2.</summary>
            AdditionalUV2 = 5,
            /// <summary>Offsets additional UV channel 3.</summary>
            AdditionalUV3 = 6,
            /// <summary>Offsets additional UV channel 4.</summary>
            AdditionalUV4 = 7,
            /// <summary>Offsets material color/texture parameters.</summary>
            Material = 8,
            /// <summary>Selects (flips to) a single group morph entry.</summary>
            Flip = 9,
            /// <summary>Applies an impulse to a rigid body.</summary>
            Impulse = 10,
        }

        /// <summary>Renamed, ASCII-safe morph name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) morph name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English morph name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>Editor display panel group the morph belongs to.</summary>
        public byte panel;
        /// <summary>Morph type that determines the concrete offset class in <see cref="offsets"/>.</summary>
        public Type type;
        /// <summary>Morph offset entries; the runtime type of each matches <see cref="type"/>.</summary>
        [SerializeReference]
        public PMXMorphOffset[] offsets;
    }

    /// <summary>Abstract base for a single morph offset entry. Concrete subclasses correspond to <see cref="PMXMorph.Type"/>.</summary>
    [Serializable]
    public abstract class PMXMorphOffset
    {
    }

    /// <summary>Offset for a Group or Flip morph that references another morph by index with a blend rate.</summary>
    [Serializable]
    public sealed class PMXGroupMorphData : PMXMorphOffset
    {
        /// <summary>Index of the referenced morph.</summary>
        public int morphIndex;
        /// <summary>Blend rate applied to the referenced morph.</summary>
        public float rate;
    }

    /// <summary>Offset for a Vertex morph that displaces a single vertex.</summary>
    [Serializable]
    public sealed class PMXVertexMorphData : PMXMorphOffset
    {
        /// <summary>Index of the affected vertex.</summary>
        public uint vertexIndex;
        /// <summary>Position displacement in Unity space (already scaled from MMD units).</summary>
        public float3 positionOffset;
    }

    /// <summary>Offset for a Bone morph that adds a translation and rotation to a bone.</summary>
    [Serializable]
    public sealed class PMXBoneMorphData : PMXMorphOffset
    {
        /// <summary>Index of the affected bone.</summary>
        public int boneIndex;
        /// <summary>Translation offset in Unity space (already scaled from MMD units).</summary>
        public float3 translation;
        /// <summary>Rotation offset.</summary>
        public quaternion rotation;
    }

    /// <summary>Offset for a UV (or additional UV) morph that displaces a vertex's texture coordinates.</summary>
    [Serializable]
    public sealed class PMXUVMorphData : PMXMorphOffset
    {
        /// <summary>Index of the affected vertex.</summary>
        public uint vertexIndex;
        /// <summary>UV coordinate displacement (XYZW for the targeted UV channel).</summary>
        public float4 uvOffset;
    }

    /// <summary>Offset for a Material morph that multiplies or adds to a material's color and texture parameters.</summary>
    [Serializable]
    public sealed class PMXMaterialMorphData : PMXMorphOffset
    {
        /// <summary>How a material morph combines with the base material values.</summary>
        public enum Method : byte
        {
            /// <summary>Multiply the base values by the offset.</summary>
            Multiply = 0,
            /// <summary>Add the offset to the base values.</summary>
            Add = 1
        }
        /// <summary>Index of the affected material, or -1 to affect all materials.</summary>
        public int materialIndex;
        /// <summary>Whether the offset is multiplied or added.</summary>
        public Method offsetMethod;
        /// <summary>Diffuse color offset including alpha.</summary>
        public Color diffuse;
        /// <summary>Specular color offset (RGB).</summary>
        public float3 specular;
        /// <summary>Specular strength offset.</summary>
        public float specularStrength;
        /// <summary>Ambient color offset (RGB).</summary>
        public float3 ambient;
        /// <summary>Edge (outline) color offset including alpha.</summary>
        public Color edgeColor;
        /// <summary>Edge (outline) size offset.</summary>
        public float edgeSize;
        /// <summary>Main texture tint offset (RGBA).</summary>
        public float4 textureTint;
        /// <summary>Sphere texture tint offset (RGBA).</summary>
        public float4 sphereTint;
        /// <summary>Toon texture tint offset (RGBA).</summary>
        public float4 toonTint;
    }

    /// <summary>Offset for an Impulse morph that applies a velocity and torque to a rigid body.</summary>
    [Serializable]
    public sealed class PMXImpulseMorphData : PMXMorphOffset
    {
        /// <summary>Index of the affected rigid body.</summary>
        public int rigidBodyIndex;
        /// <summary>True when the impulse is applied in the rigid body's local space.</summary>
        public bool isLocal;
        /// <summary>Linear velocity impulse.</summary>
        public float3 velocity;
        /// <summary>Angular velocity (torque) impulse.</summary>
        public float3 torque;
    }

    /// <summary>A PMX display (panel) frame grouping bones and morphs for the editor's manipulation panels.</summary>
    [Serializable]
    public struct PMXDisplayFrame
    {
        /// <summary>Renamed, ASCII-safe display frame name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) display frame name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English display frame name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>True for the reserved special frames (Root and facial expressions).</summary>
        public bool isSpecialFrame;
        /// <summary>Bone and morph elements contained in this frame.</summary>
        public PMXDisplayFrameElement[] elements;
    }

    /// <summary>A single element within a display frame referencing either a bone or a morph.</summary>
    [Serializable]
    public struct PMXDisplayFrameElement
    {
        /// <summary>Whether a display frame element points to a bone or a morph.</summary>
        public enum Type : byte
        {
            /// <summary>The element references a bone.</summary>
            Bone = 0,
            /// <summary>The element references a morph.</summary>
            Morph = 1,
        }

        /// <summary>Kind of object referenced by <see cref="targetIndex"/>.</summary>
        public Type targetType;
        /// <summary>Index of the referenced bone or morph.</summary>
        public int targetIndex;
    }

    /// <summary>A PMX rigid body used by the MMD physics pipeline, attached to a related bone.</summary>
    [Serializable]
    public struct PMXRigidBody
    {
        /// <summary>Collision shape of a rigid body.</summary>
        public enum Shape : byte
        {
            /// <summary>Sphere shape.</summary>
            Sphere = 0,
            /// <summary>Box shape.</summary>
            Box = 1,
            /// <summary>Capsule shape.</summary>
            Capsule = 2,
        }

        /// <summary>Physics behavior mode of a rigid body.</summary>
        public enum Mode : byte
        {
            /// <summary>Follows its bone (kinematic / bone-driven).</summary>
            Kinetic = 0,
            /// <summary>Driven fully by the physics simulation.</summary>
            Dynamic = 1,
            /// <summary>Simulated but realigned to its bone position.</summary>
            DynamicBoneAligned = 2,
        }

        /// <summary>Renamed, ASCII-safe rigid body name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) rigid body name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English rigid body name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>Index of the bone this rigid body is associated with, or -1 if none.</summary>
        public int relatedBoneIndex;
        /// <summary>Collision group index (0 to 15).</summary>
        public byte groupIndex;
        /// <summary>Bit mask of collision groups this body does not collide with.</summary>
        public short collisionGroupMask;
        /// <summary>Collision shape.</summary>
        public Shape shape;
        /// <summary>Shape dimensions in Unity space (already scaled from MMD units).</summary>
        public float3 size;
        /// <summary>Body position in Unity space (already scaled from MMD units).</summary>
        public float3 position;
        /// <summary>Body rotation as Euler angles.</summary>
        public float3 rotation;
        /// <summary>Mass of the body.</summary>
        public float mass;
        /// <summary>Linear movement damping.</summary>
        public float linearDamping;
        /// <summary>Angular (rotational) damping.</summary>
        public float angularDamping;
        /// <summary>Bounciness (restitution) coefficient.</summary>
        public float restitution;
        /// <summary>Friction coefficient.</summary>
        public float friction;
        /// <summary>Physics behavior mode.</summary>
        public Mode mode;
    }

    /// <summary>A PMX joint (constraint) connecting two rigid bodies, with translation/rotation limits and springs.</summary>
    [Serializable]
    public struct PMXJoint
    {
        /// <summary>Constraint type of a joint.</summary>
        public enum Type : byte
        {
            /// <summary>Generic 6-DOF spring constraint.</summary>
            Spring6DOF = 0,
            /// <summary>Generic 6-DOF constraint without springs.</summary>
            Generic6DOF = 1,
            /// <summary>Point-to-point constraint.</summary>
            P2P = 2,
            /// <summary>Cone-twist constraint.</summary>
            ConeTwist = 3,
            /// <summary>Slider constraint.</summary>
            Slider = 4,
            /// <summary>Hinge constraint.</summary>
            Hinge = 5,
        }

        /// <summary>Renamed, ASCII-safe joint name produced by the importer.</summary>
        public FixedString128Bytes renamedName;
        /// <summary>Original Japanese (local) joint name from the file.</summary>
        public FixedString128Bytes originalName;
        /// <summary>Original English joint name from the file.</summary>
        public FixedString128Bytes originalNameEN;
        /// <summary>Constraint type.</summary>
        public Type type;
        /// <summary>Index of the first connected rigid body.</summary>
        public int rigidBodyAIndex;
        /// <summary>Index of the second connected rigid body.</summary>
        public int rigidBodyBIndex;
        /// <summary>Joint position in Unity space (already scaled from MMD units).</summary>
        public float3 position;
        /// <summary>Joint rotation as Euler angles.</summary>
        public float3 rotation;
        /// <summary>Minimum translation limit in Unity space.</summary>
        public float3 translationLimitMin;
        /// <summary>Maximum translation limit in Unity space.</summary>
        public float3 translationLimitMax;
        /// <summary>Minimum rotation limit as Euler angles.</summary>
        public float3 rotationLimitMin;
        /// <summary>Maximum rotation limit as Euler angles.</summary>
        public float3 rotationLimitMax;
        /// <summary>Per-axis translation spring stiffness.</summary>
        public float3 springTranslation;
        /// <summary>Per-axis rotation spring stiffness.</summary>
        public float3 springRotation;
    }
}
