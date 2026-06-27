using UnityEditor;

namespace UMT.Editor
{
    /// <summary>
    /// Shared editor progress-bar reporting for VMD-to-<see cref="UnityEngine.AnimationClip"/> conversion,
    /// translating <see cref="VMDAnimationClipConverter.Stage"/> updates into
    /// <see cref="EditorUtility.DisplayProgressBar(string, string, float)"/> calls.
    /// </summary>
    internal static class VMDClipProgress
    {
        /// <summary>Displays or updates the conversion progress bar for a single stage update.</summary>
        /// <param name="title">Progress bar window title.</param>
        /// <param name="itemLabel">Optional per-item label (for example the clip name) prefixed to the status text.</param>
        /// <param name="stage">Current conversion stage.</param>
        /// <param name="frame">Current frame index within the stage.</param>
        /// <param name="totalFrames">Total frames in the stage, or 0 when not frame-based.</param>
        public static void Report(string title, string itemLabel, VMDAnimationClipConverter.Stage stage, int frame, int totalFrames)
        {
            float progress = totalFrames > 0 ? (float)frame / totalFrames : 0.0f;
            string text = FormatProgressText(stage, frame, totalFrames);
            if (!string.IsNullOrEmpty(itemLabel))
            {
                text = $"{itemLabel} — {text}";
            }
            EditorUtility.DisplayProgressBar(title, text, progress);
        }

        private static string FormatProgressText(VMDAnimationClipConverter.Stage stage, int frame, int totalFrames)
        {
            string stageText = FormatStage(stage);
            if (totalFrames <= 0)
            {
                return stageText;
            }

            return $"{stageText}: frame {frame} / {totalFrames}";
        }

        private static string FormatStage(VMDAnimationClipConverter.Stage stage)
        {
            switch (stage)
            {
                case VMDAnimationClipConverter.Stage.Setup:
                    return "Setting up conversion";
                case VMDAnimationClipConverter.Stage.BoneConversion:
                    return "Converting bone animation";
                case VMDAnimationClipConverter.Stage.MorphConversion:
                    return "Converting morph animation";
                case VMDAnimationClipConverter.Stage.CameraConversion:
                    return "Converting camera animation";
                case VMDAnimationClipConverter.Stage.Finalization:
                    return "Finalizing animation";
                case VMDAnimationClipConverter.Stage.Complete:
                    return "Conversion complete";
                default:
                    return stage.ToString();
            }
        }
    }
}
