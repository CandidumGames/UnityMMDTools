using UnityEditor;
using UnityEditor.AssetImporters;

namespace UMT.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="PMXScriptedImporter"/> that splits the import options across
    /// Model, Rig, Animation, and Materials tabs, each drawn by its own editor.
    /// </summary>
    [CustomEditor(typeof(PMXScriptedImporter))]
    public sealed class PMXScriptedImporterEditor : ScriptedImporterEditor
    {
        private const int k_ModelTab = 0;
        private const int k_RigTab = 1;
        private const int k_AnimationTab = 2;
        private const int k_MaterialsTab = 3;
        private static readonly string[] k_TabLabels = { "Model", "Rig", "Animation", "Materials" };
        private static int s_CurrentTab = k_ModelTab;

        private readonly PMXModelTabEditor m_ModelTab = new PMXModelTabEditor();
        private readonly PMXRigTabEditor m_RigTab = new PMXRigTabEditor();
        private readonly PMXAnimationTabEditor m_AnimationTab = new PMXAnimationTabEditor();
        private readonly PMXMaterialRemapEditor m_MaterialTab = new PMXMaterialRemapEditor();

        /// <summary>
        /// Initializes the per-tab editors with the importer's serialized properties.
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            m_ModelTab.OnEnable(serializedObject);
            m_RigTab.OnEnable(serializedObject);
            m_AnimationTab.OnEnable(serializedObject);
            m_MaterialTab.OnEnable(serializedObject);
        }

        /// <summary>
        /// Draws the tab bar, the selected tab's controls, and the apply/revert controls.
        /// </summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            s_CurrentTab = UMTImporterTabBar.OnGUI(s_CurrentTab, k_TabLabels);
            EditorGUILayout.Space();

            switch (s_CurrentTab)
            {
                case k_MaterialsTab:
                    // The material remap editor commits the mode field and edits the external object map directly.
                    m_MaterialTab.OnGUI((PMXScriptedImporter)target, serializedObject);
                    break;
                case k_RigTab:
                    m_RigTab.OnGUI();
                    serializedObject.ApplyModifiedProperties();
                    break;
                case k_AnimationTab:
                    m_AnimationTab.OnGUI();
                    serializedObject.ApplyModifiedProperties();
                    break;
                default:
                    m_ModelTab.OnGUI();
                    serializedObject.ApplyModifiedProperties();
                    break;
            }

            EditorGUILayout.Space();

            ApplyRevertGUI();
        }
    }
}
