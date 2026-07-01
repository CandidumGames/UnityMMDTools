using System.Collections.Generic;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Creates <see cref="SkinnedMeshRenderer"/> child objects for the imported meshes under the model root.
    /// </summary>
    public static class PMXRendererBuilder
    {
        /// <summary>
        /// Builds a skinned renderer per imported mesh, wiring materials, bones, and the root bone.
        /// </summary>
        /// <param name="model">PMX model used to locate the top-level bone.</param>
        /// <param name="root">Root object the renderer children are parented to.</param>
        /// <param name="meshes">Imported meshes to create renderers for.</param>
        /// <param name="materials">Materials indexed by PMX material index.</param>
        /// <param name="bones">Bone transforms bound to the skinned renderers.</param>
        public static void Build(PMXModel model, GameObject root, IReadOnlyList<PMXImportedMesh> meshes, IReadOnlyList<Material> materials, Transform[] bones)
        {
            Transform rootBone = PMXBoneBuilder.FindTopLevelBone(model, bones);

            foreach (PMXImportedMesh importedMesh in meshes)
            {
                GameObject child = new GameObject(importedMesh.name);
                child.transform.SetParent(root.transform, false);

                SkinnedMeshRenderer renderer = child.AddComponent<SkinnedMeshRenderer>();
                importedMesh.renderer = renderer;
                renderer.sharedMesh = importedMesh.mesh;
                Material[] sharedMaterials = new Material[importedMesh.materialIndices.Length];
                for (int i = 0; i < importedMesh.materialIndices.Length; ++i)
                {
                    int materialIndex = importedMesh.materialIndices[i];
                    sharedMaterials[i] = materialIndex >= 0 && materialIndex < materials.Count ? materials[materialIndex] : null;
                }
                renderer.sharedMaterials = sharedMaterials;

                if (bones.Length > 0)
                {
                    renderer.bones = bones;
                    renderer.rootBone = rootBone;
                }

                renderer.localBounds = importedMesh.mesh.bounds;
            }
        }
    }
}
