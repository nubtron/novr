using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

/// <summary>
/// An in-headset notice for the one failure a VR pilot has no way to see: the
/// game window has lost the Windows foreground, so the game keeps rendering
/// into the headset but stops being given any input.
///
/// <para>Why it needs saying at all: in VR the usual tell is gone. On a flat
/// screen an unfocused window is obvious — it is behind another one, its title
/// bar is grey, and the thing you clicked instead is right there. In a headset
/// the picture is unchanged, because the compositor keeps being handed frames;
/// the head still tracks, because tracking comes from the runtime rather than
/// from the window. The only symptom is that nothing responds, which is
/// indistinguishable from the mod having broken.</para>
///
/// <para>The cursor makes it worse by being correct. <see cref="VrUiCursor"/>
/// hides itself outright while <c>Application.isFocused</c> is false — it has
/// to, because the desktop pointer it is driven from now belongs to whatever
/// window took the foreground, so a cursor left on screen would sit at a
/// meaningless position and click on things. So the cursor does not freeze in
/// place, which would at least look like a stuck pointer: it disappears
/// completely, and "the cursor is gone" is what the pilot reports.</para>
///
/// <para>Head-locked rather than anchored in the world, which is the opposite
/// of what the recenter widget does. That widget is world-anchored so the
/// gaze cursor — always at the centre of the view — can be aimed at it; this
/// panel is never aimed at, because no input is arriving, and that is the
/// entire message. What it has to be instead is impossible to miss, which
/// means staying in front of the head wherever the head goes.</para>
///
/// <para>Everything here runs on <see cref="Time.unscaledTime"/>. A game that
/// has lost focus is very often also a game sitting at <c>timeScale</c> zero —
/// paused, or holding an opening briefing — and a warning that only appears
/// while time is running would be absent from most of the cases it exists
/// for.</para>
/// </summary>
public class FocusWarning : NOVRBehaviour
{
    private const float DistanceMeters = 1.4f;
    private const float VerticalOffsetMeters = -0.25f;
    private const float CanvasScale = 0.0016f;
    private static readonly Vector2 CanvasSize = new(720f, 190f);

    private const string Headline = "GAME WINDOW NOT IN FOCUS";

    private const string Body =
        "Nuclear Option is not the foreground window, so Windows is sending it no "
        + "input: the cursor is hidden and the stick, mouse and keyboard do nothing. "
        + "Click the game window on the desktop to give it back.";

    private GameObject? _root;
    private Canvas? _canvas;

    /// <summary>
    /// When focus was lost, on the unscaled clock, or -1 while focused. The
    /// warning waits out <see cref="FocusWarningConfig.DelaySeconds"/> from
    /// here so that the brief unfocused moment every alt-tab and every Steam
    /// overlay produces does not flash a panel in the pilot's face.
    /// </summary>
    private float _unfocusedSince = -1f;

    private bool _loggedLoss;

    private void Update()
    {
        if (!FocusWarningConfig.Enabled)
        {
            SetVisible(false);
            return;
        }

        if (Application.isFocused)
        {
            if (_loggedLoss)
            {
                Debug.Log($"[{nameof(FocusWarning)}] Game window regained focus; input is being delivered again.");
                _loggedLoss = false;
            }

            _unfocusedSince = -1f;
            SetVisible(false);
            return;
        }

        if (_unfocusedSince < 0f)
        {
            _unfocusedSince = Time.unscaledTime;
        }

        if (Time.unscaledTime - _unfocusedSince < FocusWarningConfig.DelaySeconds)
        {
            SetVisible(false);
            return;
        }

        if (!_loggedLoss)
        {
            Debug.Log(
                $"[{nameof(FocusWarning)}] Game window lost foreground focus; no input is being "
                + "delivered and the VR cursor is hidden. Showing the in-headset warning.");
            _loggedLoss = true;
        }

        if (!EnsurePanel()) return;

        SetVisible(true);
        UpdatePlacement();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        SetVisible(false);
    }

    /// <summary>
    /// Build the panel the first time it is actually needed. A pilot who never
    /// alt-tabs never pays for it, and — more to the point — the VR UI it hangs
    /// off does not exist yet when this component is constructed.
    /// </summary>
    private bool EnsurePanel()
    {
        if (_root != null) return true;
        if (NOUIManager.I == null) return false;

        _root = new GameObject("NOVR Focus Warning");

        var rectTransform = _root.AddComponent<RectTransform>();
        rectTransform.sizeDelta = CanvasSize;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);

        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.overrideSorting = true;

        // Above the recenter widget's 6000: this is the one panel that must not
        // be drawn behind anything, because it explains why nothing else works.
        _canvas.sortingOrder = 6100;

