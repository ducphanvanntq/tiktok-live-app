#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TikTokLiveGame.Editor
{
    public sealed class WelcomeTextureImporter : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Resources/Welcome/")) return;
            TextureImporter importer = (TextureImporter)assetImporter;
            // The toast draws atlas UVs itself, preserving the measured frame pivots.
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
        }
    }
}
#endif
