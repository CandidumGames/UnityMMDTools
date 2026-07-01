using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Parsed MikuMikuDance VMD (Vocaloid Motion Data) animation stored as a Unity <see cref="ScriptableObject"/>. Holds the model name, format version, and the bone, morph, camera, light, self-shadow, and show/IK frame tracks produced by <see cref="VMDReader"/>.
    /// </summary>
    public sealed class VMDAnimation : ScriptableObject
    {
        /// <summary>
        /// VMD file format version, distinguished by the file signature.
        /// </summary>
        public enum Version : byte
        {
            /// <summary>
            /// Version 1 VMD signature ("Vocaloid Motion Data file").
            /// </summary>
            V1,

            /// <summary>
            /// Version 2 VMD signature ("Vocaloid Motion Data 0002").
            /// </summary>
            V2,
        }

        /// <summary>Target model name decoded from the VMD header using CP932.</summary>
        [HideInInspector]
        public FixedString32Bytes modelName;

        /// <summary>Detected VMD format version.</summary>
        [HideInInspector]
        public Version version;

        /// <summary>Bone keyframes parsed from the VMD bone section.</summary>
        [HideInInspector]
        public VMDBoneFrame[] boneFrames = Array.Empty<VMDBoneFrame>();

        /// <summary>Morph (facial/blend-shape) keyframes parsed from the VMD morph section.</summary>
        [HideInInspector]
        public VMDMorphFrame[] morphFrames = Array.Empty<VMDMorphFrame>();

        /// <summary>Camera keyframes parsed from the optional VMD camera section.</summary>
        [HideInInspector]
        public VMDCameraFrame[] cameraFrames = Array.Empty<VMDCameraFrame>();

        /// <summary>Light keyframes parsed from the optional VMD light section.</summary>
        [HideInInspector]
        public VMDLightFrame[] lightFrames = Array.Empty<VMDLightFrame>();

        /// <summary>Self-shadow keyframes parsed from the optional VMD self-shadow section.</summary>
        [HideInInspector]
        public VMDSelfShadowFrame[] selfShadowFrames = Array.Empty<VMDSelfShadowFrame>();

        /// <summary>Show/IK keyframes parsed from the optional VMD show-IK section.</summary>
        [HideInInspector]
        public VMDShowIKFrame[] showIKFrames = Array.Empty<VMDShowIKFrame>();
    }

    /// <summary>
    /// A single cubic Bezier interpolation curve defined by its two control points, as stored in a VMD frame. Control point coordinates are normalized to the 0..1 range.
    /// </summary>
    [Serializable]
    public struct VMDBezierInterpolation
    {
        /// <summary>X coordinate of the first Bezier control point.</summary>
        public float x1;

        /// <summary>Y coordinate of the first Bezier control point.</summary>
        public float y1;

        /// <summary>X coordinate of the second Bezier control point.</summary>
        public float x2;

        /// <summary>Y coordinate of the second Bezier control point.</summary>
        public float y2;
    }

    /// <summary>
    /// Per-channel Bezier interpolation for a VMD bone keyframe, with separate curves for each position axis and rotation.
    /// </summary>
    [Serializable]
    public struct VMDBoneInterpolation
    {
        /// <summary>Interpolation curve for the X position channel.</summary>
        public VMDBezierInterpolation positionX;

        /// <summary>Interpolation curve for the Y position channel.</summary>
        public VMDBezierInterpolation positionY;

        /// <summary>Interpolation curve for the Z position channel.</summary>
        public VMDBezierInterpolation positionZ;

        /// <summary>Interpolation curve for the rotation channel.</summary>
        public VMDBezierInterpolation rotation;
    }

    /// <summary>
    /// Per-channel Bezier interpolation for a VMD camera keyframe.
    /// </summary>
    [Serializable]
    public struct VMDCameraInterpolation
    {
        /// <summary>Interpolation curve for camera movement (target position).</summary>
        public VMDBezierInterpolation movement;

        /// <summary>Interpolation curve for camera rotation.</summary>
        public VMDBezierInterpolation rotation;

        /// <summary>Interpolation curve for camera distance.</summary>
        public VMDBezierInterpolation distance;

        /// <summary>Interpolation curve for the camera view angle (FOV).</summary>
        public VMDBezierInterpolation viewAngle;
    }

    /// <summary>
    /// A single VMD bone keyframe: target bone name, frame number, local position/rotation offset, and interpolation. Position is converted to Unity space during parsing.
    /// </summary>
    [Serializable]
    public struct VMDBoneFrame
    {
        /// <summary>Name of the target bone, decoded with CP932.</summary>
        public FixedString32Bytes boneName;

        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Local position offset for the bone at this frame.</summary>
        public float3 position;

        /// <summary>Local rotation for the bone at this frame.</summary>
        public quaternion rotation;

        /// <summary>Per-channel Bezier interpolation toward this keyframe.</summary>
        public VMDBoneInterpolation interpolation;
    }

    /// <summary>
    /// A single VMD morph keyframe: target morph name, frame number, and weight.
    /// </summary>
    [Serializable]
    public struct VMDMorphFrame
    {
        /// <summary>Name of the target morph, decoded with CP932.</summary>
        public FixedString32Bytes morphName;

        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Morph weight at this frame.</summary>
        public float weight;
    }

    /// <summary>
    /// A single VMD camera keyframe: frame number, distance, target position, rotation, interpolation, view angle (FOV), and perspective toggle.
    /// </summary>
    [Serializable]
    public struct VMDCameraFrame
    {
        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Distance from the camera to its target.</summary>
        public float distance;

        /// <summary>Camera target (look-at) position.</summary>
        public float3 targetPosition;

        /// <summary>Camera rotation, as Euler angles.</summary>
        public float3 rotation;

        /// <summary>Per-channel Bezier interpolation toward this keyframe.</summary>
        public VMDCameraInterpolation interpolation;

        /// <summary>View angle (FOV) in degrees.</summary>
        public uint viewAngle;

        /// <summary>When true, perspective projection is disabled (orthographic).</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool perspectiveOff;
    }

    /// <summary>
    /// A single VMD light keyframe: frame number, light color, and direction/position.
    /// </summary>
    [Serializable]
    public struct VMDLightFrame
    {
        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Light color at this frame.</summary>
        public Color color;

        /// <summary>Light direction/position at this frame.</summary>
        public float3 position;
    }

    /// <summary>
    /// A single VMD self-shadow keyframe: frame number, shadow mode, and distance.
    /// </summary>
    [Serializable]
    public struct VMDSelfShadowFrame
    {
        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Self-shadow mode flag.</summary>
        public byte mode;

        /// <summary>Self-shadow distance parameter.</summary>
        public float distance;
    }

    /// <summary>
    /// A single VMD show/IK keyframe: frame number, model visibility flag, and per-bone IK enable toggles.
    /// </summary>
    [Serializable]
    public struct VMDShowIKFrame
    {
        /// <summary>Frame number on the VMD timeline.</summary>
        public uint frame;

        /// <summary>Whether the model is shown at this frame.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool show;

        /// <summary>Per-IK-bone enable/disable toggles active at this frame.</summary>
        public VMDIKToggleFrame[] ikToggles;
    }

    /// <summary>
    /// A single IK enable/disable toggle for one bone within a VMD show/IK keyframe.
    /// </summary>
    [Serializable]
    public struct VMDIKToggleFrame
    {
        /// <summary>Name of the IK bone, decoded with CP932.</summary>
        public FixedString32Bytes boneName;

        /// <summary>Frame number on the VMD timeline (inherited from the parent show/IK frame).</summary>
        public uint frame;

        /// <summary>Whether IK is enabled for this bone at this frame.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool enabled;
    }
}
