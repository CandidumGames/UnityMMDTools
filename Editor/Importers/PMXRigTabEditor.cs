using UnityEditor;

namespace UMT.Editor
{
    /// <summary>Draws the "Rig" tab of the <see cref="PMXScriptedImporter"/> inspector (humanoid avatar creation).</summary>
    internal sealed class PMXRigTabEditor
    {
        private SerializedProperty m_CreateAvatarProp;

        /// <summary>Caches the serialized properties shown on the Rig tab.</summary>
        public void OnEnable(SerializedObject serializedObject)
        {
            m_CreateAvatarProp = serializedObject.FindProperty("m_CreateAvatar");
        }

        /// <summary>Draws the Rig tab controls.</summary>
        public void OnGUI()
        {
            EditorGUILayout.PropertyField(m_CreateAvatarProp);
        }
    }
}
