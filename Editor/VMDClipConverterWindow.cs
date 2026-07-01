using UMT;
using Newtonsoft.Json;
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Editor window (<c>Tools/UMT/VMD Clip Converter</c>) that converts a VMD file into an <see cref="AnimationClip"/> for a selected PMX prefab and model, supporting runtime-solved or baked-to-FK IK conversion.
    /// </summary>
    public sealed class VMDClipConverterWindow : EditorWindow
    {
        private enum ConversionMode
        {
            IKRuntimeSolved,
            IKBakedToFK,
        }

        private enum ConversionTarget
        {
            Motion,
            Camera,
        }

        private enum CameraFrameRate
        {
            [InspectorName("30 fps")] FPS30 = 30,
            [InspectorName("60 fps")] FPS60 = 60,
            [InspectorName("120 fps")] FPS120 = 120,
        }

        private string m_VMDPath;
        private PMXModel m_PMXModel;
        private string m_OutputPath;
        private ConversionTarget m_ConversionTarget = ConversionTarget.Motion;
        private ConversionMode m_ConversionMode = ConversionMode.IKRuntimeSolved;
        private CameraFrameRate m_CameraFrameRate = CameraFrameRate.FPS30;
        private bool m_BakePhysicsToFK = new VMDAnimationClipOptions().bakePhysicsToFK;
        private int m_PhysicsSeed = checked((int)new VMDAnimationClipOptions().physicsSeed);
        private float m_PhysicsSetupDuration = new VMDAnimationClipOptions().physicsWarmUpDuration;
        private Vector2 m_Scroll;
        private string m_Log = "";

        /// <summary>
        /// Opens (or focuses) the VMD Clip Converter editor window.
        /// </summary>
        [MenuItem("Tools/UMT/VMD Clip Converter")]
        public static void Open()
        {
            GetWindow<VMDClipConverterWindow>("VMD Clip Converter");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(m_VMDPath))
            {
                m_VMDPath = GetSelectedAssetWithExtension(".vmd");
            }
        }

        private void OnGUI()
        {

            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            m_ConversionTarget = (ConversionTarget)EditorGUILayout.EnumPopup("Target", m_ConversionTarget);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_VMDPath = EditorGUILayout.TextField("VMD Path", string.IsNullOrEmpty(m_VMDPath) ? GetDefaultOutputPath() : m_VMDPath);
                if (GUILayout.Button("...", GUILayout.Width(32)))
                {
                    PickInputVMDPath();
                }
            }
            if (m_ConversionTarget == ConversionTarget.Motion)
            {
                m_PMXModel = (PMXModel)EditorGUILayout.ObjectField("PMX Model", m_PMXModel, typeof(PMXModel), false);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_OutputPath = EditorGUILayout.TextField("Animation Clip", string.IsNullOrEmpty(m_OutputPath) ? GetDefaultOutputPath() : m_OutputPath);
                if (GUILayout.Button("...", GUILayout.Width(32)))
                {
                    PickOutputPath();
                }
            }

            using (new EditorGUI.DisabledScope(!CanConvert()))
            {
                if (GUILayout.Button("Convert VMD", GUILayout.Height(32)))
                {
                    ConvertPicked();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            if (m_ConversionTarget == ConversionTarget.Motion)
            {
                m_ConversionMode = (ConversionMode)EditorGUILayout.EnumPopup("Conversion Mode", m_ConversionMode);
                if (m_ConversionMode == ConversionMode.IKBakedToFK)
                {
                    m_BakePhysicsToFK = EditorGUILayout.Toggle("Bake Physics To FK", m_BakePhysicsToFK);
                    m_PhysicsSeed = EditorGUILayout.IntField("Physics Seed", m_PhysicsSeed);
                    m_PhysicsSetupDuration = EditorGUILayout.FloatField("Physics Setup Duration (s)", m_PhysicsSetupDuration);
                }
            }
            else
            {
                m_CameraFrameRate = (CameraFrameRate)EditorGUILayout.EnumPopup("Frame Rate", m_CameraFrameRate);
                EditorGUILayout.HelpBox("Camera VMD converts to a clip targeting a camera rig: the root carries center movement and rotation; a child \"Camera\" carries the distance offset and field of view.", MessageType.Info);
                if (GUILayout.Button("Create Camera Rig In Scene"))
                {
                    CreateCameraRig();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField(m_Log, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        private void ConvertPicked()
        {
            if (m_ConversionTarget == ConversionTarget.Camera)
            {
                ConvertCameraPicked();
                return;
            }

            ConvertMotionPicked();
        }

        private void ConvertMotionPicked()
        {
            string vmdPath = m_VMDPath;
            string outputPath = string.IsNullOrEmpty(m_OutputPath) ? GetDefaultOutputPath() : m_OutputPath;
            UMTTimingCollector timingCollector = new UMTTimingCollector();
            VMDAnimation animation;
            EditorUtility.DisplayProgressBar("VMD Clip Converter", "Reading and parsing VMD", 0.0f);
            using (UMTTiming.Measure(timingCollector.RecordTiming, "Read and Parse VMD"))
            {
                byte[] bytes = File.ReadAllBytes(vmdPath);
                animation = VMDReader.Read(bytes);
            }

            VMDAnimationClipOptions options = new VMDAnimationClipOptions
            {
                bakeIKToFK = m_ConversionMode == ConversionMode.IKBakedToFK,
                bakePhysicsToFK = m_ConversionMode == ConversionMode.IKBakedToFK && m_BakePhysicsToFK,
                physicsSeed = checked((uint)Mathf.Max(0, m_PhysicsSeed)),
                physicsWarmUpDuration = Mathf.Max(0.0f, m_PhysicsSetupDuration),
                timingCallback = timingCollector.RecordTiming,
            };

            VMDModelClipData clipData = VMDAnimationClipConverter.Convert(animation, m_PMXModel, options, ReportConvertProgress);
            AnimationClip generatedClip = VMDClipDataBuilder.BuildAnimationClip(clipData, options.frameRate);
            EditorUtility.DisplayProgressBar("VMD Clip Converter", "Saving AnimationClip", 0.95f);
            using (UMTTiming.Measure(timingCollector.RecordTiming, "Asset Saving"))
            {
                SaveClip(generatedClip, outputPath);
            }

            EditorUtility.ClearProgressBar();
            m_OutputPath = outputPath;
            string timingReport = timingCollector.BuildReport("VMD Conversion Total");
            m_Log = $"Created AnimationClip: {outputPath}\nMode: {FormatConversionMode(m_ConversionMode)}\nBones: {animation.boneFrames.Length}\nMorphs: {animation.morphFrames.Length}\nShow/IK frames: {animation.showIKFrames.Length}\nBake IK To FK: {options.bakeIKToFK}\nBake Physics To FK: {options.bakePhysicsToFK}\nPhysics Seed: {options.physicsSeed}\n{timingReport}";
            Debug.Log($"[VMD Clip Converter] Created AnimationClip: {outputPath}\nMode: {FormatConversionMode(m_ConversionMode)}\n{timingReport}");
        }

        private void ConvertCameraPicked()
        {
            string vmdPath = m_VMDPath;
            string outputPath = string.IsNullOrEmpty(m_OutputPath) ? GetDefaultOutputPath() : m_OutputPath;
            UMTTimingCollector timingCollector = new UMTTimingCollector();
            VMDAnimation animation;
            EditorUtility.DisplayProgressBar("VMD Clip Converter", "Reading and parsing VMD", 0.0f);
            using (UMTTiming.Measure(timingCollector.RecordTiming, "Read and Parse VMD"))
            {
                byte[] bytes = File.ReadAllBytes(vmdPath);
                animation = VMDReader.Read(bytes);
            }

            float cameraFrameRate = (float)(int)m_CameraFrameRate;
            VMDCameraClipData cameraData = VMDAnimationClipConverter.ConvertCamera(animation, frameRate: cameraFrameRate, timingCallback: timingCollector.RecordTiming, progress: ReportConvertProgress);
            AnimationClip generatedClip = VMDClipDataBuilder.BuildCameraAnimationClip(cameraData, cameraFrameRate);
            EditorUtility.DisplayProgressBar("VMD Clip Converter", "Saving AnimationClip", 0.95f);
            using (UMTTiming.Measure(timingCollector.RecordTiming, "Asset Saving"))
            {
                SaveClip(generatedClip, outputPath);
            }

            EditorUtility.ClearProgressBar();
            m_OutputPath = outputPath;
            string timingReport = timingCollector.BuildReport("VMD Camera Conversion Total");
            m_Log = $"Created Camera AnimationClip: {outputPath}\nFrame rate: {(int)m_CameraFrameRate} fps\nCamera frames: {animation.cameraFrames.Length}\nLight frames: {animation.lightFrames.Length}\n{timingReport}";
            Debug.Log($"[VMD Clip Converter] Created Camera AnimationClip: {outputPath}\n{timingReport}");
        }

        private static void SaveClip(AnimationClip generatedClip, string outputPath)
        {
            generatedClip.name = Path.GetFileNameWithoutExtension(outputPath);

            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
            if (clip == null)
            {
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new[] { generatedClip }, outputPath, false);
                clip = generatedClip;
            }
            else
            {
                EditorUtility.CopySerialized(generatedClip, clip);
                EditorUtility.SetDirty(clip);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(outputPath);
        }

        private void CreateCameraRig()
        {
            string cameraChildName = VMDAnimationClipConverter.k_DefaultCameraChildName;
            GameObject root = new GameObject("VMD Camera Rig");
            root.AddComponent<Animator>();
            GameObject cameraTargetObject = new GameObject(VMDAnimationClipConverter.k_DefaultCameraTargetName);
            cameraTargetObject.transform.SetParent(root.transform, false);
            GameObject cameraObject = new GameObject(cameraChildName);
            cameraObject.transform.SetParent(cameraTargetObject.transform, false);
            cameraObject.AddComponent<Camera>();

            Undo.RegisterCreatedObjectUndo(root, "Create VMD Camera Rig");
            Selection.activeGameObject = root;
            m_Log = $"Created camera rig '{root.name}' with child '{cameraChildName}'. Add the converted camera clip to the rig's Animator/Animation to play it.";
        }

        private bool CanConvert()
        {
            if (string.IsNullOrEmpty(m_VMDPath))
            {
                return false;
            }
            if (!string.Equals(Path.GetExtension(m_VMDPath), ".vmd", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (m_ConversionTarget == ConversionTarget.Motion)
            {
                if (m_PMXModel == null)
                {
                    return false;
                }
            }
            return !string.IsNullOrEmpty(string.IsNullOrEmpty(m_OutputPath) ? GetDefaultOutputPath() : m_OutputPath);
        }

        private void PickInputVMDPath()
        {
            string directory = Path.GetDirectoryName(m_VMDPath)?.Replace('\\', '/') ?? "Assets";
            string name = Path.GetFileName(m_VMDPath);
            string picked = EditorUtility.OpenFilePanel("Load VMD file", directory, "vmd");
            if (!string.IsNullOrEmpty(picked))
            {
                m_VMDPath = picked;
            }
        }
        private void PickOutputPath()
        {
            string defaultPath = GetDefaultOutputPath();
            string directory = Path.GetDirectoryName(defaultPath)?.Replace('\\', '/') ?? "Assets";
            string name = Path.GetFileName(defaultPath);
            string picked = EditorUtility.SaveFilePanelInProject("Save VMD AnimationClip", name, "anim", "Choose an AnimationClip output path.", directory);
            if (!string.IsNullOrEmpty(picked))
            {
                m_OutputPath = picked;
            }
        }

        private string GetDefaultOutputPath()
        {
            string vmdPath = m_VMDPath;
            if (m_ConversionTarget == ConversionTarget.Camera)
            {
                if (string.IsNullOrEmpty(vmdPath))
                {
                    return "Assets/VMDCamera.anim";
                }

                return $"Assets/{Path.GetFileNameWithoutExtension(vmdPath)}_Camera.anim";
            }

            string modelPath = AssetDatabase.GetAssetPath(m_PMXModel);
            if (string.IsNullOrEmpty(vmdPath) || string.IsNullOrEmpty(modelPath))
            {
                return "Assets/VMDAnimation.anim";
            }

            string directory = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? "Assets";
            string vmdName = Path.GetFileNameWithoutExtension(vmdPath);
            string modelName = Path.GetFileNameWithoutExtension(modelPath);
            return $"{directory}/{vmdName}_{modelName}.anim";
        }

        private static string GetSelectedAssetWithExtension(string extension)
        {
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }

        private static string FormatConversionMode(ConversionMode mode)
        {
            switch (mode)
            {
                case ConversionMode.IKRuntimeSolved:
                    return "IK runtime-solved";
                case ConversionMode.IKBakedToFK:
                    return "Baked FK";
                default:
                    return mode.ToString();
            }
        }

        private static void ReportConvertProgress(VMDAnimationClipConverter.Stage stage, int frame, int totalFrames)
        {
            VMDClipProgress.Report("VMD Clip Converter", null, stage, frame, totalFrames);
        }
    }
}