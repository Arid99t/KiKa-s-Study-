using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Shared parsing for ARME onset files (one timestamp per line; blank lines and '#'-comments
/// are ignored). Extracted verbatim from the previously duplicated parsers so behaviour is
/// unchanged: invariant-culture float parsing, negative values rejected.
///
/// NOTE: <see cref="ARMEPlayback.ARMEOnsetBasedPlaybackController"/> keeps its own parser on
/// purpose — it uses current-culture parsing plus per-line warnings, which differ from these,
/// so it is intentionally NOT routed here.
/// </summary>
public static class ARMEOnsetUtil
{
    /// <summary>Parse every valid onset time (seconds), sorted ascending. Never returns null.</summary>
    public static List<float> ParseAll(TextAsset file)
    {
        var list = new List<float>();
        if (file == null)
            return list;

        foreach (var line in file.text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#"))
                continue;
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0f)
                list.Add(v);
        }
        list.Sort();
        return list;
    }

    /// <summary>First valid onset time in file order, or 0 if none.</summary>
    public static float ParseFirst(TextAsset file)
    {
        if (file == null)
            return 0f;

        foreach (var line in file.text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#"))
                continue;
            if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0f)
                return v;
        }
        return 0f;
    }
}
