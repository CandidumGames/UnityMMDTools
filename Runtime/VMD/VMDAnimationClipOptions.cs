using System;
using System.Collections.Generic;

namespace UMT
{
    /// <summary>
    /// Options controlling how a <see cref="VMDAnimation"/> is converted into a Unity <see cref="UnityEngine.AnimationClip"/>
    /// by <see cref="VMDAnimationClipConverter"/>.
    /// </summary>
    public sealed class VMDAnimationClipOptions
    {
        /// <summary>Frames per second of the generated clip. Defaults to 30.</summary>
        public float frameRate = 30.0f;

        /// <summary>When true, IK is solved and baked to FK bone curves; when false, sparse curves and IK toggle curves are written for the runtime solver. Defaults to true.</summary>
        public bool bakeIKToFK = true;

        /// <summary>When true (and IK is baked), physics-controlled bones are baked to FK curves. Defaults to true.</summary>
        public bool bakePhysicsToFK = true;

        /// <summary>Random seed used to initialize the physics simulation when baking physics. Defaults to 0.</summary>
        public uint physicsSeed = 0;

        /// <summary>Duration, in seconds, of the physics warm-up phase that settles bodies before sampling the bake. Defaults to 5.</summary>
        public float physicsWarmUpDuration = 5.0f;

        /// <summary>Optional callback invoked with a stage label and elapsed time for conversion timing measurements.</summary>
        public Action<string, TimeSpan> timingCallback;
    }
}