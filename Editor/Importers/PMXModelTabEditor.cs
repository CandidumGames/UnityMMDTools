using UnityEditor;

namespace UMT.Editor
{
    /// <summary>Draws the "Model" tab of the <see cref="PMXScriptedImporter"/> inspector.</summary>
    internal sealed class PMXModelTabEditor
    {
        private SerializedProperty m_GenerateDebugDataProp;

        /// <summary>Caches the serialized properties shown on the Model tab.</summary>
        public void OnEnable(SerializedObject serializedObject)
        {
            m_GenerateDebugDataProp = serializedObject.FindProperty("m_GenerateDebugData");
        }

        /// <summary>Draws the Model tab controls.</summary>
        public void OnGUI()
        {
            EditorGUILayout.PropertyField(m_GenerateDebugDataProp);
        }
    }
}
