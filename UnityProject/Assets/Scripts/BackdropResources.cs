using UnityEngine;

namespace TikTokLiveGame
{
    // The backdrop owns its texture copy and runtime materials, not the Resources asset.
    public sealed class BackdropResources : MonoBehaviour
    {
        private Texture2D texture;
        private Material[] materials;

        public void Initialize(Texture2D ownedTexture, params Material[] ownedMaterials)
        {
            texture = ownedTexture;
            materials = ownedMaterials;
        }

        private void OnDestroy()
        {
            if (texture != null) Destroy(texture);
            if (materials != null)
                foreach (Material material in materials)
                    if (material != null) Destroy(material);
        }
    }
}
