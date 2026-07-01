using System;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Raw animation curve data produced by <see cref="VMDAnimationClipConverter"/> in place of a Unity <see cref="AnimationClip"/>. A <see cref="VMDClipData"/> is a flat set of <see cref="AnimationCurve"/> channels grouped by target path: <see cref="curves"/> holds <c>x</c> consecutive channels per entry in <see cref="paths"/>, where <c>x</c> is fixed for the kind of data and known by whatever code produces or consumes the instance (it is not stored here). A channel may be <c>null</c> when it was not written.
    /// </summary>
    /// <remarks>
    /// Why curves instead of an <see cref="AnimationClip"/>: <see cref="AnimationClip.SetCurve"/> only works at runtime for legacy clips, and Unity Timeline's <c>AnimationTrack</c> rejects legacy clips. To play VMD motion through Timeline at runtime the demo evaluates these curves directly in a custom playable and writes the transforms itself. The editor importers reconstruct an <see cref="AnimationClip"/> from this data (where <c>SetCurve</c> is valid) to keep writing <c>.anim</c> sub-assets.
    /// <para>Channel layouts used in this package:</para>
    /// <list type="bullet">
    /// <item><description>Baked bones: <c>x = 7</c> per bone &#8211; localPosition x/y/z, localRotation x/y/z/w (quaternion).</description></item>
    /// <item><description>Non-baked bones: <c>x = 6</c> per bone &#8211; localPosition x/y/z, localEulerAnglesRaw x/y/z.</description></item>
    /// <item><description>Morph and IK toggle: <c>x = 1</c> per path.</description></item>
    /// </list>
    /// </remarks>
    public sealed class VMDClipData
    {
        /// <summary>Target paths, one per channel group. For bones this is the bone transform path; for morphs the renderer path; for IK toggles the bone path.</summary>
        public string[] paths = Array.Empty<string>();

        /// <summary>Flat channel curves, <c>x</c> consecutive entries per <see cref="paths"/> entry. A channel is <c>null</c> when not written.</summary>
        public AnimationCurve[] curves = Array.Empty<AnimationCurve>();

        public VMDClipData()
        {
        }

        /// <summary>
        /// Allocates the path and curve arrays for <paramref name="pathCount"/> paths with <paramref name="channelsPerPath"/> channels each.
        /// </summary>
        public VMDClipData(int pathCount, int channelsPerPath)
        {
            paths = new string[pathCount];
            curves = new AnimationCurve[checked(pathCount * channelsPerPath)];
        }
    }

    /// <summary>
    /// Blendshape (morph) weight curves. Parallel arrays: <see cref="VMDClipData.paths"/> holds the renderer path and <see cref="VMDClipData.curves"/> the weight curve (one channel per path), while <see cref="names"/> holds the blendshape name for the same index so consumers can reconstruct the <c>blendShape.&lt;name&gt;</c> binding.
    /// </summary>
    public sealed class VMDMorphClipData
    {
        /// <summary>Renderer paths, one per morph channel.</summary>
        public string[] paths = Array.Empty<string>();

        /// <summary>Blendshape names, parallel to <see cref="paths"/>.</summary>
        public string[] names = Array.Empty<string>();

        /// <summary>Blendshape weight curves, parallel to <see cref="paths"/>.</summary>
        public AnimationCurve[] curves = Array.Empty<AnimationCurve>();
    }

    /// <summary>
    /// Full result of converting the body of a VMD animation: bone curves, morph curves, and (non-baked only) IK toggle curves. The <see cref="baked"/> flag records whether <see cref="bones"/> uses the baked 7-channel quaternion layout or the non-baked 6-channel euler layout, so consumers pick the matching channel order and property names.
    /// </summary>
    public sealed class VMDModelClipData
    {
        /// <summary>True when <see cref="bones"/> is the baked output (7 channels/bone, quaternion rotation); false for the non-baked output (6 channels/bone, euler rotation).</summary>
        public bool baked;

        /// <summary>Bone transform curves. 7 channels per bone when <see cref="baked"/>, otherwise 6.</summary>
        public VMDClipData bones = new VMDClipData();

        /// <summary>Blendshape weight curves.</summary>
        public VMDMorphClipData morphs = new VMDMorphClipData();

        /// <summary>IK toggle (<c>ikEnabled</c>) curves, populated only for non-baked output; <c>null</c> when baked.</summary>
        public VMDClipData ikToggles;
    }

    /// <summary>
    /// Camera curve data for the two-node VMD camera rig. <see cref="VMDClipData.paths"/> holds the rig-relative paths (the look-at target and its camera child) and <see cref="VMDClipData.curves"/> the channels for each, laid out in the order documented by <see cref="VMDAnimationClipConverter"/>: target localPosition x/y/z, target localRotation x/y/z/w, camera child localPosition.z, and camera field of view.
    /// </summary>
    public sealed class VMDCameraClipData
    {
        /// <summary>Camera target look-at center localPosition.x curve.</summary>
        public AnimationCurve targetPositionX;

        /// <summary>Camera target look-at center localPosition.y curve.</summary>
        public AnimationCurve targetPositionY;

        /// <summary>Camera target look-at center localPosition.z curve.</summary>
        public AnimationCurve targetPositionZ;

        /// <summary>Camera target look-at center localRotation.x curve.</summary>
        public AnimationCurve targetRotationX;

        /// <summary>Camera target look-at center localRotation.y curve.</summary>
        public AnimationCurve targetRotationY;

        /// <summary>Camera target look-at center localRotation.z curve.</summary>
        public AnimationCurve targetRotationZ;

        /// <summary>Camera target look-at center localRotation.w curve.</summary>
        public AnimationCurve targetRotationW;

        /// <summary>Camera child localPosition.z curve (the camera distance along the look-at axis).</summary>
        public AnimationCurve cameraLocalPositionZ;

        /// <summary>Camera field of view curve.</summary>
        public AnimationCurve fieldOfView;
    }
}
