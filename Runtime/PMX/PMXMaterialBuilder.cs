using B83.Image.BMP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace UMT
{
    /// <summary>
    /// Builds Unity materials from PMX materials, assigning textures by index, detecting alpha coverage for
    /// transparency, and configuring lilToon, URP Unlit, or built-in Unlit shaders in that preference order.
    /// </summary>
    public static class PMXMaterialBuilder
    {
        private const string k_LilToonMultiShaderName = "_lil/lilToonMulti";
        // lilToon's "Multi" shader has no outline pass; the outline pass only exists in the separate
        // outline variant. lilToon itself swaps to this shader when _UseOutline is enabled.
        private const string k_LilToonMultiOutlineShaderName = "Hidden/lilToonMultiOutline";
        private const string k_URPUnlitFallbackShaderName = "Universal Render Pipeline/Unlit";
        private const string k_BuiltInUnlitShaderName = "Unlit/Texture";
        private const string k_BuiltInUnlitTransparentShaderName = "Unlit/Transparent";

        private static readonly int s_BaseColorProperty = Shader.PropertyToID("_Color");
        private static readonly int s_BaseTextureProperty = Shader.PropertyToID("_MainTex");
        private static readonly int s_ShadowColorProperty = Shader.PropertyToID("_ShadowColor");
        private static readonly int s_ShadowReceiveProperty = Shader.PropertyToID("_ShadowReceive");
        private static readonly int s_ShadowBorderProperty = Shader.PropertyToID("_ShadowBorder");
        private static readonly int s_lilShadowCasterBiasProperty = Shader.PropertyToID("_lilShadowCasterBias");
        private static readonly int s_Shadow2ndColorProperty = Shader.PropertyToID("_Shadow2ndColor");
        private static readonly int s_ShadowBorderColorProperty = Shader.PropertyToID("_ShadowBorderColor");
        private static readonly int s_ShadowColorTextureProperty = Shader.PropertyToID("_ShadowColorTex");
        private static readonly int s_UseShadowProperty = Shader.PropertyToID("_UseShadow");
        private static readonly int s_DoubleSidedProperty = Shader.PropertyToID("_DoubleSided");
        private static readonly int s_CullModeProperty = Shader.PropertyToID("_Cull");
        private static readonly int s_UseOutlineProperty = Shader.PropertyToID("_UseOutline");
        private static readonly int s_OutlineWidthProperty = Shader.PropertyToID("_OutlineWidth");
        private static readonly int s_OutlineColorProperty = Shader.PropertyToID("_OutlineColor");
        private static readonly int s_OutlineEnableLightingProperty = Shader.PropertyToID("_OutlineEnableLighting");
        private static readonly int s_MatcapTextureProperty = Shader.PropertyToID("_MatcapTex");
        private static readonly int s_MatcapColorProperty = Shader.PropertyToID("_MatcapColor");
        private static readonly int s_LilToonMatcapTextureProperty = Shader.PropertyToID("_MatCapTex");
        private static readonly int s_LilToonMatcapColorProperty = Shader.PropertyToID("_MatCapColor");
        private static readonly int s_UseMatcapProperty = Shader.PropertyToID("_UseMatCap");
        private static readonly int s_LilToonTransparentModeProperty = Shader.PropertyToID("_TransparentMode");
        private static readonly int s_ShadingToonyProperty = Shader.PropertyToID("_ShadingToonyFactor");
        private static readonly int s_ShadingShiftProperty = Shader.PropertyToID("_ShadingShiftFactor");
        private static readonly int s_GiEqualizationProperty = Shader.PropertyToID("_GiEqualization");
        private static readonly int s_EditModeProperty = Shader.PropertyToID("_M_EditMode");
        private static readonly int s_URPSurfaceProperty = Shader.PropertyToID("_Surface");
        private static readonly int s_URPBlendProperty = Shader.PropertyToID("_Blend");
        private static readonly int s_URPSrcBlendProperty = Shader.PropertyToID("_SrcBlend");
        private static readonly int s_URPDstBlendProperty = Shader.PropertyToID("_DstBlend");
        private static readonly int s_URPSrcBlendAlphaProperty = Shader.PropertyToID("_SrcBlendAlpha");
        private static readonly int s_URPDstBlendAlphaProperty = Shader.PropertyToID("_DstBlendAlpha");
        private static readonly int s_URPZWriteProperty = Shader.PropertyToID("_ZWrite");
        private static readonly int s_AlphaToMaskProperty = Shader.PropertyToID("_AlphaToMask");
        private static readonly int s_OutlineSrcBlendProperty = Shader.PropertyToID("_OutlineSrcBlend");
        private static readonly int s_OutlineDstBlendProperty = Shader.PropertyToID("_OutlineDstBlend");
        private static readonly int s_OutlineSrcBlendAlphaProperty = Shader.PropertyToID("_OutlineSrcBlendAlpha");
        private static readonly int s_OutlineDstBlendAlphaProperty = Shader.PropertyToID("_OutlineDstBlendAlpha");
        private static readonly int s_OutlineZWriteProperty = Shader.PropertyToID("_OutlineZWrite");
        private static readonly int s_OutlineAlphaToMaskProperty = Shader.PropertyToID("_OutlineAlphaToMask");
        private const string k_URPSurfaceTypeTransparentKeyword = "_SURFACE_TYPE_TRANSPARENT";
        private const string k_LilToonRequireUV2Keyword = "_REQUIRE_UV2";
        private const string k_UnityUIClipRectKeyword = "UNITY_UI_CLIP_RECT";

        // Final outline thickness in Unity meters per unit of PMX edge size. ~0.03 PMX units scaled by
        // the MMD-to-Unity unit factor (0.08) lands near this value, giving an MMD-like thin edge.
        private const float k_OutlineWidthScale = 0.0025f;
        // lilToon multiplies _OutlineWidth by 0.01 internally (see lilGetOutlineWidth), so the slider unit
        // is 1.0 == 1cm of object-space displacement. Convert our meter width back into that slider unit.
        private const float k_LilToonOutlineWidthUnit = 0.01f;

        /// <summary>Builds one Unity material per PMX material, resolving textures and transparency.</summary>
        /// <param name="model">PMX model providing material definitions.</param>
        /// <param name="options">Import options providing alpha-detection shaders and thresholds.</param>
        /// <param name="modelName">Model name (used for context; not embedded in material names).</param>
        /// <param name="texturesByIndex">Loaded textures indexed by PMX texture index.</param>
        /// <returns>The generated materials in PMX material order.</returns>
        public static List<Material> Build(PMXModel model, PMXImportOptions options, string modelName, Texture2D[] texturesByIndex)
        {
            List<Material> materials = new List<Material>(model.materials.Length);
            // The imported texture assets in texturesByIndex may be compressed and/or non-readable (editor
            // import settings), which corrupts CPU alpha sampling. Decode the original source files into raw
            // Color32 pixels instead, cached per resolved path for the duration of this build.
            SourcePixelCache sourcePixels = new SourcePixelCache(model, options);
            int indicesOffset = 0;
            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial pmxMaterial = model.materials[i];
                int faceIndexStart = indicesOffset;
                int faceIndexCount = pmxMaterial.faceIndexCount;
                indicesOffset += faceIndexCount;

                Texture2D mainTexture = GetTexture(texturesByIndex, pmxMaterial.textureIndex);
                Texture2D sphereTexture = GetTexture(texturesByIndex, pmxMaterial.sphereTextureIndex);
                string renamedName = pmxMaterial.renamedName.ToString();
                string materialName = PMXUtilities.SanitizeFileName(renamedName, i);

                if (options.materialOverrides != null
                    && options.materialOverrides.TryGetValue(materialName, out Material overrideMaterial)
                    && overrideMaterial != null)
                {
                    materials.Add(overrideMaterial);
                    continue;
                }

                SourcePixels mainPixels = sourcePixels.Get(pmxMaterial.textureIndex);
                (bool isBelowAlphaCoverageThreshold, float alphaCoverage, bool anyPixelOpaque) alphaDetection = DetectAlphaCoverage(
                        mainPixels,
                        model,
                        faceIndexStart,
                        faceIndexCount,
                        options.alphaDetectionThreshold);
                bool shouldBeTransparent = alphaDetection.isBelowAlphaCoverageThreshold || pmxMaterial.diffuse.a < 1.0f;
                float alphaCoverage = Mathf.Min(alphaDetection.alphaCoverage, pmxMaterial.diffuse.a);
                bool anyPixelOpaque = alphaDetection.anyPixelOpaque && !(pmxMaterial.diffuse.a < 1.0f);
                bool transparentWithZWrite = anyPixelOpaque || alphaCoverage >= options.alphaCoverageZWriteThreshold;
                Material material = BuildMaterial(pmxMaterial, mainTexture, sphereTexture, materialName, shouldBeTransparent, transparentWithZWrite);

                material.renderQueue = shouldBeTransparent ? 3000 + i : 2500 + i;

                materials.Add(material);
            }
            return materials;
        }

        private static bool IsFaceMaterial(PMXMaterial pmxMaterial)
        {
            string[] faceHeuristicName =
            {
                "face",
                "skin",
                "body",
                "head",
                "kao",
                "kawa",
                "hifu",
                "hada",
                "lian",
            };
            return HeuristicDetect(pmxMaterial.renamedName.ToString(), faceHeuristicName);
        }

        private static bool IsEyeMaterial(PMXMaterial pmxMaterial)
        {
            string[] faceHeuristicName =
            {
                "eye",
                "iris",
                "hitomi",
                "yan",
            };
            return HeuristicDetect(pmxMaterial.renamedName.ToString(), faceHeuristicName);
        }

        private static bool HeuristicDetect(string name, string[] heuristics)
        {
            string lowerName = name.ToLowerInvariant();
            foreach (string heuristic in heuristics)
            {
                if (lowerName.Contains(heuristic))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ConfigureLilToonShading(Material material, PMXMaterial pmxMaterial, Texture2D mainTexture, Texture2D sphereTexture, bool isDoubleSided, bool drawsEdge)
        {
            Color ambientColor = new Color(pmxMaterial.ambient.x, pmxMaterial.ambient.y, pmxMaterial.ambient.z, 1.0f);
            Color borderColor = new Color(
                (pmxMaterial.diffuse.r + pmxMaterial.ambient.x) * 0.5f,
                (pmxMaterial.diffuse.g + pmxMaterial.ambient.y) * 0.5f,
                (pmxMaterial.diffuse.b + pmxMaterial.ambient.z) * 0.5f,
                1.0f);
            bool isEyeMaterial = IsEyeMaterial(pmxMaterial);
            bool isFaceMaterial = IsFaceMaterial(pmxMaterial);
            SetColorIfPresent(material, s_ShadowColorProperty, ambientColor);
            SetFloatIfPresent(material, s_ShadowReceiveProperty, 1.0f);
            SetFloatIfPresent(material, s_lilShadowCasterBiasProperty, isFaceMaterial || isEyeMaterial ? 0.05f : 0.0f);
            if (isFaceMaterial)
            {
                SetFloatIfPresent(material, s_ShadowBorderProperty, 0.3f);
            }
            else if (isEyeMaterial)
            {
                SetFloatIfPresent(material, s_ShadowBorderProperty, 0.1f);
            }
            else
            {
                SetFloatIfPresent(material, s_ShadowBorderProperty, 0.5f);
            }
            SetColorIfPresent(material, s_Shadow2ndColorProperty, new Color(ambientColor.r, ambientColor.g, ambientColor.b, 0.0f));
            SetColorIfPresent(material, s_ShadowBorderColorProperty, borderColor);
            SetFloatIfPresent(material, s_UseShadowProperty, 1.0f);
            SetFloatIfPresent(material, s_ShadingToonyProperty, 0.95f);
            SetFloatIfPresent(material, s_ShadingShiftProperty, -0.05f);
            SetFloatIfPresent(material, s_GiEqualizationProperty, 0.0f);
            SetFloatIfPresent(material, s_EditModeProperty, 1.0f);

            if (mainTexture != null)
            {
                SetTextureIfPresent(material, s_ShadowColorTextureProperty, mainTexture);
            }

            if (pmxMaterial.sphereTextureMode != PMXMaterial.SphereTextureMode.None && sphereTexture != null)
            {
                SetTextureIfPresent(material, s_MatcapTextureProperty, sphereTexture);
                SetColorIfPresent(material, s_MatcapColorProperty, Color.white);
                SetTextureIfPresent(material, s_LilToonMatcapTextureProperty, sphereTexture);
                SetColorIfPresent(material, s_LilToonMatcapColorProperty, Color.white);
                SetFloatIfPresent(material, s_UseMatcapProperty, 1.0f);
            }

            SetFloatIfPresent(material, s_DoubleSidedProperty, isDoubleSided ? 1.0f : 0.0f);

            // The outline pass itself lives in the outline shader variant (selected in GetShader). These
            // properties only take effect when that variant is assigned. _UseOutline keeps lilToon's own
            // editor swap logic consistent if the material is later re-set up.
            SetFloatIfPresent(material, s_UseOutlineProperty, drawsEdge ? 1.0f : 0.0f);
            SetFloatIfPresent(material, s_OutlineWidthProperty, drawsEdge ? pmxMaterial.edgeSize * k_OutlineWidthScale / k_LilToonOutlineWidthUnit : 0.0f);
            SetColorIfPresent(material, s_OutlineColorProperty, pmxMaterial.edgeColor);
            // MMD edges are flat: keep the outline color unaffected by scene lighting.
            SetFloatIfPresent(material, s_OutlineEnableLightingProperty, 0.0f);
            SetKeywordIfPresent(material, k_LilToonRequireUV2Keyword, true);
        }

        private static void ConfigureLilToonTransparent(Material material, bool transparentWithZWrite)
        {
            SetFloatIfPresent(material, s_LilToonTransparentModeProperty, 2.0f);
            SetFloatIfPresent(material, s_URPSrcBlendProperty, (float)BlendMode.One);
            SetFloatIfPresent(material, s_URPDstBlendProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_URPSrcBlendAlphaProperty, (float)BlendMode.One);
            SetFloatIfPresent(material, s_URPDstBlendAlphaProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_AlphaToMaskProperty, 0.0f);
            SetFloatIfPresent(material, s_OutlineSrcBlendProperty, (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, s_OutlineDstBlendProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_OutlineSrcBlendAlphaProperty, (float)BlendMode.One);
            SetFloatIfPresent(material, s_OutlineDstBlendAlphaProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_OutlineZWriteProperty, transparentWithZWrite ? 1.0f : 0.0f);
            SetFloatIfPresent(material, s_OutlineAlphaToMaskProperty, 0.0f);

            SetFloatIfPresent(material, s_URPSurfaceProperty, 1.0f);
            SetFloatIfPresent(material, s_URPBlendProperty, 0.0f);
            SetFloatIfPresent(material, s_URPZWriteProperty, transparentWithZWrite ? 1.0f : 0.0f);

            SetKeywordIfPresent(material, k_UnityUIClipRectKeyword, true);
        }

        private static void ConfigureURPTransparent(Material material, bool transparentWithZWrite)
        {
            SetFloatIfPresent(material, s_URPSurfaceProperty, 1.0f);
            SetFloatIfPresent(material, s_URPBlendProperty, 0.0f);
            SetFloatIfPresent(material, s_URPSrcBlendProperty, (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, s_URPDstBlendProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_URPZWriteProperty, transparentWithZWrite ? 1.0f : 0.0f);
            material.SetOverrideTag("RenderType", "Transparent");
            SetKeywordIfPresent(material, k_URPSurfaceTypeTransparentKeyword, true);
        }

        private static void ConfigureBuiltInTransparent(Material material, bool transparentWithZWrite)
        {
            // Built-in uses a dedicated Unlit/Transparent shader, so blending is already
            // baked into the shader; only the render order needs to be set explicitly.
            material.SetOverrideTag("RenderType", "Transparent");
        }

        private static Texture2D GetTexture(Texture2D[] texturesByIndex, int textureIndex)
        {
            return textureIndex >= 0 && textureIndex < texturesByIndex.Length ? texturesByIndex[textureIndex] : null;
        }

        private static Material BuildMaterial(PMXMaterial pmxMaterial, Texture2D mainTexture, Texture2D sphereTexture, string materialName, bool transparent, bool transparentWithZWrite)
        {
            bool drawsEdge = (pmxMaterial.drawingFlags & PMXMaterial.DrawingFlags.DrawEdge) != 0 && !transparent;
            Shader shader = GetShader(transparent, drawsEdge, out ShaderKind shaderKind);
            if (shader == null)
            {
                Debug.LogError("No compatible PMX material shader was found.");
                return null;
            }

            Material material = new Material(shader)
            {
                name = materialName,
            };

            // Properties shared by every supported shader family.
            SetColorIfPresent(material, s_BaseColorProperty, pmxMaterial.diffuse);
            if (mainTexture != null)
            {
                SetTextureIfPresent(material, s_BaseTextureProperty, mainTexture);
            }

            bool isDoubleSided = (pmxMaterial.drawingFlags & PMXMaterial.DrawingFlags.DoubleSided) != 0;
            SetFloatIfPresent(material, s_CullModeProperty, isDoubleSided ? (float)CullMode.Off : (float)CullMode.Back);

            if (shaderKind == ShaderKind.LilToon)
            {
                ConfigureLilToonShading(material, pmxMaterial, mainTexture, sphereTexture, isDoubleSided, drawsEdge);
            }

            if (transparent)
            {
                switch (shaderKind)
                {
                    case ShaderKind.LilToon:
                        ConfigureLilToonTransparent(material, transparentWithZWrite);
                        break;
                    case ShaderKind.URP:
                        ConfigureURPTransparent(material, transparentWithZWrite);
                        break;
                    case ShaderKind.BuiltIn:
                        ConfigureBuiltInTransparent(material, transparentWithZWrite);
                        break;
                }
            }

            return material;
        }

        private enum ShaderKind
        {
            LilToon,
            URP,
            BuiltIn,
        }

        private static Shader GetShader(bool transparent, bool drawsEdge, out ShaderKind shaderKind)
        {
            // Edge-drawing materials need the outline-capable lilToon variant; the plain Multi shader has no
            // outline pass. URP/built-in fallbacks have no outline support, so edges are dropped there.
            Shader lilToonShader = Shader.Find(drawsEdge ? k_LilToonMultiOutlineShaderName : k_LilToonMultiShaderName);
            if (lilToonShader != null)
            {
                shaderKind = ShaderKind.LilToon;
                return lilToonShader;
            }

            Shader urpShader = Shader.Find(k_URPUnlitFallbackShaderName);
            if (urpShader != null)
            {
                shaderKind = ShaderKind.URP;
                return urpShader;
            }

            shaderKind = ShaderKind.BuiltIn;
            return Shader.Find(transparent ? k_BuiltInUnlitTransparentShaderName : k_BuiltInUnlitShaderName);
        }

        private static void SetColorIfPresent(Material material, int property, Color value)
        {
            if (material.HasProperty(property))
            {
                material.SetColor(property, value);
            }
        }

        private static void SetFloatIfPresent(Material material, int property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private static void SetTextureIfPresent(Material material, int property, Texture texture)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
            }
        }

        private static void SetKeywordIfPresent(Material material, string keywordName, bool enabled)
        {
            LocalKeyword keyword = new LocalKeyword(material.shader, keywordName);
            material.SetKeyword(keyword, enabled);
        }

        #region Transparent Material Detection

        private const float k_AlphaCoverageScale = 255.0f;
        // Indices into the job's shared result array, accumulated atomically across worker threads.
        private const int k_ResultCoveredCount = 0;
        private const int k_ResultAlphaSum = 1;
        private const int k_ResultAnyOpaque = 2;
        private const int k_ResultCount = 3;

        /// <summary>
        /// Measures the alpha coverage of a material's texture over its UV footprint on the CPU, sampling the
        /// raw source pixels at every vertex UV and every triangle centroid (center of gravity) with a
        /// Burst-compiled parallel job, to decide whether the material should be rendered as transparent.
        /// </summary>
        /// <param name="sourcePixels">Decoded source-file pixels; an invalid value is treated as fully opaque white.</param>
        /// <param name="model">PMX model providing vertices, UVs, and triangle indices.</param>
        /// <param name="faceIndexStart">Start offset into <see cref="PMXModel.indices"/> for this material.</param>
        /// <param name="faceIndexCount">Number of indices this material consumes (a multiple of three).</param>
        /// <param name="alphaThreshold">Coverage value below which the material is considered transparent.</param>
        /// <returns>
        /// A tuple of whether coverage is below the threshold, the measured average coverage, and whether
        /// any sampled pixel was fully opaque.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the model is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the index range is invalid.</exception>
        public static (bool isBelowAlphaCoverageThreshold, float alphaCoverage, bool anyPixelOpaque) DetectAlphaCoverage(
            SourcePixels sourcePixels,
            PMXModel model,
            int faceIndexStart,
            int faceIndexCount,
            float alphaThreshold)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            if (faceIndexStart < 0 || faceIndexCount < 0 || faceIndexStart + faceIndexCount > model.indices.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(faceIndexStart), faceIndexStart, "Face index range is out of bounds.");
            }

            int triangleCount = faceIndexCount / 3;
            // A material with no geometry contributes no samples; treat it as fully opaque so it is never
            // forced transparent, exactly as the GPU path's empty-footprint readback did.
            if (triangleCount == 0)
            {
                return (false, 1.0f, true);
            }

            // Compact this material's geometry: a UV per referenced vertex and re-based triangle indices,
            // so the job can sample uv[i] for i < vertexCount and compute centroids for the rest on the fly.
            Dictionary<uint, int> remappedVertices = new Dictionary<uint, int>(faceIndexCount);
            NativeArray<float2> uvs = new NativeArray<float2>(faceIndexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> indices = new NativeArray<int>(faceIndexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            int vertexCount = 0;
            for (int index = 0; index < faceIndexCount; ++index)
            {
                uint vertexIndex = model.indices[faceIndexStart + index];
                if (!remappedVertices.TryGetValue(vertexIndex, out int remapped))
                {
                    remapped = vertexCount++;
                    remappedVertices[vertexIndex] = remapped;
                    uvs[remapped] = model.vertices[(int)vertexIndex].uv;
                }
                indices[index] = remapped;
            }

            // The job's [ReadOnly] NativeArray field must always be assigned for the job-safety system, even
            // when there is no texture; a one-element placeholder stands in and is ignored via hasTexture.
            bool hasTexture = sourcePixels.IsValid;
            NativeArray<Color32> pixels = hasTexture
                ? new NativeArray<Color32>(sourcePixels.pixels, Allocator.TempJob)
                : new NativeArray<Color32>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            NativeArray<int> result = new NativeArray<int>(k_ResultCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            DetectAlphaJob job = new DetectAlphaJob
            {
                uvs = uvs,
                indices = indices,
                vertexCount = vertexCount,
                pixels = pixels,
                textureWidth = sourcePixels.width,
                textureHeight = sourcePixels.height,
                hasTexture = hasTexture,
                result = result,
            };
            job.Schedule(vertexCount + triangleCount, 64).Complete();

            int coveredSamples = result[k_ResultCoveredCount];
            float alphaCoverage = coveredSamples > 0
                ? result[k_ResultAlphaSum] / (coveredSamples * k_AlphaCoverageScale)
                : 1.0f;
            bool isBelowAlphaCoverageThreshold = alphaCoverage < alphaThreshold;
            bool anyPixelOpaque = result[k_ResultAnyOpaque] != 0;

            uvs.Dispose();
            indices.Dispose();
            result.Dispose();
            pixels.Dispose();

            return (isBelowAlphaCoverageThreshold, alphaCoverage, anyPixelOpaque);
        }

        /// <summary>Decoded source-file pixels for one texture: a tightly-packed bottom-up <see cref="Color32"/> grid.</summary>
        public readonly struct SourcePixels
        {
            /// <summary>Bottom-up, row-major RGBA pixels; <c>null</c> when no source texture was decoded.</summary>
            public readonly Color32[] pixels;
            public readonly int width;
            public readonly int height;

            public SourcePixels(Color32[] pixels, int width, int height)
            {
                this.pixels = pixels;
                this.width = width;
                this.height = height;
            }

            /// <summary>True when the pixel grid is present and matches its dimensions.</summary>
            public bool IsValid => pixels != null && width > 0 && height > 0 && pixels.Length >= width * height;
        }

        /// <summary>
        /// Decodes a PMX model's texture files into raw <see cref="SourcePixels"/> on demand, caching each
        /// resolved file so it is decoded only once per build. Decoding reads the original source bytes rather
        /// than the imported texture asset, so it is unaffected by editor compression or read/write settings.
        /// BMP and TGA are decoded straight to <see cref="Color32"/> arrays; other formats go through a
        /// throwaway in-memory decode that never produces a compressed asset.
        /// </summary>
        private sealed class SourcePixelCache
        {
            private readonly PMXModel m_Model;
            private readonly string m_BaseDirectory;
            private readonly Dictionary<int, SourcePixels> m_ByTextureIndex = new Dictionary<int, SourcePixels>();

            public SourcePixelCache(PMXModel model, PMXImportOptions options)
            {
                m_Model = model;
                m_BaseDirectory = !string.IsNullOrEmpty(options.textureBaseDirectory)
                    ? options.textureBaseDirectory
                    : Path.GetDirectoryName(options.sourcePath);
            }

            /// <summary>Returns the decoded pixels for a texture index, or an invalid value when unavailable.</summary>
            public SourcePixels Get(int textureIndex)
            {
                if (textureIndex < 0 || textureIndex >= m_Model.texturePaths.Length)
                {
                    return default;
                }
                if (m_ByTextureIndex.TryGetValue(textureIndex, out SourcePixels cached))
                {
                    return cached;
                }

                SourcePixels decoded = Decode(m_Model.texturePaths[textureIndex].ToString());
                m_ByTextureIndex[textureIndex] = decoded;
                return decoded;
            }

            private SourcePixels Decode(string texturePath)
            {
                string fullPath = ResolvePath(texturePath);
                if (string.IsNullOrEmpty(fullPath))
                {
                    return default;
                }

                byte[] bytes = File.ReadAllBytes(fullPath);
                string extension = Path.GetExtension(fullPath);

                if (string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase))
                {
                    using MemoryStream stream = new MemoryStream(bytes, false);
                    Color32[] pixels = TGALoader.LoadTGA(stream, out int width, out int height, out int _);
                    return new SourcePixels(pixels, width, height);
                }

                if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    BMPImage bmp = new BMPLoader().LoadBMP(bytes);
                    int bmpWidth = bmp.info.absWidth;
                    int bmpHeight = bmp.info.absHeight;
                    // BMPImage.imageData is already bottom-up for positive heights; a negative height means the
                    // rows are stored top-down, so flip to the bottom-up order the sampler expects (this
                    // mirrors what BMPImage.ToTexture2D does before SetPixels32).
                    if (bmp.info.height < 0)
                    {
                        FlipRowsInPlace(bmp.imageData, bmpWidth, bmpHeight);
                    }
                    return new SourcePixels(bmp.imageData, bmpWidth, bmpHeight);
                }

                // PNG/JPG: decode through a throwaway in-memory texture. LoadImage produces uncompressed,
                // readable RGBA32 pixels independent of any imported asset's compression settings.
                Texture2D temporary = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(temporary, bytes);
                SourcePixels decoded = new SourcePixels(temporary.GetPixels32(), temporary.width, temporary.height);
                PMXUtilities.DestroyRuntimeObject(temporary);
                return decoded;
            }

            private string ResolvePath(string texturePath)
            {
                if (string.IsNullOrWhiteSpace(texturePath)
                    || string.IsNullOrEmpty(m_BaseDirectory)
                    || !Directory.Exists(m_BaseDirectory))
                {
                    return null;
                }

                string[] candidates =
                {
                    Path.Combine(m_BaseDirectory, texturePath),
                    Path.Combine(m_BaseDirectory, texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)),
                };
                foreach (string candidate in candidates)
                {
                    string resolved = Path.GetFullPath(candidate);
                    if (File.Exists(resolved))
                    {
                        return resolved;
                    }
                }

                string fileName = Path.GetFileName(texturePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return null;
                }
                foreach (string file in Directory.GetFiles(m_BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
                return null;
            }

            /// <summary>Reverses the row order of a row-major pixel grid in place (top-down to bottom-up).</summary>
            private static void FlipRowsInPlace(Color32[] pixels, int width, int height)
            {
                for (int y = 0; y < height / 2; ++y)
                {
                    int topRow = y * width;
                    int bottomRow = (height - 1 - y) * width;
                    for (int x = 0; x < width; ++x)
                    {
                        (pixels[topRow + x], pixels[bottomRow + x]) = (pixels[bottomRow + x], pixels[topRow + x]);
                    }
                }
            }
        }

        /// <summary>
        /// Burst-compiled parallel job that point-samples a material's texture once per work item: vertex UVs
        /// for items below <see cref="vertexCount"/>, and triangle centroids (computed on the fly from
        /// <see cref="indices"/>) for the rest. Each worker accumulates into the shared <see cref="result"/>
        /// array atomically.
        /// </summary>
        [BurstCompile]
        private unsafe struct DetectAlphaJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> uvs;
            [ReadOnly] public NativeArray<int> indices;
            public int vertexCount;
            [ReadOnly] public NativeArray<Color32> pixels;
            public int textureWidth;
            public int textureHeight;
            public bool hasTexture;
            // Shared across all work items; every write is an atomic Interlocked accumulation, so the
            // parallel-for aliasing restriction is intentionally disabled.
            [NativeDisableParallelForRestriction] public NativeArray<int> result;

            public void Execute(int workItem)
            {
                float2 uv;
                if (workItem < vertexCount)
                {
                    uv = uvs[workItem];
                }
                else
                {
                    int triangle = workItem - vertexCount;
                    int indexBase = triangle * 3;
                    uv = (uvs[indices[indexBase + 0]] + uvs[indices[indexBase + 1]] + uvs[indices[indexBase + 2]]) / 3.0f;
                }

                byte alpha = SampleAlpha(uv);

                // Every sample counts toward coverage so alpha-0 texels drag the average down, matching the
                // GPU pass which averaged alpha over the whole rasterized footprint (its green-channel mask
                // marked every covered texel, not just the non-transparent ones).
                int* basePointer = (int*)result.GetUnsafePtr();
                Interlocked.Increment(ref basePointer[k_ResultCoveredCount]);
                Interlocked.Add(ref basePointer[k_ResultAlphaSum], alpha);
                if (alpha == 255)
                {
                    Interlocked.Exchange(ref basePointer[k_ResultAnyOpaque], 1);
                }
            }

            /// <summary>
            /// Point-samples the texture alpha at the given UV. An absent texture is treated as fully opaque
            /// white, matching the GPU pass's white-texture fallback.
            /// </summary>
            private byte SampleAlpha(float2 uv)
            {
                if (!hasTexture)
                {
                    return 255;
                }

                // The source pixels are bottom-up (row 0 = bottom), so flip V to map PMX UV space onto them,
                // and wrap so tiled UVs sample within bounds.
                float u = uv.x - math.floor(uv.x);
                float v = 1.0f - uv.y;
                v -= math.floor(v);

                int x = (int)(u * textureWidth);
                int y = (int)(v * textureHeight);
                x = math.clamp(x, 0, textureWidth - 1);
                y = math.clamp(y, 0, textureHeight - 1);

                return pixels[y * textureWidth + x].a;
            }
        }

        #endregion
    }
}
