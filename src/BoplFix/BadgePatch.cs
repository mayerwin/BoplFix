using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BoplLanFix
{
    /// <summary>
    /// Adds a small "+ BoplFix vX" badge near the main-menu version number, so it's obvious the mod is
    /// loaded.
    ///
    /// This builds a SEPARATE, standalone text element and positions it relative to the rendered
    /// "2.5.1" - it NEVER modifies the version label (no appending, no alignment change, no cloning),
    /// so "2.5.1" stays exactly where the game put it. The badge is right-aligned so its right edge
    /// lines up with the version's right edge (same margin), and it's placed just below the version if
    /// there's room on screen, otherwise just above it.
    /// </summary>
    [HarmonyPatch(typeof(printText), "Awake")]
    internal static class PrintText_Version_Patch
    {
        private static void Postfix(printText __instance)
        {
            try
            {
                if (!Plugin.ShowBadge) return;
                var tmp = __instance.GetComponent<TMP_Text>();
                if (tmp == null) return;
                var parent = tmp.transform.parent;
                if (parent == null || parent.Find("BoplFixBadge") != null) return; // idempotent

                tmp.ForceMeshUpdate();
                // Use the ACTUAL rendered glyph extents (not textBounds, which has padding that makes
                // the badge overhang). Right edge = rightmost visible glyph; top/bottom = ascender/descender.
                var ti = tmp.textInfo;
                float rightLocal = float.NegativeInfinity, topLocal = float.NegativeInfinity, botLocal = float.PositiveInfinity;
                for (int i = 0; i < ti.characterCount; i++)
                {
                    var ci = ti.characterInfo[i];
                    if (!ci.isVisible) continue;
                    if (ci.topRight.x > rightLocal) rightLocal = ci.topRight.x;
                    if (ci.ascender > topLocal) topLocal = ci.ascender;
                    if (ci.descender < botLocal) botLocal = ci.descender;
                }
                if (float.IsInfinity(rightLocal))   // no visible glyphs -> fall back to bounds
                {
                    Bounds vb0 = tmp.textBounds;
                    rightLocal = vb0.max.x; topLocal = vb0.max.y; botLocal = vb0.min.y;
                }

                // Fresh, standalone element (do NOT touch the version label).
                var go = new GameObject("BoplFixBadge", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var b = go.AddComponent<TextMeshProUGUI>();
                if (tmp.font != null) b.font = tmp.font;
                if (tmp.fontSharedMaterial != null) b.fontSharedMaterial = tmp.fontSharedMaterial;
                b.enableAutoSizing = false;
                b.fontSize = tmp.fontSize * Mathf.Clamp(Plugin.BadgeSizePercent, 5, 100) / 100f;
                b.color = Plugin.BadgeColor();
                b.richText = true;
                b.enableWordWrapping = false;
                b.overflowMode = TextOverflowModes.Overflow;
                b.alignment = TextAlignmentOptions.BottomRight; // text right edge sits at the box's right edge
                b.text = "+ BoplFix v" + Plugin.Version;
                b.ForceMeshUpdate();

                var vRT = tmp.rectTransform;
                var bRT = b.rectTransform;
                bRT.anchorMin = vRT.anchorMin;
                bRT.anchorMax = vRT.anchorMax;
                bRT.sizeDelta = new Vector2(Mathf.Max(b.preferredWidth, 1f), Mathf.Max(b.preferredHeight, 1f));

                // World-space top-right / bottom-right of the rendered "2.5.1".
                Vector3 vTopRight = tmp.transform.TransformPoint(new Vector3(rightLocal, topLocal, 0f));
                Vector3 vBotRight = tmp.transform.TransformPoint(new Vector3(rightLocal, botLocal, 0f));
                float gap = (vTopRight.y - vBotRight.y) * 0.18f; // small gap, proportional to version height

                // Below if there's room on screen under the version, else above.
                var cam = tmp.canvas != null ? tmp.canvas.worldCamera : null;
                float vBotScreenY = RectTransformUtility.WorldToScreenPoint(cam, vBotRight).y;
                float vTopScreenY = RectTransformUtility.WorldToScreenPoint(cam, vTopRight).y;
                float badgeScreenH = (vTopScreenY - vBotScreenY) * (b.fontSize / Mathf.Max(tmp.fontSize, 1f)) + 6f;
                bool roomBelow = vBotScreenY > badgeScreenH + 8f;

                if (roomBelow)
                {
                    bRT.pivot = new Vector2(1f, 1f);              // top-right pivot -> badge hangs below
                    bRT.position = vBotRight - new Vector3(0f, gap, 0f);
                }
                else
                {
                    bRT.pivot = new Vector2(1f, 0f);              // bottom-right pivot -> badge sits above
                    bRT.position = vTopRight + new Vector3(0f, gap, 0f);
                }
            }
            catch (Exception e) { Plugin.Log?.LogError("PrintText badge patch: " + e); }
        }
    }
}
