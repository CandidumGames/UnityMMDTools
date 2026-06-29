using System;
using Unity.Collections;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Converts a parsed <see cref="VMDAnimation"/> into a Unity <see cref="AnimationClip"/> for an imported PMX root
    /// driven by an <see cref="MMDTransformManager"/>. Supports baked FK output, optional physics baking, morph
    /// (blend-shape) curves, IK toggle curves, and quaternion continuity. Conversion progress is reported through a
    /// <see cref="ProgressCallback"/> using the <see cref="Stage"/> enum.
    /// </summary>
    public static partial class VMDAnimationClipConverter
    {

        /// <summary>Animated property name (on <see cref="MMDBoneTransform"/>) carrying the IK enable/disable toggle in non-baked clips.</summary>
        public const string k_IKEnabledProperty = "ikEnabled";

        /// <summary>
        /// Progress stages reported during VMD-to-clip conversion.
        /// </summary>
        public enum Stage
        {
            /// <summary>Initial setup before any curves are written.</summary>
            Setup,

            /// <summary>Bone curve conversion (FK bake or sparse curves) is in progress.</summary>
            BoneConversion,

            /// <summary>Morph (blend-shape) curve conversion is in progress.</summary>
            MorphConversion,

            /// <summary>Camera curve conversion is in progress.</summary>
            CameraConversion,

            /// <summary>Final clip processing such as quaternion continuity.</summary>
            Finalization,

            /// <summary>Conversion has completed.</summary>
            Complete,
        }

        /// <summary>
        /// Callback invoked to report conversion progress.
        /// </summary>
        /// <param name="stage">The current conversion stage.</param>
        /// <param name="frame">The number of frames processed so far in the current stage.</param>
        /// <param name="totalFrames">The total number of frames to process in the current stage.</param>
        public delegate void ProgressCallback(Stage stage, int frame, int totalFrames);

        /// <summary>
        /// Converts a VMD animation into an <see cref="AnimationClip"/> by building standalone solver and physics
        /// contexts directly from the <see cref="PMXModel"/>, without requiring an instantiated PMX prefab/root.
        /// Builds bone curves (baked FK or sparse runtime-solved with IK toggle curves), morph curves, and ensures
        /// quaternion continuity.
        /// </summary>
        /// <param name="animation">The parsed VMD animation to convert.</param>
        /// <param name="model">The PMX model used to build the transform and physics contexts.</param>
        /// <param name="options">Conversion options; a default instance is used when null.</param>
        /// <param name="progress">Optional progress callback.</param>
        /// <returns>The generated <see cref="VMDModelClipData"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="animation"/> or <paramref name="model"/> is null.</exception>
        public static VMDModelClipData Convert(
            VMDAnimation animation,
            PMXModel model,
            VMDAnimationClipOptions options = null,
            ProgressCallback progress = null)
        {
            if (animation == null)
            {
                throw new ArgumentNullException(nameof(animation));
            }
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            options ??= new VMDAnimationClipOptions();
            ReportProgress(progress, Stage.Setup, 0, 0);
            VMDModelClipData clipData = CreateModelClipData(model, options.bakeIKToFK);
            PMXAnimationPaths paths = PMXAnimationPathBuilder.Build(model);
            MMDTransformManager.SolverContext transformContext = default;
            MMDPhysicsManager.PhysicsSolverContext physicsContext = default;
            MMDTransformManager.InitializeSolverContext(model, ref transformContext);
            MMDTransformManager.SetSolveConstraintsAndIK(
                ref transformContext,
                true);

            bool bakePhysics =
                options.bakeIKToFK &&
                options.bakePhysicsToFK &&
                model.rigidBodies.Length > 0;
            if (bakePhysics)
            {
                MMDPhysicsManager.InitializePhysicsContext(
                    model,
                    options.physicsSeed,
                    true,
                    ref physicsContext);
            }

            IndexResolver resolver = new IndexResolver(model, Allocator.Persistent);
            using (UMTTiming.Measure(options.timingCallback, "Bone Conversion"))
            {
                if (options.bakeIKToFK)
                {
                    AddBakedBoneCurves(
                        clipData.bones,
                        animation,
                        ref transformContext,
                        ref physicsContext,
                        paths.bonePaths,
                        bakePhysics,
                        ref resolver,
                        options,
                        progress);
                }
                else
                {
                    AddBoneCurves(
                        clipData.bones,
                        animation,
                        model,
                        ref transformContext,
                        paths.bonePaths,
                        ref resolver,
                        options.frameRate,
                        progress);
                    AddIKCurves(
                        clipData.ikToggles,
                        animation,
                        ref transformContext,
                        paths.bonePaths,
                        ref resolver,
                        options.frameRate);
                }
            }

            using (UMTTiming.Measure(options.timingCallback, "Morph Conversion"))
            {
                AddMorphCurves(
                    clipData.morphs,
                    animation,
                    model,
                    paths.morphRendererPaths,
                    ref resolver,
                    options.frameRate,
                    progress);
            }

            ReportProgress(progress, Stage.Finalization, 0, 0);
            resolver.Dispose();
            MMDPhysicsManager.DisposePhysicsContext(ref physicsContext);
            MMDTransformManager.DisposeSolverContext(ref transformContext);
            ReportProgress(progress, Stage.Complete, 1, 1);
            return clipData;
        }

        // Allocates a model clip-data container with bone path/curve arrays sized to the model's bone count and the
        // channel count for the chosen mode (7 baked / 6 non-baked). Non-baked also allocates the IK toggle group.
        private static VMDModelClipData CreateModelClipData(PMXModel model, bool bakeIKToFK)
        {
            int boneCount = model.bones.Length;
            int channelCount = bakeIKToFK ? k_BakedBoneChannelCount : k_NonBakedBoneChannelCount;
            VMDModelClipData clipData = new VMDModelClipData
            {
                baked = bakeIKToFK,
                bones = new VMDClipData(boneCount, channelCount),
            };
            if (!bakeIKToFK)
            {
                clipData.ikToggles = new VMDClipData();
            }
            return clipData;
        }

        /// <summary>
        /// Asynchronously converts a VMD animation into an <see cref="AnimationClip"/> by building standalone solver and physics
        /// contexts directly from the <see cref="PMXModel"/>, without requiring an instantiated PMX prefab/root.
        /// Builds bone curves (baked FK or sparse runtime-solved with IK toggle curves), morph curves, and ensures
        /// quaternion continuity.
        /// </summary>
        /// <param name="frameBudget">Frame budget for yielding.</param>
        /// <param name="animation">The parsed VMD animation to convert.</param>
        /// <param name="model">The PMX model used to build the transform and physics contexts.</param>
        /// <param name="options">Conversion options; a default instance is used when null.</param>
        /// <param name="progress">Optional progress callback.</param>
        /// <returns>The generated <see cref="VMDModelClipData"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="animation"/> or <paramref name="model"/> is null.</exception>
        public static async Awaitable<VMDModelClipData> ConvertAsync(
            UMTFrameBudget frameBudget,
            VMDAnimation animation,
            PMXModel model,
            PMXAnimationPaths paths,
            VMDAnimationClipOptions options = null,
            ProgressCallback progress = null)
        {
            if (animation == null)
            {
                throw new ArgumentNullException(nameof(animation));
            }
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            options ??= new VMDAnimationClipOptions();
            ReportProgress(progress, Stage.Setup, 0, 0);
            VMDModelClipData clipData = CreateModelClipData(model, options.bakeIKToFK);

            if (paths == null || paths.bonePaths.Length != model.bones.Length || paths.morphRendererPaths.Length != model.morphs.Length)
            {
                PMXAnimationPaths builtPaths = await PMXAnimationPathBuilder.BuildAsync(frameBudget, model);
                if (paths == null)
                {
                    paths = builtPaths;
                }
                else
                {
                    paths.CopyFrom(builtPaths);
                }
            }
            await frameBudget.YieldIfNeeded();

            MMDTransformManager.SolverContext transformContext = default;
            MMDPhysicsManager.PhysicsSolverContext physicsContext = default;
            MMDTransformManager.InitializeSolverContext(model, ref transformContext);
            MMDTransformManager.SetSolveConstraintsAndIK(
                ref transformContext,
                true);
            await frameBudget.YieldIfNeeded();
            bool bakePhysics =
                options.bakeIKToFK &&
                options.bakePhysicsToFK &&
                model.rigidBodies.Length > 0;
            if (bakePhysics)
            {
                MMDPhysicsManager.InitializePhysicsContext(
                    model,
                    options.physicsSeed,
                    true,
                    ref physicsContext);
            }
            await frameBudget.YieldIfNeeded();

            IndexResolver resolver = new IndexResolver(model, Allocator.Persistent);
            using (UMTTiming.Measure(options.timingCallback, "Bone Conversion"))
            {
                if (options.bakeIKToFK)
                {
                    await AddBakedBoneCurvesAsync(
                        frameBudget,
                        clipData.bones,
                        animation,
                        transformContext,
                        physicsContext,
                        paths.bonePaths,
                        bakePhysics,
                        resolver,
                        options,
                        progress);
                }
                else
                {
                    AddBoneCurves(
                        clipData.bones,
                        animation,
                        model,
                        ref transformContext,
                        paths.bonePaths,
                        ref resolver,
                        options.frameRate,
                        progress);
                    await frameBudget.YieldIfNeeded();
                    AddIKCurves(
                        clipData.ikToggles,
                        animation,
                        ref transformContext,
                        paths.bonePaths,
                        ref resolver,
                        options.frameRate);
                }
            }
            await frameBudget.YieldIfNeeded();

            using (UMTTiming.Measure(options.timingCallback, "Morph Conversion"))
            {
                AddMorphCurves(
                    clipData.morphs,
                    animation,
                    model,
                    paths.morphRendererPaths,
                    ref resolver,
                    options.frameRate,
                    progress);
            }
            await frameBudget.YieldIfNeeded();

            ReportProgress(progress, Stage.Finalization, 0, 0);
            resolver.Dispose();
            MMDPhysicsManager.DisposePhysicsContext(ref physicsContext);
            MMDTransformManager.DisposeSolverContext(ref transformContext);
            ReportProgress(progress, Stage.Complete, 1, 1);
            await frameBudget.YieldIfNeeded();
            return clipData;
        }

        private static void ReportProgress(ProgressCallback progress, Stage stage, int frame, int totalFrames)
        {
            if (progress != null)
            {
                progress(stage, frame, totalFrames);
            }
        }

    }
}
