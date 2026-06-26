namespace UMT
{
    /// <summary>
    /// Shared MMD-format constants used across the PMX/VMD import and runtime pipeline.
    /// </summary>
    public static class MMDConstants
    {
        /// <summary>
        /// Scale factor converting MMD units into Unity units (one MMD unit equals 0.08 Unity units).
        /// </summary>
        public const float k_MMDUnitToUnityUnit = 0.08f;

        /// <summary>
        /// The ASCII signature expected at the start of a PMX file header.
        /// </summary>
        public const string k_PMXHeaderSignature = "PMX ";

        /// <summary>
        /// The number of bytes occupied by the PMX header signature.
        /// </summary>
        public const int k_PMXHeaderSignatureByteCount = 4;

        /// <summary>
        /// The PMX version targeted and fully supported by the importer.
        /// </summary>
        public const float k_SupportedPMXVersion = 2.0f;

        /// <summary>
        /// The number of globals/data flags stored in the PMX header.
        /// </summary>
        public const byte k_PMXHeaderDataCount = 8;

        /// <summary>
        /// The maximum number of additional UV channels a PMX vertex may declare.
        /// </summary>
        public const byte k_MaxPMXAdditionalUVCount = 4;
    }
}
