using System;
using System.Collections.Generic;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Optionally builds and assigns a humanoid <see cref="Avatar"/> for an imported PMX model by mapping PMX bones to Unity human bones using the configured avatar mapping resource.
    /// </summary>
    public static class PMXAvatarBuilder
    {
        [Serializable]
        private sealed class PMXHumanoidAvatarMappingConfig
        {
            public string[] requiredHumanBones = Array.Empty<string>();
            public PMXHumanBoneMapping[] mappings = Array.Empty<PMXHumanBoneMapping>();
        }

        [Serializable]
        private sealed class PMXHumanBoneMapping
        {
            public string humanBone = "";
            public string sourceBoneName = "";
        }

        private static readonly Dictionary<HumanBodyBones, string> s_HumanTraitBoneNames = BuildHumanTraitBoneNames();

        /// <summary>
        /// Builds a humanoid avatar for the model, adding an <see cref="Animator"/> and assigning the avatar when the required human bones can be mapped; otherwise returns a result without an avatar.
        /// </summary>
        /// <param name="model">PMX model providing bone names for mapping.</param>
        /// <param name="root">Root object that receives the <see cref="Animator"/> and avatar.</param>
        /// <param name="bones">Bone transforms corresponding to the model's bones.</param>
        /// <param name="modelName">Model name used to name the generated avatar.</param>
        /// <param name="umtResources">Resources providing the humanoid avatar mapping configuration.</param>
        /// <returns>The avatar build result, with <c>hasHumanoidAvatar</c> indicating success.</returns>
        public static PMXAvatarBuildResult Build(PMXModel model, GameObject root, Transform[] bones, string modelName, UMTResources umtResources)
        {
            bones ??= Array.Empty<Transform>();
            PMXAvatarBuildResult result = new PMXAvatarBuildResult();
            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            GameObject clonedRoot = GameObject.Instantiate(root, root.transform.parent, true);
            Transform[] clonedBones = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; ++i)
            {
                if (bones[i] != null)
                {
                    clonedBones[i] = clonedRoot.transform.Find(PMXUtilities.GetTransformPathFromRoot(bones[i], root.transform));
                }
            }

            animator.applyRootMotion = true;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.cullingMode = AnimatorCullingMode.CullCompletely;

            if (animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
            {
                result.hasHumanoidAvatar = true;
                result.avatar = animator.avatar;
                return result;
            }

            if (!TryBuildBoneMapping(model, clonedBones, umtResources, out GameObject[] mappedBones))
            {
                Debug.LogWarning($"PMX importer could not build a humanoid avatar for {modelName}: required humanoid bones were not found.");
                return result;
            }

            EnsureTPose(mappedBones);

            HumanDescription humanDescription = BuildHumanDescription(clonedRoot, mappedBones);
            Avatar avatar = AvatarBuilder.BuildHumanAvatar(clonedRoot, humanDescription);
            avatar.name = $"{modelName}_Avatar";

            if (!avatar.isValid || !avatar.isHuman)
            {
                Debug.LogWarning($"PMX importer built an invalid humanoid avatar for {modelName}.");
                PMXUtilities.DestroyRuntimeObject(clonedRoot);
                PMXUtilities.DestroyRuntimeObject(avatar);
                return result;
            }

            animator.avatar = avatar;
            result.hasHumanoidAvatar = true;
            result.avatar = avatar;
            PMXUtilities.DestroyRuntimeObject(clonedRoot);
            return result;
        }

        private static void EnsureTPose(GameObject[] mappedBones)
        {
            int leftUpperArmIndex = (int)HumanBodyBones.LeftUpperArm;
            int leftLowerArmIndex = (int)HumanBodyBones.LeftLowerArm;
            int rightUpperArmIndex = (int)HumanBodyBones.RightUpperArm;
            int rightLowerArmIndex = (int)HumanBodyBones.RightLowerArm;
            if (leftLowerArmIndex >= mappedBones.Length || leftUpperArmIndex >= mappedBones.Length || rightLowerArmIndex >= mappedBones.Length || rightUpperArmIndex >= mappedBones.Length)
            {
                return;
            }

            if (mappedBones[leftUpperArmIndex] == null || mappedBones[leftLowerArmIndex] == null || mappedBones[rightUpperArmIndex] == null || mappedBones[rightLowerArmIndex] == null)
            {
                return;
            }

            Vector3 left = (mappedBones[leftLowerArmIndex].transform.position - mappedBones[leftUpperArmIndex].transform.position).normalized;
            mappedBones[leftUpperArmIndex].transform.rotation = Quaternion.FromToRotation(left, Vector3.left) * mappedBones[leftUpperArmIndex].transform.rotation;

            Vector3 right = (mappedBones[rightLowerArmIndex].transform.position - mappedBones[rightUpperArmIndex].transform.position).normalized;
            mappedBones[rightUpperArmIndex].transform.rotation = Quaternion.FromToRotation(right, Vector3.right) * mappedBones[rightUpperArmIndex].transform.rotation;
        }

        /// <summary>
        /// Attempts to map PMX bones to Unity human bones using the configured mapping, succeeding only when all required human bones are present.
        /// </summary>
        /// <param name="model">PMX model providing bone names.</param>
        /// <param name="bones">Bone transforms corresponding to the model's bones.</param>
        /// <param name="umtResources">Resources providing the avatar mapping configuration.</param>
        /// <param name="mappedBones">On success, the mapped bone objects indexed by <see cref="HumanBodyBones"/>.</param>
        /// <returns>True when all required human bones were mapped; otherwise false.</returns>
        public static bool TryBuildBoneMapping(PMXModel model, Transform[] bones, UMTResources umtResources, out GameObject[] mappedBones)
        {
            mappedBones = Array.Empty<GameObject>();
            PMXHumanoidAvatarMappingConfig config = LoadMappingConfig(umtResources);
            if (config == null || config.mappings == null || config.mappings.Length == 0)
            {
                Debug.LogWarning("PMX humanoid avatar mapping resource is missing or empty.");
                return false;
            }

            mappedBones = BuildBoneMapping(model, bones, config.mappings);
            return HasRequiredBones(mappedBones, config.requiredHumanBones);
        }

        private static GameObject[] BuildBoneMapping(PMXModel model, Transform[] bones, IEnumerable<PMXHumanBoneMapping> mappings)
        {
            HashSet<Transform> usedTransforms = new HashSet<Transform>();
            GameObject[] mappedBones = new GameObject[(int)HumanBodyBones.LastBone];
            foreach (PMXHumanBoneMapping mapping in mappings)
            {
                if (!TryParseHumanBodyBone(mapping.humanBone, out HumanBodyBones humanBodyBone))
                {
                    continue;
                }

                Transform bone = FindHumanBoneTransform(model, bones, mapping.sourceBoneName, usedTransforms);
                if (bone == null)
                {
                    continue;
                }

                usedTransforms.Add(bone);
                mappedBones[(int)humanBodyBone] = bone.gameObject;
            }

            return mappedBones;
        }

        private static HumanBone[] BuildHumanBones(GameObject[] mappedBones)
        {
            List<HumanBone> humanBones = new List<HumanBone>();
            for (int i = 0; i < mappedBones.Length; ++i)
            {
                GameObject bone = mappedBones[i];
                if (bone == null || !s_HumanTraitBoneNames.TryGetValue((HumanBodyBones)i, out string humanName))
                {
                    continue;
                }

                humanBones.Add(new HumanBone
                {
                    boneName = bone.name,
                    humanName = humanName,
                    limit = new HumanLimit
                    {
                        useDefaultValues = true,
                    },
                });
            }

            return humanBones.ToArray();
        }

        private static HumanDescription BuildHumanDescription(GameObject root, GameObject[] mappedBones)
        {
            HumanDescription humanDescription = new HumanDescription
            {
                human = BuildHumanBones(mappedBones),
                skeleton = BuildSkeletonBones(root),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0.0f,
                hasTranslationDoF = false,
            };

            return humanDescription;
        }

        private static SkeletonBone[] BuildSkeletonBones(GameObject root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            SkeletonBone[] skeletonBones = new SkeletonBone[transforms.Length];
            for (int i = 0; i < transforms.Length; ++i)
            {
                Transform transform = transforms[i];
                skeletonBones[i] = new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale,
                };
            }

            return skeletonBones;
        }

        private static Transform FindHumanBoneTransform(PMXModel model, Transform[] bones, string sourceBoneName, HashSet<Transform> usedTransforms)
        {
            if (string.IsNullOrWhiteSpace(sourceBoneName))
            {
                return null;
            }

            for (int i = 0; i < bones.Length && i < model.bones.Length; ++i)
            {
                if (usedTransforms.Contains(bones[i]))
                {
                    continue;
                }

                if (model.bones[i].originalName.ToString() == sourceBoneName || model.bones[i].originalNameEN.ToString() == sourceBoneName || bones[i].name == sourceBoneName)
                {
                    return bones[i];
                }
            }

            return null;
        }

        private static bool HasRequiredBones(GameObject[] mappedBones, IEnumerable<string> requiredHumanBones)
        {
            foreach (string requiredHumanBone in requiredHumanBones ?? Array.Empty<string>())
            {
                if (!TryParseHumanBodyBone(requiredHumanBone, out HumanBodyBones humanBodyBone) || mappedBones[(int)humanBodyBone] == null)
                {
                    return false;
                }
            }

            return true;
        }

        private static PMXHumanoidAvatarMappingConfig LoadMappingConfig(UMTResources umtResources)
        {
            if (umtResources == null)
            {
                throw new ArgumentNullException(nameof(umtResources));
            }

            return JsonUtility.FromJson<PMXHumanoidAvatarMappingConfig>(umtResources.GetPMXHumanoidAvatarMappingsJson());
        }

        private static bool TryParseHumanBodyBone(string value, out HumanBodyBones bone)
        {
            return Enum.TryParse(value, true, out bone) && bone != HumanBodyBones.LastBone;
        }

        private static Dictionary<HumanBodyBones, string> BuildHumanTraitBoneNames()
        {
            Dictionary<HumanBodyBones, string> names = new Dictionary<HumanBodyBones, string>();
            for (int i = 0; i < HumanTrait.BoneName.Length; ++i)
            {
                string humanTraitName = HumanTrait.BoneName[i];
                HumanBodyBones bone = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), humanTraitName.Replace(" ", ""), true);
                names[bone] = humanTraitName;
            }
            return names;
        }
    }
}
