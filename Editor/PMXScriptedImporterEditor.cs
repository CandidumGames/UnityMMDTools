using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UMT.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="PMXScriptedImporter"/> that exposes avatar, debug, and VMD conversion options.
    /// </summary>
    [CustomEditor(typeof(PMXScriptedImporter))]
    public sealed class PMXScriptedImporterEditor : ScriptedImporterEditor
    {
        private SerializedProperty m_CreateAvatarProp;
        private SerializedProperty m_GenerateDebugDataProp;
        private SerializedProperty m_VMDAnimationsProp;
        private SerializedProperty m_VMDFrameRateProp;
        private SerializedProperty m_VMDBakeIKToFKProp;
        private SerializedProperty m_VMDBakePhysicsToFKProp;
        private SerializedProperty m_VMDPhysicsWarmUpDurationProp;

        /// <summary>
        /// Caches the serialized importer properties shown in the inspector.
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            m_CreateAvatarProp = serializedObject.FindProperty("m_CreateAvatar");
            m_GenerateDebugDataProp = serializedObject.FindProperty("m_GenerateDebugData");
            m_VMDAnimationsProp = serializedObject.FindProperty("m_VMDAnimations");
            m_VMDFrameRateProp = serializedObject.FindProperty("m_VMDFrameRate");
            m_VMDBakeIKToFKProp = serializedObject.FindProperty("m_VMDBakeIKToFK");
            m_VMDBakePhysicsToFKProp = serializedObject.FindProperty("m_VMDBakePhysicsToFK");
            m_VMDPhysicsWarmUpDurationProp = serializedObject.FindProperty("m_VMDPhysicsWarmUpDuration");
        }

        /// <summary>
        /// Draws the importer inspector, including the VMD conversion options and the apply/revert controls.
        /// </summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(m_CreateAvatarProp);
            EditorGUILayout.PropertyField(m_GenerateDebugDataProp);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("VMD Animation Conversion", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_VMDAnimationsProp);
            EditorGUILayout.PropertyField(m_VMDFrameRateProp);
            EditorGUILayout.PropertyField(m_VMDBakeIKToFKProp);
            if (m_VMDBakeIKToFKProp.boolValue)
            {
                EditorGUILayout.PropertyField(m_VMDBakePhysicsToFKProp);
                EditorGUILayout.PropertyField(m_VMDPhysicsWarmUpDurationProp);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            ApplyRevertGUI();
        }
    }
}
