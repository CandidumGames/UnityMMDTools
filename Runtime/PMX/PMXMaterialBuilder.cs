using System;
using System.Collections.Generic;
using Unity.Collections;
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
            Mesh mesh = BuildTemporaryMesh(model);
            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial pmxMaterial = model.materials[i];
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

                (bool isBelowAlphaCoverageThreshold, float alphaCoverage, bool anyPixelOpaque) alphaDetection = DetectAlphaCoverage(
                        mainTexture,
                        mesh,
                        i,
                        options.uvBlitShader,
                        options.alphaDetectorShader,
                        options.alphaDetectionTextureSize,
                        options.alphaDetectionThreshold);
                bool shouldBeTransparent = alphaDetection.isBelowAlphaCoverageThreshold || pmxMaterial.diffuse.a < 1.0f;
                float alphaCoverage = Mathf.Min(alphaDetection.alphaCoverage, pmxMaterial.diffuse.a);
                bool anyPixelOpaque = alphaDetection.anyPixelOpaque && !(pmxMaterial.diffuse.a < 1.0f);
                bool transparentWithZWrite = anyPixelOpaque || alphaCoverage >= options.alphaCoverageZWriteThreshold;
                Material material = BuildMaterial(pmxMaterial, mainTexture, sphereTexture, materialName, shouldBeTransparent, transparentWithZWrite);

                material.renderQueue += i;

                materials.Add(material);
            }
            PMXUtilities.DestroyRuntimeObject(mesh);
            return materials;
        }

        /// <summary>
        /// Builds a temporary UV-only mesh, with one submesh per material, used by alpha-coverage detection.
        /// The caller is responsible for destroying the returned mesh.
        /// </summary>
        /// <param name="model">PMX model providing vertices, UVs, indices, and materials.</param>
        /// <returns>The temporary mesh used for alpha detection.</returns>
        public static Mesh BuildTemporaryMesh(PMXModel model)
        {
            Mesh mesh = new Mesh
            {
                name = "TemporaryMesh",
            };

            Vector3[] vertices = new Vector3[model.vertices.Length];
            Vector2[] uvs = new Vector2[model.vertices.Length];
            for (int i = 0; i < model.vertices.Length; ++i)
            {
                PMXVertex vertex = model.vertices[i];
                uvs[i] = new Vector2(vertex.uv.x, 1.0f - vertex.uv.y);
            }
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);

            mesh.SetIndexBufferParams(model.indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(model.indices, 0, 0, model.indices.Length, MeshUpdateFlags.DontRecalculateBounds);
            mesh.subMeshCount = model.materials.Length;

            int indicesOffset = 0;
            for (int materialIndex = 0; materialIndex < model.materials.Length; ++materialIndex)
            {
                int faceIndexCount = model.materials[materialIndex].faceIndexCount;
                mesh.SetSubMesh(materialIndex, new SubMeshDescriptor
                {
                    indexStart = indicesOffset,
                    indexCount = faceIndexCount,
                    topology = MeshTopology.Triangles
                });
                indicesOffset += faceIndexCount;
            }
            mesh.UploadMeshData(true);

            return mesh;
        }

        private static void ConfigureLilToonShading(Material material, PMXMaterial pmxMaterial, Texture2D mainTexture, Texture2D sphereTexture, bool isDoubleSided, bool drawsEdge)
        {
            Color ambientColor = new Color(pmxMaterial.ambient.x, pmxMaterial.ambient.y, pmxMaterial.ambient.z, 1.0f);
            Color borderColor = new Color(
                (pmxMaterial.diffuse.r + pmxMaterial.ambient.x) * 0.5f,
                (pmxMaterial.diffuse.g + pmxMaterial.ambient.y) * 0.5f,
                (pmxMaterial.diffuse.b + pmxMaterial.ambient.z) * 0.5f,
                1.0f);
            SetColorIfPresent(material, s_ShadowColorProperty, ambientColor);
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
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = transparentWithZWrite
                ? (int)RenderQueue.GeometryLast + 1
                : (int)RenderQueue.Transparent;
            SetKeywordIfPresent(material, k_URPSurfaceTypeTransparentKeyword, true);
        }

        private static void ConfigureURPTransparent(Material material, bool transparentWithZWrite)
        {
            SetFloatIfPresent(material, s_URPSurfaceProperty, 1.0f);
            SetFloatIfPresent(material, s_URPBlendProperty, 0.0f);
            SetFloatIfPresent(material, s_URPSrcBlendProperty, (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, s_URPDstBlendProperty, (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, s_URPZWriteProperty, transparentWithZWrite ? 1.0f : 0.0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = transparentWithZWrite
                ? (int)RenderQueue.GeometryLast + 1
                : (int)RenderQueue.Transparent;
            SetKeywordIfPresent(material, k_URPSurfaceTypeTransparentKeyword, true);
        }

        private static void ConfigureBuiltInTransparent(Material material, bool transparentWithZWrite)
        {
            // Built-in uses a dedicated Unlit/Transparent shader, so blending is already
            // baked into the shader; only the render order needs to be set explicitly.
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = transparentWithZWrite
                ? (int)RenderQueue.GeometryLast + 1
                : (int)RenderQueue.Transparent;
        }

        private static Texture2D GetTexture(Texture2D[] texturesByIndex, int textureIndex)
        {
            return textureIndex >= 0 && textureIndex < texturesByIndex.Length ? texturesByIndex[textureIndex] : null;
        }

        private static Material BuildMaterial(PMXMaterial pmxMaterial, Texture2D mainTexture, Texture2D sphereTexture, string materialName, bool transparent, bool transparentWithZWrite)
        {
            bool drawsEdge = (pmxMaterial.drawingFlags & PMXMaterial.DrawingFlags.DrawEdge) != 0;
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
            SetKeywordIfPresent(material, k_UnityUIClipRectKeyword, true);

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

        private const int k_ResultCount = 3;
        private const int k_ThreadGroupSize = 8;
        private const float k_AlphaCoverageScale = 255.0f;
        private const string k_KernelName = "DetectAlpha";
        private static readonly int s_MainTexProperty = Shader.PropertyToID("_MainTex");
        private static readonly int s_AlphaTextureProperty = Shader.PropertyToID("_AlphaTexture");
        private static readonly int s_ResultProperty = Shader.PropertyToID("_Result");
        private static readonly int s_TextureSizeProperty = Shader.PropertyToID("_TextureSize");

        /// <summary>
        /// Measures the alpha coverage of a material's texture over its UV footprint using a synchronous
        /// command-buffer GPU readback, to decide whether the material should be rendered as transparent.
        /// </summary>
        /// <param name="texture">Texture to measure; a white texture is used when null.</param>
        /// <param name="temporaryMesh">UV-only mesh whose submesh defines the sampled footprint.</param>
        /// <param name="subMeshIndex">Submesh index corresponding to the material being measured.</param>
        /// <param name="uvBlitShader">UV-space blit shader feeding the detection compute shader.</param>
        /// <param name="alphaDetectorShader">Compute shader that accumulates alpha statistics.</param>
        /// <param name="textureSize">Square render target size used for sampling.</param>
        /// <param name="alphaThreshold">Coverage value below which the material is considered transparent.</param>
        /// <returns>
        /// A tuple of whether coverage is below the threshold, the measured average coverage, and whether
        /// any sampled pixel was fully opaque.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the temporary mesh or a required shader is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the submesh index or texture size is invalid.</exception>
        public static (bool isBelowAlphaCoverageThreshold, float alphaCoverage, bool anyPixelOpaque) DetectAlphaCoverage(
            Texture2D texture,
            Mesh temporaryMesh,
            int subMeshIndex,
            Shader uvBlitShader,
            ComputeShader alphaDetectorShader,
            int textureSize,
            float alphaThreshold)
        {
            if (temporaryMesh == null)
            {
                throw new ArgumentNullException(nameof(temporaryMesh));
            }
            if (uvBlitShader == null)
            {
                throw new ArgumentNullException(nameof(uvBlitShader));
            }
            if (alphaDetectorShader == null)
            {
                throw new ArgumentNullException(nameof(alphaDetectorShader));
            }
            if (subMeshIndex < 0 || subMeshIndex >= temporaryMesh.subMeshCount)
            {
                throw new ArgumentOutOfRangeException(nameof(subMeshIndex), subMeshIndex, "Sub-mesh index is out of range.");
            }
            if (textureSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(textureSize), textureSize, "Texture size must be positive.");
            }

            RenderTexture renderTexture = RenderTexture.GetTemporary(textureSize, textureSize, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm);
            renderTexture.filterMode = FilterMode.Point;

            Material uvMaterial = new Material(uvBlitShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            properties.SetTexture(s_MainTexProperty, texture != null ? texture : Texture2D.whiteTexture);

            int kernel = alphaDetectorShader.FindKernel(k_KernelName);
            ComputeBuffer resultBuffer = new ComputeBuffer(k_ResultCount, sizeof(uint), ComputeBufferType.Structured);
            resultBuffer.SetData(new uint[k_ResultCount]);

            int threadGroups = Mathf.CeilToInt(textureSize / (float)k_ThreadGroupSize);
            CommandBuffer commandBuffer = new CommandBuffer
            {
                name = "PMX Material Alpha Detection",
            };
            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.ClearRenderTarget(false, true, Color.white);
            commandBuffer.DrawMesh(temporaryMesh, Matrix4x4.identity, uvMaterial, subMeshIndex, 0, properties);
            commandBuffer.SetComputeTextureParam(alphaDetectorShader, kernel, s_AlphaTextureProperty, renderTexture);
            commandBuffer.SetComputeBufferParam(alphaDetectorShader, kernel, s_ResultProperty, resultBuffer);
            commandBuffer.SetComputeIntParams(alphaDetectorShader, s_TextureSizeProperty, textureSize, textureSize);
            commandBuffer.DispatchCompute(alphaDetectorShader, kernel, threadGroups, threadGroups, 1);
            NativeArray<uint> resultBufferArray = new NativeArray<uint>(k_ResultCount, Allocator.Persistent);
            commandBuffer.RequestAsyncReadbackIntoNativeArray(ref resultBufferArray, resultBuffer, request => { });
            commandBuffer.WaitAllAsyncReadbackRequests();
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Release();

            float alphaCoverage = resultBufferArray[0] > 0
                ? resultBufferArray[1] / (resultBufferArray[0] * k_AlphaCoverageScale)
                : 1.0f;
            bool isBelowAlphaCoverageThreshold = alphaCoverage < alphaThreshold;
            bool anyPixelOpaque = resultBufferArray[2] > 0;

            resultBuffer.Release();
            RenderTexture.ReleaseTemporary(renderTexture);
            PMXUtilities.DestroyRuntimeObject(uvMaterial);
            resultBufferArray.Dispose();

            return (isBelowAlphaCoverageThreshold, alphaCoverage, anyPixelOpaque);
        }
    }

    #endregion
}
