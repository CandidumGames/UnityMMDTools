using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Shared toolbar-style tab bar for the UMT scripted-importer inspectors. Draws a centered, fixed-height row of tab buttons and returns the selected index.
    /// </summary>
    internal static class UMTImporterTabBar
    {
        private const float k_Height = 24.0f;

        /// <summary>
        /// Draws the tab bar and returns the index of the selected tab.
        /// </summary>
        /// <param name="current">Index of the currently selected tab.</param>
        /// <param name="labels">Tab labels in display order.</param>
        /// <returns>The selected tab index.</returns>
        public static int OnGUI(int current, string[] labels)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            int selected = GUILayout.Toolbar(current, labels, GUILayout.Height(k_Height));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return selected;
        }
    }
}
