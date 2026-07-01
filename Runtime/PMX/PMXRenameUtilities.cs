using Kawazu;
using Newtonsoft.Json;
using PinyinNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Ordered PMX2FBX-style token replacement lists for general names and morph names.
    /// </summary>
    [Serializable]
    public sealed class PMXRenameLists
    {
        /// <summary>Ordered replacements applied to material, bone, rigid body, and joint names.</summary>
        public List<PMXRenameEntry> renameList = new List<PMXRenameEntry>();
        /// <summary>Ordered replacements applied to morph names.</summary>
        public List<PMXRenameEntry> morphRenameList = new List<PMXRenameEntry>();
    }

    /// <summary>
    /// A single ordered token replacement mapping a source substring to its replacement.
    /// </summary>
    [Serializable]
    public sealed class PMXRenameEntry
    {
        /// <summary>Substring to search for.</summary>
        public string from = "";
        /// <summary>Replacement text.</summary>
        public string to = "";
    }

    /// <summary>
    /// Result of a rename pass, recording how many names changed and a full original-to-renamed string map.
    /// </summary>
    public sealed class PMXRenameResult
    {
        /// <summary>Number of Japanese (local) names that changed.</summary>
        public int localNameRenameCount;
        /// <summary>Number of English names that changed.</summary>
        public int englishNameRenameCount;
        /// <summary>Per-name record of original and renamed values.</summary>
        public List<PMXStringMapEntry> stringMap = new List<PMXStringMapEntry>();

        /// <summary>
        /// Total number of names that changed across local and English names.
        /// </summary>
        public int totalRenameCount => localNameRenameCount + englishNameRenameCount;
    }

    /// <summary>
    /// A single original-to-renamed mapping entry within a rename result.
    /// </summary>
    [Serializable]
    public sealed class PMXStringMapEntry
    {
        /// <summary>Category of the renamed element (for example "material", "bone", or "morph").</summary>
        public string category = "";
        /// <summary>Index of the element within its category.</summary>
        public int index;
        /// <summary>Renamed field name (for example "name").</summary>
        public string field = "";
        /// <summary>Original name before renaming.</summary>
        public string original = "";
        /// <summary>Resulting name after renaming.</summary>
        public string renamed = "";
    }

    /// <summary>
    /// Applies ordered PMX2FBX-style token replacements followed by Japanese (Kawazu) and Chinese (Pinyin) romanization to produce ASCII-safe, unique PMX material, bone, morph, rigid body, and joint names.
    /// </summary>
    public static class PMXRenameUtilities
    {
        private const int k_HiraganaStart = 0x3040;
        private const int k_HiraganaEnd = 0x309F;
        private const int k_KatakanaStart = 0x30A0;
        private const int k_KatakanaEnd = 0x30FF;
        private const int k_KatakanaPhoneticExtensionsStart = 0x31F0;
        private const int k_KatakanaPhoneticExtensionsEnd = 0x31FF;
        private const int k_KanaSupplementStart = 0x1B000;
        private const int k_KanaSupplementEnd = 0x1B0FF;
        private const int k_KanaExtendedAStart = 0x1B100;
        private const int k_KanaExtendedAEnd = 0x1B12F;
        private const int k_SmallKanaExtensionStart = 0x1B130;
        private const int k_SmallKanaExtensionEnd = 0x1B16F;
        private const int k_HalfwidthKatakanaStart = 0xFF66;
        private const int k_HalfwidthKatakanaEnd = 0xFF9F;

        private const int k_HangulJamoStart = 0x1100;
        private const int k_HangulJamoEnd = 0x11FF;
        private const int k_HangulCompatibilityJamoStart = 0x3130;
        private const int k_HangulCompatibilityJamoEnd = 0x318F;
        private const int k_HangulJamoExtendedAStart = 0xA960;
        private const int k_HangulJamoExtendedAEnd = 0xA97F;
        private const int k_HangulSyllablesStart = 0xAC00;
        private const int k_HangulSyllablesEnd = 0xD7AF;
        private const int k_HangulJamoExtendedBStart = 0xD7B0;
        private const int k_HangulJamoExtendedBEnd = 0xD7FF;

        private const int k_CJKUnifiedIdeographsExtensionAStart = 0x3400;
        private const int k_CJKUnifiedIdeographsExtensionAEnd = 0x4DBF;
        private const int k_CJKUnifiedIdeographsStart = 0x4E00;
        private const int k_CJKUnifiedIdeographsEnd = 0x9FFF;
        private const int k_CJKCompatibilityIdeographsStart = 0xF900;
        private const int k_CJKCompatibilityIdeographsEnd = 0xFAFF;
        private const int k_CJKUnifiedIdeographsExtensionBStart = 0x20000;
        private const int k_CJKUnifiedIdeographsExtensionBEnd = 0x2A6DF;
        private const int k_CJKUnifiedIdeographsExtensionCStart = 0x2A700;
        private const int k_CJKUnifiedIdeographsExtensionCEnd = 0x2B73F;
        private const int k_CJKUnifiedIdeographsExtensionDStart = 0x2B740;
        private const int k_CJKUnifiedIdeographsExtensionDEnd = 0x2B81F;
        private const int k_CJKUnifiedIdeographsExtensionEStart = 0x2B820;
        private const int k_CJKUnifiedIdeographsExtensionEEnd = 0x2CEAF;
        private const int k_CJKUnifiedIdeographsExtensionFStart = 0x2CEB0;
        private const int k_CJKUnifiedIdeographsExtensionFEnd = 0x2EBEF;
        private const int k_CJKCompatibilityIdeographsSupplementStart = 0x2F800;
        private const int k_CJKCompatibilityIdeographsSupplementEnd = 0x2FA1F;

        private static KawazuConverter m_KawazuConverter;
        private static UMTResources m_KawazuConverterResources;
        private static string m_KawazuDictionaryPath;
        private static UMTResources m_PinyinDictionaryResources;

        /// <summary>
        /// Loads the default rename lists from the rename-list JSON stored in the given resources.
        /// </summary>
        /// <param name="umtResources">UMT resources providing the rename-list JSON.</param>
        /// <returns>The deserialized rename lists.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="umtResources"/> is null.</exception>
        public static PMXRenameLists LoadRenameLists(UMTResources umtResources)
        {
            if (umtResources == null)
            {
                throw new ArgumentNullException(nameof(umtResources));
            }

            return LoadRenameListsJson(umtResources.GetPMXRenameListsJson());
        }

        /// <summary>
        /// Deserializes rename lists from a JSON string.
        /// </summary>
        /// <param name="json">Rename-list JSON.</param>
        /// <returns>The deserialized rename lists, or an empty instance when the JSON deserializes to null.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="json"/> is null or empty.</exception>
        public static PMXRenameLists LoadRenameListsJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("Rename list JSON is required.", nameof(json));
            }

            return JsonConvert.DeserializeObject<PMXRenameLists>(json) ?? new PMXRenameLists();
        }

        /// <summary>
        /// Asynchronously deserializes rename lists from a JSON string.
        /// </summary>
        /// <remarks>
        /// The parse itself is a single indivisible call; this yields once through <paramref name="frameBudget"/> before it so the UI can repaint, and runs on the main thread (no worker offload) to stay safe on WebGL.
        /// </remarks>
        /// <param name="frameBudget">Frame budget used to yield back to the main thread before parsing; may be null to run without yielding.</param>
        /// <param name="json">Rename-list JSON.</param>
        /// <returns>The deserialized rename lists, or an empty instance when the JSON deserializes to null.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="json"/> is null or empty.</exception>
        public static async Task<PMXRenameLists> LoadRenameListsJsonAsync(UMTFrameBudget frameBudget, string json)
        {
            await YieldIfNeeded(frameBudget);
            return LoadRenameListsJson(json);
        }

        /// <summary>
        /// Renames the material, bone, morph, rigid body, and joint names of a model in place, applying
        /// ordered token replacements, romanization, ASCII normalization, and bone-name uniqueness.
        /// </summary>
        /// <param name="model">Model whose names are renamed in place.</param>
        /// <param name="renameLists">Ordered token replacement lists to apply.</param>
        /// <param name="umtResources">Resources providing romanization dictionaries.</param>
        /// <returns>The rename result with change counts and a string map.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> or <paramref name="renameLists"/> is null.</exception>
        public static PMXRenameResult Rename(PMXModel model, PMXRenameLists renameLists, UMTResources umtResources)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            if (renameLists == null)
            {
                throw new ArgumentNullException(nameof(renameLists));
            }

            string[] materialNames = BuildRenamedNames(GetOriginalNames(model.materials), renameLists.renameList, umtResources, true);
            string[] boneNames = BuildRenamedNames(GetOriginalNames(model.bones), renameLists.renameList, umtResources, false);
            string[] morphNames = BuildRenamedNames(GetOriginalNames(model.morphs), renameLists.morphRenameList, umtResources, true);
            string[] rigidBodyNames = BuildRenamedNames(GetOriginalNames(model.rigidBodies), renameLists.renameList, umtResources, false);
            string[] jointNames = BuildRenamedNames(GetOriginalNames(model.joints), renameLists.renameList, umtResources, false);
            boneNames = BuildUniqueBoneNames(model, boneNames);

            PMXRenameResult result = new PMXRenameResult();
            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial material = model.materials[i];
                string originalName = material.originalName.ToString();
                ApplyRenamedName("material", i, originalName, materialNames[i], ref material.renamedName, result);
                model.materials[i] = material;
            }

            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone bone = model.bones[i];
                string originalName = bone.originalName.ToString();
                ApplyRenamedName("bone", i, originalName, boneNames[i], ref bone.renamedName, result);
                model.bones[i] = bone;
            }

            for (int i = 0; i < model.morphs.Length; ++i)
            {
                PMXMorph morph = model.morphs[i];
                string originalName = morph.originalName.ToString();
                ApplyRenamedName("morph", i, originalName, morphNames[i], ref morph.renamedName, result);
                model.morphs[i] = morph;
            }

            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody rigidBody = model.rigidBodies[i];
                string originalName = rigidBody.originalName.ToString();
                ApplyRenamedName("rigidBody", i, originalName, rigidBodyNames[i], ref rigidBody.renamedName, result);
                model.rigidBodies[i] = rigidBody;
            }

            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint joint = model.joints[i];
                string originalName = joint.originalName.ToString();
                ApplyRenamedName("joint", i, originalName, jointNames[i], ref joint.renamedName, result);
                model.joints[i] = joint;
            }

            return result;
        }

        /// <summary>
        /// Asynchronously renames the material, bone, morph, rigid body, and joint names of a model in place, applying
        /// ordered token replacements, romanization, ASCII normalization, and bone-name uniqueness.
        /// </summary>
        /// <remarks>
        /// Runs entirely on the calling (main) thread and yields cooperatively through <paramref name="frameBudget"/> rather than offloading to a worker thread, so it stays responsive  and functional  on single-threaded platforms such as WebGL.
        /// </remarks>
        /// <param name="frameBudget">Frame budget used to yield back to the main thread between stages; may be null to run without yielding.</param>
        /// <param name="model">Model whose names are renamed in place.</param>
        /// <param name="renameLists">Ordered token replacement lists to apply.</param>
        /// <param name="umtResources">Resources providing romanization dictionaries.</param>
        /// <returns>The rename result with change counts and a string map.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> or <paramref name="renameLists"/> is null.</exception>
        public static async Task<PMXRenameResult> RenameAsync(UMTFrameBudget frameBudget, PMXModel model, PMXRenameLists renameLists, UMTResources umtResources)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            if (renameLists == null)
            {
                throw new ArgumentNullException(nameof(renameLists));
            }

            string[] materialNames = BuildRenamedNames(GetOriginalNames(model.materials), renameLists.renameList, umtResources, true);
            await YieldIfNeeded(frameBudget);
            string[] boneNames = BuildRenamedNames(GetOriginalNames(model.bones), renameLists.renameList, umtResources, false);
            await YieldIfNeeded(frameBudget);
            string[] morphNames = BuildRenamedNames(GetOriginalNames(model.morphs), renameLists.morphRenameList, umtResources, true);
            await YieldIfNeeded(frameBudget);
            string[] rigidBodyNames = BuildRenamedNames(GetOriginalNames(model.rigidBodies), renameLists.renameList, umtResources, false);
            await YieldIfNeeded(frameBudget);
            string[] jointNames = BuildRenamedNames(GetOriginalNames(model.joints), renameLists.renameList, umtResources, false);
            await YieldIfNeeded(frameBudget);
            boneNames = BuildUniqueBoneNames(model, boneNames);
            await YieldIfNeeded(frameBudget);

            PMXRenameResult result = new PMXRenameResult();
            for (int i = 0; i < model.materials.Length; ++i)
            {
                PMXMaterial material = model.materials[i];
                string originalName = material.originalName.ToString();
                ApplyRenamedName("material", i, originalName, materialNames[i], ref material.renamedName, result);
                model.materials[i] = material;
            }
            await YieldIfNeeded(frameBudget);

            for (int i = 0; i < model.bones.Length; ++i)
            {
                PMXBone bone = model.bones[i];
                string originalName = bone.originalName.ToString();
                ApplyRenamedName("bone", i, originalName, boneNames[i], ref bone.renamedName, result);
                model.bones[i] = bone;
            }
            await YieldIfNeeded(frameBudget);

            for (int i = 0; i < model.morphs.Length; ++i)
            {
                PMXMorph morph = model.morphs[i];
                string originalName = morph.originalName.ToString();
                ApplyRenamedName("morph", i, originalName, morphNames[i], ref morph.renamedName, result);
                model.morphs[i] = morph;
            }
            await YieldIfNeeded(frameBudget);

            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody rigidBody = model.rigidBodies[i];
                string originalName = rigidBody.originalName.ToString();
                ApplyRenamedName("rigidBody", i, originalName, rigidBodyNames[i], ref rigidBody.renamedName, result);
                model.rigidBodies[i] = rigidBody;
            }
            await YieldIfNeeded(frameBudget);

            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint joint = model.joints[i];
                string originalName = joint.originalName.ToString();
                ApplyRenamedName("joint", i, originalName, jointNames[i], ref joint.renamedName, result);
                model.joints[i] = joint;
            }

            return result;
        }

        private static async Task YieldIfNeeded(UMTFrameBudget frameBudget)
        {
            if (frameBudget != null)
            {
                await frameBudget.YieldIfNeeded();
            }
        }

        private static string[] BuildRenamedNames(IReadOnlyList<string> names, IReadOnlyList<PMXRenameEntry> renameList, UMTResources umtResources, bool enforceUniqueNames)
        {
            if (names == null)
            {
                throw new ArgumentNullException(nameof(names));
            }
            if (renameList == null)
            {
                throw new ArgumentNullException(nameof(renameList));
            }

            string[] result = new string[names.Count];
            List<string> pendingTransliterations = new List<string>();
            HashSet<string> pendingSet = new HashSet<string>();
            for (int i = 0; i < names.Count; ++i)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name))
                {
                    result[i] = name;
                    continue;
                }

                string renamed = ApplyOrderedReplacements(name, renameList);
                result[i] = renamed;
                if (pendingSet.Add(renamed))
                {
                    if (!IsASCII(renamed))
                    {
                        pendingTransliterations.Add(renamed);
                    }
                }
            }

            Dictionary<string, string> transliterations = TransliterateRemainingNonASCIIBatch(pendingTransliterations, umtResources);
            HashSet<string> uniqueNames = enforceUniqueNames ? new HashSet<string>(StringComparer.Ordinal) : null;

            for (int i = 0; i < names.Count; ++i)
            {
                string renamed = result[i];
                if (string.IsNullOrEmpty(renamed))
                {
                    continue;
                }

                if (transliterations.TryGetValue(renamed, out string transliterated))
                {
                    renamed = transliterated;
                }

                renamed = ToASCII(renamed);
                if (enforceUniqueNames)
                {
                    while (uniqueNames.Contains(renamed))
                    {
                        renamed += "_";
                    }

                    uniqueNames.Add(renamed);
                }

                result[i] = renamed;
            }

            return result;
        }

        private static string[] BuildUniqueBoneNames(PMXModel model, IReadOnlyList<string> names)
        {
            int boneCount = model.bones.Length;
            int[] depths = new int[boneCount];
            byte[] states = new byte[boneCount];
            for (int i = 0; i < boneCount; ++i)
            {
                ResolveBoneDepth(model, i, depths, states);
            }

            List<int> indices = new List<int>(boneCount);
            for (int i = 0; i < boneCount; ++i)
            {
                indices.Add(i);
            }
            indices.Sort((left, right) =>
            {
                int depthComparison = depths[left].CompareTo(depths[right]);
                return depthComparison != 0 ? depthComparison : left.CompareTo(right);
            });

            string[] result = new string[boneCount];
            string[] paths = new string[boneCount];
            HashSet<string> usedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int orderIndex = 0; orderIndex < indices.Count; ++orderIndex)
            {
                int boneIndex = indices[orderIndex];
                int parentBoneIndex = model.bones[boneIndex].parentBoneIndex;
                string parentPath = parentBoneIndex >= 0 && parentBoneIndex < boneCount ? paths[parentBoneIndex] : string.Empty;
                string baseName = names[boneIndex];
                string generatedName = string.IsNullOrEmpty(baseName) ? $"Bone_{boneIndex}" : baseName;
                string path = BuildDAGPath(parentPath, generatedName);

                if (!string.IsNullOrEmpty(baseName))
                {
                    generatedName = PMXUtilities.GetUniqueName(baseName, candidate => usedPaths.Contains(BuildDAGPath(parentPath, candidate)));
                    path = BuildDAGPath(parentPath, generatedName);
                    result[boneIndex] = generatedName;
                }
                else
                {
                    if (usedPaths.Contains(path))
                    {
                        Debug.LogError($"PMX bone {boneIndex} has an empty name whose generated DAG path '{path}' conflicts with another bone.");
                    }

                    result[boneIndex] = baseName;
                }

                paths[boneIndex] = path;
                usedPaths.Add(path);
            }

            return result;
        }

        private static int ResolveBoneDepth(PMXModel model, int boneIndex, int[] depths, byte[] states)
        {
            if (states[boneIndex] == 2)
            {
                return depths[boneIndex];
            }
            if (states[boneIndex] == 1)
            {
                throw new InvalidOperationException($"PMX bone hierarchy contains a cycle involving bone index {boneIndex}.");
            }

            states[boneIndex] = 1;
            int parentBoneIndex = model.bones[boneIndex].parentBoneIndex;
            depths[boneIndex] = parentBoneIndex >= 0 && parentBoneIndex < model.bones.Length ? ResolveBoneDepth(model, parentBoneIndex, depths, states) + 1 : 0;
            states[boneIndex] = 2;
            return depths[boneIndex];
        }

        private static string BuildDAGPath(string parentPath, string name)
        {
            return string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}|{name}";
        }

        private static string[] GetOriginalNames(IReadOnlyList<PMXMaterial> values)
        {
            string[] names = new string[values.Count];
            for (int i = 0; i < values.Count; ++i)
            {
                names[i] = values[i].originalName.ToString();
            }
            return names;
        }

        private static string[] GetOriginalNames(IReadOnlyList<PMXBone> values)
        {
            string[] names = new string[values.Count];
            for (int i = 0; i < values.Count; ++i)
            {
                names[i] = values[i].originalName.ToString();
            }
            return names;
        }

        private static string[] GetOriginalNames(IReadOnlyList<PMXMorph> values)
        {
            string[] names = new string[values.Count];
            for (int i = 0; i < values.Count; ++i)
            {
                names[i] = values[i].originalName.ToString();
            }
            return names;
        }

        private static string[] GetOriginalNames(IReadOnlyList<PMXRigidBody> values)
        {
            string[] names = new string[values.Count];
            for (int i = 0; i < values.Count; ++i)
            {
                names[i] = values[i].originalName.ToString();
            }
            return names;
        }

        private static string[] GetOriginalNames(IReadOnlyList<PMXJoint> values)
        {
            string[] names = new string[values.Count];
            for (int i = 0; i < values.Count; ++i)
            {
                names[i] = values[i].originalName.ToString();
            }
            return names;
        }

        private static string ApplyOrderedReplacements(string name, IReadOnlyList<PMXRenameEntry> renameList)
        {
            string renamed = name;
            foreach (PMXRenameEntry entry in renameList)
            {
                if (entry == null || string.IsNullOrEmpty(entry.from))
                {
                    continue;
                }

                if (renamed.IndexOf(entry.from, StringComparison.Ordinal) >= 0)
                {
                    renamed = renamed.Replace(entry.from, entry.to ?? string.Empty);
                }
            }

            return renamed;
        }

        /// <summary>
        /// Serializes a rename result's string map to indented JSON.
        /// </summary>
        /// <param name="result">Rename result whose string map is serialized.</param>
        /// <returns>The indented JSON representation of the string map.</returns>
        public static string ToStringMapJson(PMXRenameResult result)
        {
            return JsonConvert.SerializeObject(result.stringMap, Formatting.Indented);
        }

        private static void ApplyRenamedName(string category, int index, string original, string renamedName, ref FixedString128Bytes targetNameField, PMXRenameResult result)
        {
            AddStringMapEntry(result, category, index, "name", original, renamedName);

            if (renamedName != targetNameField.ToString())
            {
                ++result.localNameRenameCount;
                targetNameField.CopyFromTruncated(renamedName ?? string.Empty);
            }
        }

        private static void AddStringMapEntry(PMXRenameResult result, string category, int index, string field, string original, string renamed)
        {
            if (original == renamed)
            {
                return;
            }

            result.stringMap.Add(new PMXStringMapEntry
            {
                category = category,
                index = index,
                field = field,
                original = original,
                renamed = renamed,
            });
        }

        private static Dictionary<string, string> TransliterateRemainingNonASCIIBatch(IReadOnlyList<string> values, UMTResources umtResources)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            List<string> japaneseCandidates = new List<string>();
            HashSet<string> japaneseCandidateSet = new HashSet<string>();
            for (int i = 0; i < values.Count; ++i)
            {
                string value = values[i];
                if (string.IsNullOrEmpty(value) || IsASCII(value))
                {
                    result[value] = value;
                    continue;
                }

                if (HasPotentialJapaneseCharacters(value) && japaneseCandidateSet.Add(value))
                {
                    japaneseCandidates.Add(value);
                }
            }
            Dictionary<string, string> romajiCandidates = ConvertJapaneseToRomajiBatchWithKawazu(japaneseCandidates, umtResources);
            for (int i = 0; i < values.Count; ++i)
            {
                string originalValue = values[i];
                if (result.ContainsKey(originalValue))
                {
                    continue;
                }

                string value = romajiCandidates.TryGetValue(originalValue, out string converted) ? converted : originalValue;
                if (HasCJKCharacters(value))
                {
                    value = ConvertCJKToPinyin(value, umtResources);
                }

                result[originalValue] = value;
            }

            return result;
        }

        private static Dictionary<string, string> ConvertJapaneseToRomajiBatchWithKawazu(IReadOnlyList<string> values, UMTResources umtResources)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (values == null || values.Count == 0)
            {
                return result;
            }

            KawazuConverter converter = GetKawazuConverter(umtResources);
            string separator = "\u001FPRS\u001E";

            string joined = string.Join(separator, values);
            string romaji = RemoveDiacritics(converter.ConvertSync(joined, To.Romaji, Mode.Normal, RomajiSystem.Hepburn, null, null));
            string[] convertedValues = (romaji ?? string.Empty).Split(new[] { separator }, StringSplitOptions.None);

            for (int i = 0; i < values.Count; ++i)
            {
                result[values[i]] = string.IsNullOrWhiteSpace(convertedValues[i]) ? values[i] : convertedValues[i];
            }

            return result;
        }

        private static string ConvertCJKToPinyin(string value, UMTResources umtResources)
        {
            if (umtResources == null)
            {
                throw new InvalidOperationException($"{nameof(UMTResources)} is required for Chinese pinyin conversion.");
            }

            if (m_PinyinDictionaryResources != umtResources)
            {
                PinyinConvert.LoadDictionary(umtResources.GetPinyinDictionaryText());
                m_PinyinDictionaryResources = umtResources;
            }

            string pinyin = PinyinConvert.GetPinyin(value);
            return string.IsNullOrWhiteSpace(pinyin) ? value : RemoveDiacritics(pinyin).Replace("|", string.Empty);
        }

        /// <summary>
        /// Removes combining diacritic marks from a string, returning a recomposed result.
        /// </summary>
        /// <param name="text">Text to strip of diacritics.</param>
        /// <returns>The text with non-spacing marks removed, or the input unchanged when null or whitespace.</returns>
        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            string normalizedString = text.Normalize(NormalizationForm.FormD);
            StringBuilder stringBuilder = new StringBuilder();

            foreach (char c in normalizedString)
            {

                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static bool HasPotentialJapaneseCharacters(string value)
        {
            return HasJapaneseSpecificCharacters(value) || HasCJKCharacters(value);
        }

        private static bool HasJapaneseSpecificCharacters(string value)
        {
            return ContainsCodePointInRanges(value, IsJapaneseSpecificCodePoint);
        }

        private static bool HasKoreanCharacters(string value)
        {
            return ContainsCodePointInRanges(value, IsKoreanCodePoint);
        }

        private static bool HasCJKCharacters(string value)
        {
            return ContainsCodePointInRanges(value, IsCJKCodePoint);
        }

        private static bool ContainsCodePointInRanges(string value, Func<int, bool> predicate)
        {
            foreach (int codePoint in EnumerateCodePoints(value))
            {
                if (predicate(codePoint))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<int> EnumerateCodePoints(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                yield break;
            }

            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    yield return char.ConvertToUtf32(c, value[++i]);
                }
                else
                {
                    yield return c;
                }
            }
        }

        private static bool IsJapaneseSpecificCodePoint(int codePoint)
        {
            return IsCodePointInRange(codePoint, k_HiraganaStart, k_HiraganaEnd) || IsCodePointInRange(codePoint, k_KatakanaStart, k_KatakanaEnd) || IsCodePointInRange(codePoint, k_KatakanaPhoneticExtensionsStart, k_KatakanaPhoneticExtensionsEnd) || IsCodePointInRange(codePoint, k_KanaSupplementStart, k_KanaSupplementEnd) || IsCodePointInRange(codePoint, k_KanaExtendedAStart, k_KanaExtendedAEnd) || IsCodePointInRange(codePoint, k_SmallKanaExtensionStart, k_SmallKanaExtensionEnd) || IsCodePointInRange(codePoint, k_HalfwidthKatakanaStart, k_HalfwidthKatakanaEnd);
        }

        private static bool IsKoreanCodePoint(int codePoint)
        {
            return IsCodePointInRange(codePoint, k_HangulJamoStart, k_HangulJamoEnd) || IsCodePointInRange(codePoint, k_HangulCompatibilityJamoStart, k_HangulCompatibilityJamoEnd) || IsCodePointInRange(codePoint, k_HangulJamoExtendedAStart, k_HangulJamoExtendedAEnd) || IsCodePointInRange(codePoint, k_HangulSyllablesStart, k_HangulSyllablesEnd) || IsCodePointInRange(codePoint, k_HangulJamoExtendedBStart, k_HangulJamoExtendedBEnd);
        }

        private static bool IsCJKCodePoint(int codePoint)
        {
            return IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionAStart, k_CJKUnifiedIdeographsExtensionAEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsStart, k_CJKUnifiedIdeographsEnd) || IsCodePointInRange(codePoint, k_CJKCompatibilityIdeographsStart, k_CJKCompatibilityIdeographsEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionBStart, k_CJKUnifiedIdeographsExtensionBEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionCStart, k_CJKUnifiedIdeographsExtensionCEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionDStart, k_CJKUnifiedIdeographsExtensionDEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionEStart, k_CJKUnifiedIdeographsExtensionEEnd) || IsCodePointInRange(codePoint, k_CJKUnifiedIdeographsExtensionFStart, k_CJKUnifiedIdeographsExtensionFEnd) || IsCodePointInRange(codePoint, k_CJKCompatibilityIdeographsSupplementStart, k_CJKCompatibilityIdeographsSupplementEnd);
        }

        private static bool IsCodePointInRange(int codePoint, int start, int end)
        {
            return codePoint >= start && codePoint <= end;
        }

        private static KawazuConverter GetKawazuConverter(UMTResources umtResources)
        {
            if (umtResources == null)
            {
                throw new InvalidOperationException($"{nameof(UMTResources)} is required for Japanese romanization.");
            }

            if (m_KawazuConverter != null && m_KawazuConverterResources == umtResources)
            {
                return m_KawazuConverter;
            }

            KawazuDictionaryData dictionaryData = umtResources.GetKawazuDictionaryData();

            DisposeKawazuConverter();

            string dictionaryPath = CreateDictionaryPath(dictionaryData.charBin, dictionaryData.matrixBin, dictionaryData.systemDictionary, dictionaryData.unknownDictionary);
            m_KawazuConverter = new KawazuConverter(dictionaryPath);
            m_KawazuDictionaryPath = dictionaryPath;
            m_KawazuConverterResources = umtResources;
            return m_KawazuConverter;
        }

        private static string CreateDictionaryPath(byte[] charBin, byte[] matrixBin, byte[] systemDictionary, byte[] unknownDictionary)
        {
            if (charBin == null)
            {
                throw new ArgumentNullException(nameof(charBin));
            }

            if (matrixBin == null)
            {
                throw new ArgumentNullException(nameof(matrixBin));
            }

            if (systemDictionary == null)
            {
                throw new ArgumentNullException(nameof(systemDictionary));
            }

            if (unknownDictionary == null)
            {
                throw new ArgumentNullException(nameof(unknownDictionary));
            }

            string path = Path.Combine(Application.temporaryCachePath, "UMT", "Kawazu", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "char.bin"), charBin);
            File.WriteAllBytes(Path.Combine(path, "matrix.bin"), matrixBin);
            File.WriteAllBytes(Path.Combine(path, "sys.dic"), systemDictionary);
            File.WriteAllBytes(Path.Combine(path, "unk.dic"), unknownDictionary);
            return path;
        }

        private static void DisposeKawazuConverter()
        {
            m_KawazuConverter?.Dispose();
            m_KawazuConverter = null;
            m_KawazuConverterResources = null;

            if (!string.IsNullOrEmpty(m_KawazuDictionaryPath) && Directory.Exists(m_KawazuDictionaryPath))
            {
                Directory.Delete(m_KawazuDictionaryPath, true);
            }

            m_KawazuDictionaryPath = null;
        }

        private static string ToASCII(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            StringBuilder builder = new StringBuilder(value.Length + 1);
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                bool isLetter = c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';
                if (isLetter || isDigit || c == '_')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                    if (char.IsHighSurrogate(c) &&
                        i + 1 < value.Length &&
                        char.IsLowSurrogate(value[i + 1]))
                    {
                        ++i;
                    }
                }
            }

            if (builder.Length > 0 && builder[0] >= '0' && builder[0] <= '9')
            {
                builder.Insert(0, '_');
            }

            return builder.ToString();
        }

        private static bool IsASCII(string value)
        {
            foreach (char c in value)
            {
                if (c > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

    }
}
