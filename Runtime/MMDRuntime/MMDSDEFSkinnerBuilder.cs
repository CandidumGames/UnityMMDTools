using System.Collections.Generic;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Attaches an <see cref="MMDSDEFSkinner"/> to each generated <see cref="SkinnedMeshRenderer"/> whose mesh contains SDEF vertices, wiring the per-vertex SDEF data, morph table, and bindposes needed by the GPU skinning pass.
    /// </summary>
    public static class MMDSDEFSkinnerBuilder
    {
        /// <summary>
        /// Builds one skinner per SDEF mesh under the model root and returns them. Returns an empty array when no mesh uses SDEF.
        /// </summary>
        /// <param name="model">Source PMX model providing the bone count.</param>
        /// <param name="meshes">Imported meshes carrying the SDEF skinning data and their renderers.</param>
        /// <returns>The created skinners, in mesh order.</returns>
        public static MMDSDEFSkinner[] Build(PMXModel model, IReadOnlyList<PMXImportedMesh> meshes)
        {
            List<MMDSDEFSkinner> skinners = new List<MMDSDEFSkinner>();

            foreach (PMXImportedMesh importedMesh in meshes)
            {
                if (!importedMesh.hasSDEF)
                {
                    continue;
                }

                SkinnedMeshRenderer renderer = importedMesh.renderer;
                if (renderer == null)
                {
                    continue;
                }

                MMDSDEFSkinner skinner = renderer.GetComponent<MMDSDEFSkinner>();
                if (skinner == null)
                {
                    skinner = renderer.gameObject.AddComponent<MMDSDEFSkinner>();
                }

                skinner.targetRenderer = renderer;
                skinner.model = model;
                skinner.bindposes = importedMesh.mesh.bindposes;
                skinner.sdefVertexData = importedMesh.sdefVertexData;
                skinner.morphTable = importedMesh.morphTable;
                skinner.hasTangent = importedMesh.hasTangent;

                // The mesh is GPU-deformed, so the renderer cannot derive its own deformed bounds; keep it from being culled incorrectly by updating bounds from the (static) bind-pose mesh each frame.
                renderer.updateWhenOffscreen = true;

                skinners.Add(skinner);
            }

            return skinners.ToArray();
        }
    }
}
