using UMT;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Unity <see cref="ScriptedImporter"/> for <c>.pmx</c> assets that runs the full PMX import pipeline and,
    /// optionally, converts attached VMD animations into <see cref="AnimationClip"/> sub-assets.
    /// </summary>
    [ScriptedImporter(1, new[] { "pmx" })]
    public sealed class PMXScriptedImporter : ScriptedImporter
    {
        /// <summary>Project path of the alpha-detector compute shader used by the material builder.</summary>
        public const string k_AlphaDetectorShaderPath = "Packages/com.candidumgames.unitymmdtools/Shaders/AlphaDetector.compute";
        [SerializeField] private bool m_CreateAvatar = false;
        [SerializeField] private bool m_GenerateDebugData = false;
        [SerializeField] private List<VMDAnimation> m_VMDAnimations = new List<VMDAnimation>();
        [SerializeField] private float m_VMDFrameRate = 30.0f;
        [SerializeField] private bool m_VMDBakeIKToFK = true;
        [SerializeField] private bool m_VMDBakePhysicsToFK = false;
        [SerializeField] private float m_VMDPhysicsWarmUpDuration = 5.0f;

        /// <summary>
        /// Imports the <c>.pmx</c> asset: builds the model, materials, meshes, MMD runtime components, and optional
        /// avatar, registers them with the import context, and converts any attached VMD animations to clips.
        /// </summary>
        /// <param name="ctx">Asset import context for the source PMX file.</param>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            UMTTimingCollector timingCollector = new UMTTimingCollector();
            PMXImportOptions options = new PMXImportOptions
            {
                umtResources = PMXImportPipeline.LoadRequiredResources(),
                createAvatar = m_CreateAvatar,
                applyRenames = true,
                strictVersion = true,
                parent = null,
                loadTextures = (model, opts) => PMXProjectTextures.Load(model, ctx.assetPath),
                uvBlitShader = Shader.Find("Hidden/UMT/UVBlit"),
                alphaDetectorShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_AlphaDetectorShaderPath),
                timingCallback = timingCollector.RecordTiming,
            };

            PMXImportResult importResult = PMXImporter.Import(ctx.assetPath, options);
            PMXImportPipelineResult pipelineResult = PMXImportPipeline.Import(importResult, ctx.assetPath, ctx, m_GenerateDebugData);

            PMXModel model = importResult.model;
            if (model != null && m_VMDAnimations.Count > 0)
            {
                VMDAnimationClipOptions vmdOptions = new VMDAnimationClipOptions
                {
                    frameRate = Mathf.Max(1.0f, m_VMDFrameRate),
                    bakeIKToFK = m_VMDBakeIKToFK,
                    bakePhysicsToFK = m_VMDBakePhysicsToFK,
                    physicsSeed = 0,
                    physicsWarmUpDuration = Mathf.Max(0.0f, m_VMDPhysicsWarmUpDuration),
                    timingCallback = timingCollector.RecordTiming,
                };

                foreach (VMDAnimation vmdAnimation in m_VMDAnimations)
                {
                    if (vmdAnimation == null)
                    {
                        continue;
                    }

                    AnimationClip clip = VMDAnimationClipConverter.Convert(vmdAnimation, model, vmdOptions);
                    string clipName = string.IsNullOrEmpty(vmdAnimation.name)
                        ? "VMDClip"
                        : $"{vmdAnimation.name}_Clip";
                    clip.name = clipName;
                    ctx.AddObjectToAsset(clipName, clip);
                }
            }

            string timingReport = timingCollector.BuildReport("PMX Import Total");
            if (m_GenerateDebugData)
            {
                string timingLogPath = Path.ChangeExtension(ctx.assetPath, ".pmx.import.log");
                File.WriteAllText(timingLogPath, timingReport);
            }

            Debug.Log(
                $"[PMX Import] {ctx.assetPath}: model={pipelineResult.modelName}, " +
                $"meshes={pipelineResult.meshCount}, materials={pipelineResult.materialCount}, " +
                $"avatar={pipelineResult.hasAvatar}, renamed={pipelineResult.renameCount}, " +
                $"vmdClips={m_VMDAnimations.Count}\n" +
                timingReport);
        }
    }
}
