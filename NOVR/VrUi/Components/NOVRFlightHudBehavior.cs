using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRFlightHudBehavior : UIRenderedCanvasBehavior
{
    private readonly Dictionary<Image, Vector2> _originalLineSizes = new Dictionary<Image, Vector2>();
    private float _appliedLineThickness = -1f;
    private int _frameCount;

    // HUD opacity state: the game renders the HUD with additive materials
    // (white lines saturate to white over bright sky/terrain), so we swap
    // every graphic to an alpha-blended material and raise its alpha.
    private readonly List<Graphic> _hudGraphics = new List<Graphic>();
    private readonly Dictionary<Graphic, Material> _opaqueMaterials = new Dictionary<Graphic, Material>();
    private float _appliedOpacity = -1f;
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static Shader _alphaBlendedUiShader;
    private static Shader _maskedUiShader;

    public override void Awake()
    {
        base.Awake();
        var hudcenter = FindChildStartingWith(transform, "HUDCenter");
        if (hudcenter != null) hudcenter.gameObject.AddComponent(typeof(NoVrHudBehavior));
        
        var hmdcenter = FindChildStartingWith(transform, "HMDCenter");
        if (hmdcenter != null) hmdcenter.gameObject.AddComponent(typeof(NOVRHMDBehavior));
        
        if (hudcenter != null)
        {
            MoveHmdPanelToHud("TopRightPanel", hudcenter, new Vector3(330, 290, 0f), new Vector3(0.6f, 0.6f, 0.6f));
            MoveHmdPanelToHud("LowerLeftPanel", hudcenter, new Vector3(-400f, 80f, 0f), new Vector3(0.6f, 0.6f, 0.6f));
        }

        var targetDesignator = FindChildStartingWith(transform, "targetDesignator");
        if (targetDesignator != null) targetDesignator.gameObject.AddComponent(typeof(NOVRTargetDesignatorBehavior));

        if (!gameObject.TryGetComponent<PitchCompassBehavior>(out _))
        {
            gameObject.AddComponent<PitchCompassBehavior>();
        }

        // var velocityVector = FindChildStartingWith(transform, "velocityVector");
        // if (velocityVector != null) velocityVector.gameObject.AddComponent(typeof(NOVRVelocityVectorBehavior));
    }
    
    private void Update()
    {
        transform.position = new Vector3(0f, 0f, 1000f);
        transform.rotation = Quaternion.identity;

        // Scale the whole flight HUD (both the HMD center and the cockpit HUD
        // center) instead of only the HMD subtree. Scaling the root also
        // brings the HUD elements closer to the view center, which the game's
        // built-in HUD width/height/side/top settings can then fine tune.
        var hudScale = Mathf.Clamp(ModConfiguration.Instance.VrHudScale.Value, 0.25f, 1.5f);
        transform.localScale = Vector3.one * hudScale;

        _frameCount++;
        if (_frameCount > 30 && _frameCount % 60 == 0)
        {
            ApplyHudLineThickness();
            ApplyHudOpacity();
        }
    }

    /// <summary>
    /// Thickens thin line-like UI elements of the main HUD (borders, brackets,
    /// tapes, the waterline, tick marks) by the HUD Line Thickness factor.
    /// Original sizes are captured once the game has finished laying out the
    /// HUD; changes to the config are re-applied live.
    /// </summary>
    private void ApplyHudLineThickness()
    {
        var thickness = Mathf.Clamp(ModConfiguration.Instance.HudLineThickness.Value, 0.5f, 3f);
        if (Mathf.Approximately(thickness, _appliedLineThickness))
        {
            return;
        }

        if (_originalLineSizes.Count == 0)
        {
            CaptureLineElements();
        }

        foreach (var pair in _originalLineSizes)
        {
            var image = pair.Key;
            if (image == null)
            {
                continue;
            }

            var originalSize = pair.Value;
            var thinDimension = Mathf.Min(Mathf.Abs(originalSize.x), Mathf.Abs(originalSize.y));
            var thickenedSize = originalSize;
            if (Mathf.Abs(originalSize.x) < Mathf.Abs(originalSize.y))
            {
                thickenedSize.x = thinDimension * thickness;
            }
            else
            {
                thickenedSize.y = thinDimension * thickness;
            }

            image.rectTransform.sizeDelta = thickenedSize;
        }

        _appliedLineThickness = thickness;
        Debug.Log($"{nameof(NOVRFlightHudBehavior)}: Applied HUD line thickness {thickness:F2} to {_originalLineSizes.Count} line elements");
    }

    /// <summary>
    /// Makes the VR HUD more opaque. Two complementary changes, both driven
    /// by the HUD Opacity config (0.25-1.0, default 1.0):
    ///  - the game's additive HUD materials are replaced with alpha-blended
    ///    ones, so lines keep their own color instead of adding to (and
    ///    saturating against) whatever sky/terrain is behind them;
    ///  - each element's alpha is raised to at least the configured opacity,
    ///    so the HUD reads as a solid overlay rather than a faint glow.
    /// Captures graphics again every second so HUD app panels the game builds
    /// after takeoff get the treatment too; config changes re-apply live.
    /// </summary>
    private void ApplyHudOpacity()
    {
        var opacity = Mathf.Clamp(ModConfiguration.Instance.HudOpacity.Value, 0f, 1f);
        if (opacity <= 0f)
        {
            return;
        }

        var configChanged = !Mathf.Approximately(opacity, _appliedOpacity);

        var newGraphics = false;
        foreach (var graphic in GetComponentsInChildren<Graphic>(true))
        {
            if (!_hudGraphics.Contains(graphic))
            {
                _hudGraphics.Add(graphic);
                newGraphics = true;
            }
        }

        if (!configChanged && !newGraphics)
        {
            return;
        }

        foreach (var graphic in _hudGraphics)
        {
            if (graphic == null)
            {
                continue;
            }

            if (configChanged || !_opaqueMaterials.ContainsKey(graphic))
            {
                ApplyOpacityToGraphic(graphic, opacity);
            }
        }

        _appliedOpacity = opacity;
        Debug.Log($"{nameof(NOVRFlightHudBehavior)}: Applied HUD opacity {opacity:F2} to {_hudGraphics.Count} graphics");
    }

    private void ApplyOpacityToGraphic(Graphic graphic, float opacity)
    {
        var color = graphic.color;
        color.a = Mathf.Max(color.a, opacity);
        graphic.color = color;

        // The tactical map keeps its own rendering: it depends on its original
        // materials, masks, and texture transparency. Swapping them for the
        // alpha-blended stand-ins makes the map overflow its clip region and
        // lose its look.
        if (IsPartOfDynamicMap(graphic))
        {
            return;
        }

        // The game renders the HUD with additive blending, so its textures are
        // authored for that: the background is plain black (black adds nothing)
        // and many textures carry no alpha channel at all. Alpha-blending such
        // a texture would show that background as an opaque rectangle, so
        // additive-authored textures keep their original material.
        if (IsAdditiveAuthoredTexture(graphic))
        {
            return;
        }

        var material = GetOpaqueMaterial(graphic);
        if (material == null)
        {
            return;
        }

        if (graphic is TextMeshProUGUI tmp)
        {
            tmp.fontSharedMaterial = material;
        }
        else
        {
            graphic.material = material;
        }
    }

    private static readonly Dictionary<Texture, bool> _additiveAuthoredTextures = new Dictionary<Texture, bool>();

    /// <summary>
    /// Returns true when the graphic's texture cannot be alpha-blended because
    /// it was authored for the game's additive HUD rendering: either it has no
    /// alpha channel at all (DXT1 etc. — the black background only disappears
    /// under additive blending, where black adds nothing), or it carries large
    /// opaque black regions despite having an alpha channel. Such textures must
    /// keep their original additive material or they render as opaque black
    /// rectangles behind the HUD elements.
    /// </summary>
    private static bool IsAdditiveAuthoredTexture(Graphic graphic)
    {
        var texture = GetGraphicTexture(graphic);
        if (texture == null)
        {
            return false;
        }

        if (_additiveAuthoredTextures.TryGetValue(texture, out var cached))
        {
            return cached;
        }

        var result = false;
        if (texture is Texture2D tex2d)
        {
            if (!TextureFormatHasAlpha(tex2d.format))
            {
                result = true;
            }
            else if (HasOpaqueBlackBackground(tex2d, GetTextureRegion(graphic)))
            {
                result = true;
            }

            if (result)
            {
                Debug.Log($"{nameof(NOVRFlightHudBehavior)}: HUD opacity keeps '{texture.name}' on its original additive material (additive-authored texture, no usable alpha).");
            }
        }

        _additiveAuthoredTextures[texture] = result;
        return result;
    }

    private static Rect? GetTextureRegion(Graphic graphic)
    {
        if (graphic is Image image && image.sprite != null)
        {
            return image.sprite.textureRect;
        }

        return null;
    }

    private static bool HasOpaqueBlackBackground(Texture2D tex2d, Rect? region)
    {
        try
        {
            var x0 = region.HasValue ? Mathf.FloorToInt(region.Value.x) : 0;
            var y0 = region.HasValue ? Mathf.FloorToInt(region.Value.y) : 0;
            var w = region.HasValue ? Mathf.FloorToInt(region.Value.width) : tex2d.width;
            var h = region.HasValue ? Mathf.FloorToInt(region.Value.height) : tex2d.height;

            // Sample a bounded grid so large textures stay cheap.
            var stride = Mathf.Max(1, Mathf.FloorToInt(Mathf.Sqrt(w * h / 8192f)));
            var opaqueBlack = 0;
            var total = 0;
            for (var y = y0; y < y0 + h; y += stride)
            {
                for (var x = x0; x < x0 + w; x += stride)
                {
                    var pixel = tex2d.GetPixel(x, y);
                    total++;
                    if (pixel.a >= 0.97f && pixel.r < 0.08f && pixel.g < 0.08f && pixel.b < 0.08f)
                    {
                        opaqueBlack++;
                    }
                }
            }

            return total > 0 && opaqueBlack / (float)total > 0.2f;
        }
        catch (Exception)
        {
            // Texture is not readable; fall back to treating it as alpha-safe.
            return false;
        }
    }

    private static bool TextureFormatHasAlpha(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.RGBA32:
            case TextureFormat.ARGB32:
            case TextureFormat.BGRA32:
            case TextureFormat.RGBA4444:
            case TextureFormat.RGBAFloat:
            case TextureFormat.RGBAHalf:
            case TextureFormat.R16:
            case TextureFormat.RG16:
            case TextureFormat.DXT5:
            case TextureFormat.BC7:
            case TextureFormat.ASTC_4x4:
            case TextureFormat.ASTC_5x5:
            case TextureFormat.ASTC_6x6:
            case TextureFormat.ASTC_8x8:
            case TextureFormat.ASTC_10x10:
            case TextureFormat.ASTC_12x12:
            case TextureFormat.PVRTC_RGBA2:
            case TextureFormat.PVRTC_RGBA4:
            case TextureFormat.ETC2_RGBA8:
            case TextureFormat.ETC2_RGBA1:
                return true;
            default:
                return false;
        }
    }

    private Material GetOpaqueMaterial(Graphic graphic)
    {
        if (_opaqueMaterials.TryGetValue(graphic, out var existing))
        {
            return existing;
        }

        Material material = null;
        if (graphic is TextMeshProUGUI tmp)
        {
            // The font asset's base material uses the regular (alpha-blended)
            // Distance Field shader; the game's HUD text uses the additive
            // variant of the same font material instead.
            var font = tmp.font;
            if (font != null)
            {
                material = new Material(font.material);
            }
        }
        else
        {
            // Elements inside a Mask/RectMask2D region need a stencil-capable
            // shader or their clip regions overflow; the plain alpha-blended
            // shader (Mobile/Particles/Alpha Blended) has no stencil support.
            var shader = IsInsideMaskedRegion(graphic) ? GetMaskedUiShader() : GetAlphaBlendedUiShader();
            if (shader != null)
            {
                material = new Material(shader);
                var texture = GetGraphicTexture(graphic);
                if (texture != null)
                {
                    material.SetTexture(MainTexId, texture);
                }
            }
        }

        if (material != null)
        {
            _opaqueMaterials[graphic] = material;
        }

        return material;
    }

    private static bool IsPartOfDynamicMap(Graphic graphic)
    {
        if (graphic.GetComponentInParent<global::DynamicMap>() != null)
        {
            return true;
        }

        // The map content may live under its own canvases (MapCanvas,
        // MaximizedMapCanvas) rather than under the DynamicMap component.
        var canvas = graphic.canvas;
        while (canvas != null)
        {
            if (canvas.name != null && canvas.name.Contains("MapCanvas"))
            {
                return true;
            }
            canvas = canvas.transform.parent != null
                ? canvas.transform.parent.GetComponentInParent<Canvas>()
                : null;
        }

        return false;
    }

    private static bool IsInsideMaskedRegion(Graphic graphic)
    {
        return graphic.GetComponentInParent<Mask>() != null ||
               graphic.GetComponentInParent<RectMask2D>() != null;
    }

    private static Texture GetGraphicTexture(Graphic graphic)
    {
        switch (graphic)
        {
            case RawImage rawImage:
                return rawImage.texture;
            case Image image:
                return image.sprite != null ? image.sprite.texture : null;
            case Text text:
                return text.font != null ? text.font.material.mainTexture : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The build strips UI/Default, so there is no stock alpha-blended UI
    /// shader to grab. Mobile/Particles/Alpha Blended (and TMP's Sprite
    /// shader as a fallback) are unlit alpha-blended shaders that ship with
    /// the game; at least one is normally loaded in the cockpit scene.
    /// Mobile/Particles/Alpha Blended has no stencil support, so masked
    /// regions use TextMeshPro/Sprite instead (it has the standard UI
    /// stencil/ClipRect properties).
    /// </summary>
    private static Shader GetAlphaBlendedUiShader()
    {
        return GetCachedUiShader(ref _alphaBlendedUiShader, "Mobile/Particles/Alpha Blended", "TextMeshPro/Sprite");
    }

    private static Shader GetMaskedUiShader()
    {
        return GetCachedUiShader(ref _maskedUiShader, "TextMeshPro/Sprite", "Mobile/Particles/Alpha Blended");
    }

    private static Shader GetCachedUiShader(ref Shader cache, params string[] preferred)
    {
        if (cache != null)
        {
            return cache;
        }

        for (var i = 0; i < preferred.Length; i++)
        {
            cache = Shader.Find(preferred[i]);
            if (cache != null)
            {
                return cache;
            }
        }

        foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
        {
            for (var i = 0; i < preferred.Length; i++)
            {
                if (shader.name == preferred[i])
                {
                    cache = shader;
                    return cache;
                }
            }
        }

        return null;
    }

    private void CaptureLineElements()
    {
        // Only anchor-pinned elements are resized (stretched elements use
        // sizeDelta as an offset, so touching them would break layout).
        foreach (var image in GetComponentsInChildren<Image>(true))
        {
            var rectTransform = image.rectTransform;
            if (rectTransform.anchorMin != rectTransform.anchorMax)
            {
                continue;
            }

            var size = rectTransform.sizeDelta;
            var width = Mathf.Abs(size.x);
            var height = Mathf.Abs(size.y);
            var thin = Mathf.Min(width, height);
            var thick = Mathf.Max(width, height);

            if (thin < 0.5f || thick / thin < 3f || thin > 12f)
            {
                continue;
            }

            _originalLineSizes[image] = size;
        }
    }
    
    private void MoveHmdPanelToHud(string panelName, Transform noVrHudParent, Vector3 localPosition, Vector3 localScale)
    {
        if (noVrHudParent == null)
            return;
        
        var panel = FindChildStartingWith(transform, panelName);
        if (panel == null)
            return;
        
        panel.SetParent(noVrHudParent, false);
        panel.localPosition = localPosition;
        panel.localEulerAngles = Vector3.zero;
        panel.localScale = localScale;
        MakePanelInvisible(panel);
        
        if (panelName == "TopRightPanel")
        {
            PositionTopRightPanelChildren(panel);
        }
    }
    
    private static void MakePanelInvisible(Transform panel)
    {
        var image = panel.GetComponent<Image>();
        if (image != null)
            image.enabled = false;
    }
    
    private static void MakeChildImageInvisible(Transform parent, string childName)
    {
        var child = FindChildStartingWith(parent, childName);
        if (child == null)
            return;
        
        var image = child.GetComponent<Image>();
        if (image != null)
            image.enabled = false;
    }
    
    
    private static void PositionTopRightPanelChildren(Transform topRightPanel)
    {
        SetChildLocalPosition(topRightPanel, "countermeasuresBackground", new Vector3(-750f, -55f, 0f));
        SetChildLocalPosition(topRightPanel, "weaponPanel", new Vector3(-100f, -55f, 0f));
        SetChildLocalPosition(topRightPanel, "PowerPanel", new Vector3(-350f, -80f, 0f));
        var powerPanel = FindChildStartingWith(topRightPanel, "PowerPanel");
        if (powerPanel == null)
            return;
        
        MakePanelInvisible(powerPanel);
        MakeChildImageInvisible(powerPanel, "chargeBarBackground");
    }
    
    private static void SetChildLocalPosition(Transform parent, string childName, Vector3 localPosition)
    {
        var child = FindChildStartingWith(parent, childName);
        if (child == null)
            return;
        
        child.localPosition = localPosition;
    }
}
