using UnityEditor;

namespace UMT.Editor
{
    /// <summary>
    /// Draws the "Animation" tab of the <see cref="PMXScriptedImporter"/> inspector (VMD animation conversion).
    /// </summary>
    internal sealed class PMXAnimationTabEditor
    {
        private SerializedProperty m_VMDAnimationsProp;
        private SerializedProperty m_VMDFrameRateProp;
        private SerializedProperty m_VMDBakeIKToFKProp;
        private SerializedProperty m_VMDBakePhysicsToFKProp;
        private SerializedProperty m_VMDPhysicsWarmUpDurationProp;

        /// <summary>
        /// Caches the serialized properties shown on the Animation tab.
        /// </summary>
        public void OnEnable(SerializedObject serializedObject)
        {
            m_VMDAnimationsProp = serializedObject.FindProperty("m_VMDAnimations");
            m_VMDFrameRateProp = serializedObject.FindProperty("m_VMDFrameRate");
            m_VMDBakeIKToFKProp = serializedObject.FindProperty("m_VMDBakeIKToFK");
            m_VMDBakePhysicsToFKProp = serializedObject.FindProperty("m_VMDBakePhysicsToFK");
            m_VMDPhysicsWarmUpDurationProp = serializedObject.FindProperty("m_VMDPhysicsWarmUpDuration");
        }

        /// <summary>
        /// Draws the Animation tab controls.
        /// </summary>
        public void OnGUI()
        {
            EditorGUILayout.LabelField("VMD Animation Conversion", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_VMDAnimationsProp);
            EditorGUILayout.PropertyField(m_VMDFrameRateProp);
            EditorGUILayout.PropertyField(m_VMDBakeIKToFKProp);
            if (m_VMDBakeIKToFKProp.boolValue)
            {
                EditorGUILayout.PropertyField(m_VMDBakePhysicsToFKProp);
                EditorGUILayout.PropertyField(m_VMDPhysicsWarmUpDurationProp);
            }
        }
    }
}
