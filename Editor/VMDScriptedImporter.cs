using UMT;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;

namespace UMT.Editor
{
    /// <summary>
    /// Unity <see cref="ScriptedImporter"/> for <c>.vmd</c> assets that parses the motion data into a
    /// <see cref="VMDAnimation"/> main object.
    /// </summary>
    [ScriptedImporter(1, new[] { "vmd" })]
    public sealed class VMDScriptedImporter : ScriptedImporter
    {
        /// <summary>
        /// Reads and parses the <c>.vmd</c> file into a <see cref="VMDAnimation"/> and sets it as the main asset object.
        /// </summary>
        /// <param name="ctx">Asset import context for the source VMD file.</param>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            byte[] bytes = File.ReadAllBytes(ctx.assetPath);
            VMDAnimation animation = VMDReader.Read(bytes);
            animation.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset(animation.name, animation);
            ctx.SetMainObject(animation);
        }
    }
}
