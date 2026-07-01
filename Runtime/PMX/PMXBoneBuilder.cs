using System;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Builds Unity bone transforms, their hierarchy, and skinning bindposes from a PMX model.
    /// </summary>
    public static class PMXBoneBuilder
    {
        /// <summary>
        /// Creates one <see cref="Transform"/> per PMX bone, parents them, and sets local positions.
        /// </summary>
        /// <param name="model">PMX model providing bone definitions.</param>
        /// <param name="root">Root transform that parentless bones are attached to.</param>
        /// <returns>The created bone transforms in PMX bone order.</returns>
        public static Transform[] BuildBones(PMXModel model, Transform root)
        {
            Transform[] bones = new Transform[model.bones.Length];
            for (int i = 0; i < model.bones.Length; ++i)
            {
                string renamedName = model.bones[i].renamedName.ToString();
                GameObject bone = new GameObject(PMXUtilities.GetGeneratedObjectName(renamedName, "Bone", i));
                bones[i] = bone.transform;
            }

            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone pmxBone = model.bones[i];
                bool hasParentBone = pmxBone.parentBoneIndex >= 0 && pmxBone.parentBoneIndex < bones.Length;
                Transform parent = hasParentBone ? bones[pmxBone.parentBoneIndex] : root;
                bones[i].SetParent(parent, false);
                bones[i].localPosition = hasParentBone ? pmxBone.position - model.bones[pmxBone.parentBoneIndex].position : pmxBone.position;
            }

            return bones;
        }

        /// <summary>
        /// Computes skinning bindpose matrices for the given bones relative to the root.
        /// </summary>
        /// <param name="root">Root transform the bindposes are expressed relative to.</param>
        /// <param name="bones">Bone transforms to compute bindposes for.</param>
        /// <returns>One bindpose matrix per bone, in bone order.</returns>
        public static Matrix4x4[] BuildBindposes(Transform root, Transform[] bones)
        {
            Matrix4x4[] bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; ++i)
            {
                bindposes[i] = bones[i].worldToLocalMatrix * root.localToWorldMatrix;
            }
            return bindposes;
        }

        /// <summary>
        /// Finds the first parentless (root) bone transform, falling back to the first bone.
        /// </summary>
        /// <param name="model">PMX model providing bone parent indices.</param>
        /// <param name="bones">Bone transforms corresponding to the model's bones.</param>
        /// <returns>The top-level bone transform, or null when there are no bones.</returns>
        public static Transform FindTopLevelBone(PMXModel model, Transform[] bones)
        {
            for (int i = 0; i < model.bones.Length; ++i)
            {
                if (model.bones[i].parentBoneIndex < 0)
                {
                    return bones[i];
                }
            }

            return bones.Length > 0 ? bones[0] : null;
        }
    }
}
