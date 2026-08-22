using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Stop the weapon-safety wash blowing out to white on the captured HUD.
///
/// <para>Lowering the gear engages the weapon safety
/// (<c>WeaponStation.SafetyIsOn</c> is true for any gear state other than
/// <c>LockedRetracted</c>), and <c>WeaponStatus.UpdateSafety</c> — subscribed to
/// <c>Aircraft.onSetGear</c> — enables <c>safetyImage</c>: the <c>weaponDisabled</c>
/// graphic, a stock white <c>Background</c> sprite tinted <c>(0.689, 0.689,
/// 0.689, 0.8)</c> stretched over the whole 240x100 weapon panel and drawn last.
/// Flat, that alpha-composites to a light grey wash over the readout, which is
/// how the game says "safed".</para>
///
/// <para><b>An additive panel cannot wash anything out — it can only add light.</b>
/// The capture is premultiplied, so the wash lands in the texture as a solid
/// opaque grey (measured: <c>133,133,133,255</c>, the alpha already saturated by
/// the panel backing underneath it), and
/// <see cref="FlightHudCaptureBackend.CreatePanelMaterial"/> adds that at
/// <c>Captured HUD Brightness</c>: 0.52 x 2 = 1.04, clipped. Measured on the eye
/// render, the weapon panel comes out <c>255,255,255</c> — a solid white block,
/// the most prominent thing on the HUD, where the game meant the least.</para>
///
/// <para>Nothing else on the HUD does this. The symbology is saturated (green
/// x 2 is still green) and the backings are black sprites whose alpha is the
/// mask — the case <c>HmdVisorBackend</c>'s shade quad was built for. This one
/// graphic is the odd one out: a mid-grey with meaningful alpha, i.e. neither
/// symbology nor mask.</para>
///
/// <para><b>So the wash is re-tinted to undo the boost, and only the boost.</b>
/// The capture already holds the flat game's own composited pixel — the same
/// canvas, the same blend, the same encoding — so the only thing standing
/// between it and the flat appearance is <c>Captured HUD Brightness</c>.
/// Dividing the tint by it puts the wash back where the game drew it: at the
/// 2.0 default, 0.689 becomes 0.344. No gamma term, unlike
/// <c>HmdVisorBackend.ShadePanel</c> — that one converts because its operand is
/// alpha, which is never gamma-encoded, multiplying scene light; both operands
/// here live in the capture's own encoding. Measured on the eye render: the
/// wash lands at 150/255 against the 140/255 the flat game composites, where the
/// gamma-converted version undershot to 81.</para>
///
/// <para>The alpha is left exactly as authored, so the wash goes on dimming the
/// symbology beneath it by the same 20% the flat game does.</para>
///
/// <para>The game never writes this colour itself (<c>WeaponStatus</c> only
/// toggles <c>enabled</c>), so holding a tint on it needs no per-frame fight —
/// but the authored value is re-read whenever someone else does change it, and
/// restored when the capture stands down.</para>
/// </summary>
internal static class WeaponSafetyWash
{
    private static readonly FieldInfo? SafetyImageField =
        AccessTools.Field(typeof(global::WeaponStatus), "safetyImage");

    private static readonly FieldInfo? WeaponStatusField =
        AccessTools.Field(typeof(global::CombatHUD), "weaponStatus");

    private const float RebindInterval = 0.5f;

    private static Image? _image;
    private static Color _authored;
    private static Color _applied;
    private static bool _held;
    private static float _nextRebind;
    private static bool _logged;

    /// <summary>
    /// Hold the compensated tint, rebinding if the HUD has been rebuilt.
    /// Cheap enough to call every frame: it writes only when the value moves.
    /// </summary>
    internal static void Apply(float brightness)
    {
        if (_image == null)
        {
            if (Time.unscaledTime < _nextRebind) return;
            _nextRebind = Time.unscaledTime + RebindInterval;
            if (!Bind()) return;
        }

        // Someone else moved it — a theme change, a build where the sprite is
        // authored differently — so that is the new authored value.
        if (_held && _image!.color != _applied) _authored = _image.color;

        var wanted = Compensate(_authored, brightness);
        if (_held && _image!.color == wanted) return;

        _image!.color = wanted;
        _applied = wanted;
        _held = true;

        if (_logged) return;
        _logged = true;
        Debug.Log($"[NOVR] Captured HUD: weapon safety wash re-tinted {_authored.r:F3} -> " +
                  $"{wanted.r:F3} (alpha {_authored.a:F2} kept) so it composites as the flat " +
                  $"game's grey instead of clipping to white at brightness {brightness:F2}.");
    }

    /// <summary>Put the game's own colour back.</summary>
    internal static void Restore()
    {
        if (_held && _image != null) _image.color = _authored;
        _image = null;
        _held = false;
        _nextRebind = 0f;
    }

    private static bool Bind()
    {
        var hud = SceneSingleton<CombatHUD>.i;
        if (hud == null || SafetyImageField == null || WeaponStatusField == null) return false;

        // The weapon readout is reached through the HUD's own reference rather
        // than by searching for a name: CombatHUD owns the WeaponStatus, and
        // WeaponStatus owns the graphic it enables.
        if (WeaponStatusField?.GetValue(hud) is not global::WeaponStatus status) return false;

        if (SafetyImageField.GetValue(status) is not Image image) return false;

        _image = image;
        _authored = image.color;
        _held = false;
        return true;
    }

    /// <summary>
    /// The tint that makes an additive panel driven at <paramref name="brightness"/>
    /// display the wash at the level the flat game composites it to: the
    /// authored colour with the brightness boost taken back out.
    /// </summary>
    private static Color Compensate(Color authored, float brightness)
    {
        if (brightness <= 1f) return authored;

        return new Color(
            Mathf.Clamp01(authored.r / brightness),
            Mathf.Clamp01(authored.g / brightness),
            Mathf.Clamp01(authored.b / brightness),
            authored.a);
    }
}
