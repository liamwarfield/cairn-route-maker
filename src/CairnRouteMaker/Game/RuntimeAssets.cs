using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Game;

internal static class RuntimeAssets
{
    /// <summary>
    /// Protect a runtime asset from Resources.UnloadUnusedAssets, which the game runs on zone
    /// changes: anything only referenced from mod code counts as unused and gets destroyed
    /// (DontDestroyOnLoad only covers GameObjects).
    /// </summary>
    public static T Keep<T>(T asset)
        where T : Object
    {
        if (asset != null)
        {
            asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }

        return asset;
    }
}
