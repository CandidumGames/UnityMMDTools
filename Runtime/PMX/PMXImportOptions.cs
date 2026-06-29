using System;
using System.Collections.Generic;
using UnityEngine;

namespace UMT
{
    /// <summary>Options controlling how a PMX file is parsed and built into Unity objects.</summary>
    public sealed class PMXImportOptions
    {
        /// <summary>Source PMX file path; set automatically by file-based import.</summary>
        public string sourcePath;
        /// <summary>Optional override name for the generated root object and assets.</summary>
        public string sourceName;
        /// <summary>Base directory used to resolve relative texture paths; defaults to the source file's folder.</summary>
        public string textureBaseDirectory;
        /// <summary>Optional parent transform for the generated root object.</summary>
        public Transform parent;
        /// <summary>When true, applies PMX renames before building objects and metadata.</summary>
        public bool applyRenames = true;
        /// <summary>UMT resources providing rename lists, romanization dictionaries, and avatar mappings.</summary>
        public UMTResources umtResources;
        /// <summary>Explicit rename lists; when null, lists are loaded from <see cref="umtResources"/>.</summary>
        public PMXRenameLists renameLists;
        /// <summary>Optional callback invoked with a stage label and its elapsed time for timing instrumentation.</summary>
        public Action<string, TimeSpan> timingCallback;
        /// <summary>When true, requires the PMX file version to match the supported version.</summary>
        public bool strictVersion = true;
        /// <summary>When true, attempts to build and assign a humanoid avatar.</summary>
        public bool createAvatar = true;
        /// <summary>Optional custom texture loader; when null, the default file-based loader is used.</summary>
        public Func<PMXModel, PMXImportOptions, Texture2D[]> loadTextures;
        /// <summary>Alpha-coverage value below which a material is treated as transparent.</summary>
        public float alphaDetectionThreshold = 0.99f;
        /// <summary>Alpha-coverage value at or above which a transparent material keeps Z-write enabled.</summary>
        public float alphaCoverageZWriteThreshold = 0.90f;
        /// <summary>
        /// Optional per-slot material overrides keyed by the generated material name (the sanitized slot key).
        /// When an entry exists for a slot, the builder uses that material directly instead of generating one.
        /// </summary>
        public Dictionary<string, Material> materialOverrides;
    }
}
