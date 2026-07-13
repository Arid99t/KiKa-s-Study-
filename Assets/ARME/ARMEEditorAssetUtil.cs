#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared editor-only helpers for the ensemble auto-wire context menus. Extracted verbatim from
/// the previously identical copies in ARMEUserStudyController, ARMEEnsembleSyncPlayer and
/// ARMEOnsetBasedEnsembleController so the logic lives in one place. Behaviour is unchanged.
/// </summary>
public static class ARMEEditorAssetUtil
{
    /// <summary>Strip the quartet "_TB" (top/bottom) suffix from a clip name, if present.</summary>
    public static string StripTopBottomSuffix(string clipName)
        => clipName.EndsWith("_TB") ? clipName.Substring(0, clipName.Length - 3) : clipName;

    /// <summary>Find an asset of type T whose file name (without extension) exactly matches, within a folder.</summary>
    public static T FindAsset<T>(string assetName, string folder) where T : UnityEngine.Object
    {
        foreach (string guid in AssetDatabase.FindAssets($"t:{typeof(T).Name}", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == assetName)
                return AssetDatabase.LoadAssetAtPath<T>(path);
        }
        return null;
    }
}
#endif