        CreatePanel(rectTransform);

        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
        _root.SetActive(false);
        return true;
    }

    private static void CreatePanel(RectTransform parent)
    {
        var background = CreateChild("Background", parent, CanvasSize, Vector2.zero);
        var image = background.gameObject.AddComponent<Image>();
        image.color = new Color(0.34f, 0.08f, 0.08f, 0.94f);
        image.raycastTarget = false;

        CreateText(
            "Headline",
            background,
            Headline,
            new Vector2(0f, 58f),
            new Vector2(CanvasSize.x - 48f, 46f),
            34,
            new Color(1f, 0.86f, 0.42f));

        CreateText(
            "Body",
            background,
            Body,
            new Vector2(0f, -24f),
            new Vector2(CanvasSize.x - 64f, 108f),
            22,
            Color.white);
    }

    private static RectTransform CreateChild(string name, RectTransform parent, Vector2 size, Vector2 anchoredPosition)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);

        var rectTransform = gameObject.AddComponent<RectTransform>();
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = anchoredPosition;
        return rectTransform;
    }

    private static void CreateText(
        string name,
        RectTransform parent,
        string text,
        Vector2 anchoredPosition,
        Vector2 size,
        int fontSize,
        Color color)
    {
        var rectTransform = CreateChild(name, parent, size, anchoredPosition);

        var textComponent = rectTransform.gameObject.AddComponent<Text>();
        textComponent.text = text;
        textComponent.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        textComponent.fontSize = fontSize;
        textComponent.alignment = TextAnchor.MiddleCenter;
        textComponent.color = color;
        textComponent.horizontalOverflow = HorizontalWrapMode.Wrap;
        textComponent.verticalOverflow = VerticalWrapMode.Truncate;
        textComponent.raycastTarget = false;
    }

    private void SetVisible(bool visible)
    {
        if (_root == null) return;
        if (_root.activeSelf == visible) return;

        _root.SetActive(visible);
    }

    /// <summary>
    /// Hold the panel in front of the head. Parented to the same smoothed
    /// forward reference the cockpit HUD uses rather than to the camera
    /// directly, so it inherits the smoothing the rest of the VR UI has and
    /// does not judder against it.
    /// </summary>
    private void UpdatePlacement()
    {
        if (_root == null) return;

        var manager = NOUIManager.I;
        if (manager == null) return;

        var reference = manager.CockpitHudReference;
        if (reference == null) return;

        var referenceTransform = reference.transform;
        if (_root.transform.parent != referenceTransform)
        {
            _root.transform.SetParent(referenceTransform, false);
        }

        _root.transform.localPosition = new Vector3(0f, VerticalOffsetMeters, DistanceMeters);
        _root.transform.localRotation = Quaternion.identity;
        _root.transform.localScale = Vector3.one * CanvasScale;

        if (_canvas != null)
        {
            _canvas.worldCamera = manager.CockpitHudCamera;
        }
    }
}

/// <summary>
/// The settings live here rather than in ModConfiguration because this mod is
/// developed as a stack of independent branches, and a setting declared in the
/// shared file puts every layer that adds one in conflict with every other.
/// See <see cref="ConfigSectionAttribute"/>.
/// </summary>
[ConfigSection(Order = 56)]
public static class FocusWarningConfig
{
    private const string Section = "General";

    public static ConfigEntry<bool> FocusWarningEnabled;
    public static ConfigEntry<float> FocusWarningDelaySeconds;

    // Every read goes through these: ConfigSections catches a section that
    // fails to bind and carries on, so an entry can legitimately be null and
    // the warning must fail closed rather than throw every frame.
    public static bool Enabled => FocusWarningEnabled != null && FocusWarningEnabled.Value;

    public static float DelaySeconds =>
        FocusWarningDelaySeconds != null ? Mathf.Max(FocusWarningDelaySeconds.Value, 0f) : 1f;

    public static void Bind(ConfigFile config)
    {
        FocusWarningEnabled = config.Bind(
            Section,
            "Focus Warning",
            true,
            "Show a warning in the headset when the game window is not the foreground window on the "
            + "desktop. Windows sends input only to the foreground window, so a game that has lost it "
            + "keeps drawing and keeps tracking your head while ignoring the stick, the mouse and the "
            + "keyboard entirely — and the VR cursor hides itself, because the desktop pointer it "
            + "follows now belongs to another window. None of that is visible from inside a headset: "
            + "the view still moves, so it reads as the mod having broken rather than as a window "
            + "needing a click. Turn it off if you deliberately fly with the game in the background.");

        FocusWarningDelaySeconds = config.Bind(
            Section,
            "Focus Warning Delay",
            1f,
            new ConfigDescription(
                "How long the game must have been out of focus before the warning appears. Alt-tabbing, "
                + "the Steam overlay and the headset's own dashboard all drop focus for a moment in "
                + "normal use, and a panel that appears for each of them is worse than the problem it "
                + "reports. One second is long enough to sit out the ordinary cases and short enough "
                + "that a real loss of focus is explained before it is puzzled over. 0 shows it "
                + "immediately.",
                new AcceptableValueRange<float>(0f, 10f)));
    }
}
