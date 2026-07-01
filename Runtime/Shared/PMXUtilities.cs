using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Small shared helpers for PMX import: model naming, file-name sanitization, unique object naming, warning collection, and runtime object destruction.
    /// </summary>
    public static class PMXUtilities
    {
        /// <summary>
        /// Resolves a display name for the model, preferring the option source name, then the PMX Japanese name, then the PMX English name, then the source file name, falling back to <c>"PMXModel"</c>.
        /// </summary>
        /// <param name="model">The parsed PMX model whose model info is consulted.</param>
        /// <param name="options">The import options that may supply a source name or source path.</param>
        /// <returns>The resolved model name.</returns>
        public static string GetModelName(PMXModel model, PMXImportOptions options)
        {
            if (!string.IsNullOrEmpty(options.sourceName))
            {
                return options.sourceName;
            }
            string modelName = model.modelInfo.name.ToString();
            if (!string.IsNullOrEmpty(modelName))
            {
                return modelName;
            }
            string modelNameEN = model.modelInfo.nameEN.ToString();
            if (!string.IsNullOrEmpty(modelNameEN))
            {
                return modelNameEN;
            }
            return string.IsNullOrEmpty(options.sourcePath) ? "PMXModel" : Path.GetFileNameWithoutExtension(options.sourcePath);
        }

        /// <summary>
        /// Produces a file-system-safe name by substituting a default when the input is blank and replacing invalid file-name characters with underscores.
        /// </summary>
        /// <param name="value">The candidate file name.</param>
        /// <param name="index">An index appended to the default name when <paramref name="value"/> is blank; ignored when negative.</param>
        /// <returns>The sanitized, trimmed file name.</returns>
        public static string SanitizeFileName(string value, int index)
        {
            string sanitized = value;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (index >= 0)
                {
                    sanitized = $"PMXModel_{index}";
                }
                else
                {
                    sanitized = "PMXModel";
                }
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalid, '_');
            }

            return sanitized.Trim();
        }

        /// <summary>
        /// Returns the renamed object name, or a generated <c>"{fallbackPrefix}_{index}"</c> name when the rename is empty.
        /// </summary>
        /// <param name="renamedName">The preferred renamed object name.</param>
        /// <param name="fallbackPrefix">The prefix used to build a fallback name.</param>
        /// <param name="index">The index appended to the fallback prefix.</param>
        /// <returns>The chosen object name.</returns>
        public static string GetGeneratedObjectName(string renamedName, string fallbackPrefix, int index)
        {
            return string.IsNullOrEmpty(renamedName) ? $"{fallbackPrefix}_{index}" : renamedName;
        }

        /// <summary>
        /// Builds an object name that is unique among the children of <paramref name="parent"/>, combining a prefix with the renamed or generated base name and appending a numeric suffix on collision.
        /// </summary>
        /// <param name="parent">The parent transform whose children are checked for name collisions.</param>
        /// <param name="renamedName">The preferred renamed object name.</param>
        /// <param name="prefix">A prefix prepended to the base name.</param>
        /// <param name="fallbackPrefix">The prefix used to build a fallback name when the rename is empty.</param>
        /// <param name="index">The index appended to the fallback prefix.</param>
        /// <param name="excludedChild">An optional child to exclude from collision checks (for example, the object being renamed).</param>
        /// <returns>A name unique among the parent's children.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="parent"/> is <c>null</c>.</exception>
        public static string GetUniqueGeneratedObjectName(Transform parent, string renamedName, string prefix, string fallbackPrefix, int index, Transform excludedChild = null)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            string baseName = prefix + GetGeneratedObjectName(renamedName, fallbackPrefix, index);
            return GetUniqueName(baseName, candidate => HasChildWithName(parent, candidate, excludedChild));
        }

        /// <summary>
        /// Builds the slash-separated hierarchy path of a transform relative to a root, excluding the root itself.
        /// </summary>
        /// <param name="current">The transform whose path is built.</param>
        /// <param name="root">The ancestor transform that terminates the path; when <c>null</c>, only the transform's own name is returned.</param>
        /// <returns>The relative transform path.</returns>
        public static string GetTransformPathFromRoot(this Transform current, Transform root)
        {
            if (root == null) return current.name;

            StringBuilder pathBuilder = new StringBuilder(current.name);
            Transform tempTransform = current.parent;

            while (tempTransform != null && tempTransform != root)
            {
                pathBuilder.Insert(0, "/");
                pathBuilder.Insert(0, tempTransform.name);
                tempTransform = tempTransform.parent;
            }

            return pathBuilder.ToString();
        }
        /// <summary>
        /// Returns a name unique according to the supplied existence predicate, appending an incrementing numeric suffix until the predicate reports the name as unused.
        /// </summary>
        /// <param name="baseName">The desired base name.</param>
        /// <param name="nameExists">A predicate returning <c>true</c> when a candidate name is already taken.</param>
        /// <returns>A name for which <paramref name="nameExists"/> returns <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="nameExists"/> is <c>null</c>.</exception>
        public static string GetUniqueName(string baseName, Func<string, bool> nameExists)
        {
            if (nameExists == null)
            {
                throw new ArgumentNullException(nameof(nameExists));
            }

            string uniqueName = baseName;
            int suffix = 1;
            while (nameExists(uniqueName))
            {
                uniqueName = $"{baseName}_{suffix++}";
            }

            return uniqueName;
        }

        /// <summary>
        /// Records a deduplicated warning on the import result (when provided) and logs it via <see cref="Debug.LogWarning(object)"/>.
        /// </summary>
        /// <param name="result">The import result to collect the warning into; may be <c>null</c>.</param>
        /// <param name="message">The warning message.</param>
        public static void AddWarning(PMXImportResult result, string message)
        {
            if (result != null)
            {
                if (result.warnings.Contains(message))
                {
                    return;
                }
                result.warnings.Add(message);
            }
            Debug.LogWarning($"[PMX Import] {message}");
        }

        /// <summary>
        /// Destroys a Unity object, choosing <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/> in play mode and <see cref="UnityEngine.Object.DestroyImmediate(UnityEngine.Object)"/> otherwise.
        /// </summary>
        /// <param name="value">The object to destroy; ignored when <c>null</c>.</param>
        public static void DestroyRuntimeObject(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private static bool HasChildWithName(Transform parent, string childName, Transform excludedChild)
        {
            for (int i = 0; i < parent.childCount; ++i)
            {
                Transform child = parent.GetChild(i);
                if (child != excludedChild && string.Equals(child.name, childName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Disposes any existing persistent native array and replaces it with a newly allocated one of the given length.
        /// </summary>
        /// <typeparam name="T">The unmanaged element type of the native array.</typeparam>
        /// <param name="array">The native array reference to dispose and reallocate.</param>
        /// <param name="count">The element count of the new persistent array.</param>
        public static void ResizePersistent<T>(ref NativeArray<T> array, int count) where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }

            array = new NativeArray<T>(count, Allocator.Persistent);
        }
    }
}
