using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRFlightHudBehavior : UIRenderedCanvasBehavior
{
    private readonly Dictionary<Image, Vector2> _originalLineSizes = new Dictionary<Image, Vector2>();
    private float _appliedLineThickness = -1f;
    private int _frameCount;

    // HUD opacity state.
    //
    // The game renders the HUD additively: the framebuffer gets the texture RGB
    // *added* to the scene, so black background pixels contribute nothing and
    // coloured pixels contribute their colour. That is invisible over dark
    // terrain and useless over a bright sky — dst + src saturates to white and
    // the line loses its hue entirely. Measured from a RenderDoc capture: the
    // HUD is drawn after tonemapping, so nothing upstream is washing it out.
    // The blend equation itself is the whole problem.
    //
    // So every HUD graphic is moved onto an alpha-blended material. Where the
    // texture has no alpha channel to blend with, one is reconstructed from the
    // image itself (see RebuildAlphaFromLuminance) rather than giving up.
    private readonly List<Graphic> _hudGraphics = new List<Graphic>();
    private readonly Dictionary<Graphic, Material> _opaqueMaterials = new Dictionary<Graphic, Material>();
    private float _appliedOpacity = -1f;
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    //: Reconstructed textures, keyed by the original. Built once and shared by
    //: every graphic using that texture.
    private static readonly Dictionary<Texture, Texture2D> _rebuiltTextures =
        new Dictionary<Texture, Texture2D>();
    //: Black point for the alpha rebuild, as a fraction of full brightness.
    //: Deliberately tiny — just enough to drop compression noise. An earlier
    //: version used a much higher floor with a knee, on the theory that dim
    //: pixels were background. They are not: the HUD icons are line art, and
    //: those dim pixels are the antialiased shoulders of one-pixel lines.
    //: Cutting them broke weaponIcon_gatlingGun into disconnected blobs.
    private const float CoverageFloor = 0.015f;
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

        var tally = new Dictionary<string, int>();
        foreach (var graphic in _hudGraphics)
        {
            if (graphic == null)
            {
                continue;
            }

            if (configChanged || !_opaqueMaterials.ContainsKey(graphic))
            {
                var outcome = ApplyOpacityToGraphic(graphic, opacity);
                tally.TryGetValue(outcome, out var count);
                tally[outcome] = count + 1;
            }
        }

        _appliedOpacity = opacity;
        var breakdown = string.Join(", ", tally.Select(pair => $"{pair.Value} {pair.Key}"));
        // Report what happened to every graphic, not just how many were seen.
        // The previous version logged only a total, so an element that silently
        // kept its additive material was indistinguishable from one that was
        // converted — and two of them were, which took a GPU capture to notice.
        Debug.Log($"{nameof(NOVRFlightHudBehavior)}: Applied HUD opacity {opacity:F2} to " +
                  $"{_hudGraphics.Count} graphics ({breakdown})");
    }

    /// <summary>
    /// Move one graphic onto an alpha-blended material. Returns a short tag
    /// naming what actually happened, for the summary line — every outcome,
    /// including every refusal, has to be countable.
    /// </summary>
    private string ApplyOpacityToGraphic(Graphic graphic, float opacity)
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
            return "skipped:map";
        }

        var material = GetOpaqueMaterial(graphic);
        if (material == null)
        {
            return graphic is TextMeshProUGUI ? "skipped:no-font" : "skipped:no-shader";
        }

        if (graphic is TextMeshProUGUI tmp)
        {
            tmp.fontSharedMaterial = material;
            return "tmp-text";
        }

        // The textures are authored for additive blending: the background is
        // plain black (black adds nothing) and many carry no alpha channel at
        // all. Alpha-blending such a texture as-is would show that background
        // as an opaque rectangle — so rebuild the alpha from the image instead
        // of leaving the element additive, which is the only way an element
        // whose texture has no alpha can ever be made readable over bright sky.
        var outcome = "alpha-blended";
        if (IsAdditiveAuthoredTexture(graphic))
        {
            var rebuilt = RebuildAlphaFromLuminance(GetGraphicTexture(graphic));
            if (rebuilt == null || !TryUseRebuiltTexture(graphic, rebuilt))
            {
                // Reconstruction failed, or this Graphic type has no texture we
                // can swap. Additive is the safe fallback: washed out, but never
                // an opaque black box.
                return "kept-additive:rebuild-failed";
            }
            outcome = "alpha-rebuilt";
        }

        graphic.material = material;
        return outcome;
    }

    private static readonly Dictionary<Sprite, Sprite> _rebuiltSprites = new Dictionary<Sprite, Sprite>();

    /// <summary>
    /// Point the graphic itself at the rebuilt texture.
    ///
    /// Setting _MainTex on the material does nothing here: Unity UI binds the
    /// texture from Graphic.mainTexture at draw time and overwrites whatever the
    /// material had. Doing it that way looked like it worked — the rebuilt
    /// texture was created, uploaded, and visible in a GPU capture — while every
    /// draw still sampled the original. With an alpha-blended material and a
    /// source texture whose alpha is 255 everywhere, that renders the whole
    /// quad opaque: black background included. The capture is what caught it;
    /// the draw's bound texture was the original, not the copy.
    /// </summary>
    private static bool TryUseRebuiltTexture(Graphic graphic, Texture2D rebuilt)
    {
        switch (graphic)
        {
            case RawImage rawImage:
                rawImage.texture = rebuilt;
                return true;

            case Image image when image.sprite != null:
                var original = image.sprite;
                if (!_rebuiltSprites.TryGetValue(original, out var sprite))
                {
                    // Same rect, pivot, border and pixels-per-unit as the
                    // original, so nothing about layout or 9-slicing changes —
                    // only the pixels behind it.
                    var rect = original.rect;
                    sprite = Sprite.Create(
                        rebuilt,
                        rect,
                        new Vector2(original.pivot.x / rect.width, original.pivot.y / rect.height),
                        original.pixelsPerUnit,
                        0,
                        SpriteMeshType.FullRect,
                        original.border);
                    sprite.name = original.name + " (alpha rebuilt)";
                    _rebuiltSprites[original] = sprite;
                }

                image.sprite = sprite;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Build an alpha-blendable copy of an additive-authored texture.
    ///
    /// Under additive blending a pixel's brightness *is* its coverage: black
    /// contributes nothing, a bright pixel contributes its colour. So
    /// alpha = max(r,g,b) and rgb = rgb / alpha re-expresses exactly the same
    /// image as colour-times-coverage, which alpha blending can draw. A DXT1
    /// texture with no alpha channel at all comes out as a clean mask.
    ///
    /// Read back through a RenderTexture rather than Texture2D.GetPixels: HUD
    /// textures are compressed and usually not marked readable, and a blit is
    /// the only path that works regardless.
    /// </summary>
    private static Texture2D RebuildAlphaFromLuminance(Texture source)
    {
        if (source == null)
        {
            return null;
        }

        if (_rebuiltTextures.TryGetValue(source, out var cached))
        {
            return cached;
        }

        Texture2D result = null;
        RenderTexture temporary = null;
        var previous = RenderTexture.active;
        var sourceFilter = source.filterMode;
        try
        {
            temporary = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            // Point sampling for the readback. Blit's default bilinear filter
            // samples on a half-texel offset, which bleeds the bright lines out
            // into the black background: measured on throttleArc, a texture
            // that is 94.9% pure black came back with 60.9% of its pixels
            // non-zero. Every one of those became a low-alpha tint, and the
            // element rendered as a dark rectangle instead of a line.
            source.filterMode = FilterMode.Point;
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;

            // Keep a mip chain. The HUD art is line drawings at 512x256 shown
            // at a fraction of that size, and the original textures are mipped;
            // a copy without mips is point-sampled on minification, which broke
            // weaponIcon_gatlingGun into disconnected dots. Large elements were
            // unaffected, which is what makes this look like a content bug
            // rather than a sampling one.
            result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true)
            {
                name = source.name + " (alpha rebuilt)",
                wrapMode = source.wrapMode,
                filterMode = source.filterMode,
            };
            result.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);

            var pixels = result.GetPixels32();
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var coverage = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b));

                // Alpha is the brightness, straight through. That is not a
                // simplification, it is the identity that makes this work: an
                // additive draw contributes src, and an alpha draw contributes
                // src*a, so taking a = brightness and normalising the colour to
                // full intensity reproduces exactly the same contribution while
                // also occluding what is behind it. Rescaling alpha on any curve
                // breaks that equivalence and shows up as line art thinning out.
                var alpha = coverage / 255f;
                if (alpha <= CoverageFloor)
                {
                    pixels[i] = new Color32(0, 0, 0, 0);
                    continue;
                }

                // Normalise the colour to full intensity and carry the intensity
                // in alpha instead, so a dim line becomes a fully-coloured line
                // drawn at partial coverage rather than a washed-out one.
                var scale = 255f / coverage;
                pixels[i] = new Color32(
                    (byte)Mathf.Min(255f, pixel.r * scale),
                    (byte)Mathf.Min(255f, pixel.g * scale),
                    (byte)Mathf.Min(255f, pixel.b * scale),
                    (byte)Mathf.Min(255f, alpha * 255f));
            }

            result.SetPixels32(pixels);
            result.Apply(true, false);   // regenerate the mip chain from the rebuilt pixels
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"{nameof(NOVRFlightHudBehavior)}: could not rebuild alpha for " +
                             $"'{source.name}' ({exception.GetType().Name}: {exception.Message}); " +
                             "leaving it on the additive material.");
            if (result != null)
            {
                Destroy(result);
            }
            result = null;
        }
        finally
        {
            // The source is the game's own texture asset — the point filtering
            // above is ours and must not outlive the readback.
            source.filterMode = sourceFilter;
            RenderTexture.active = previous;
            if (temporary != null)
            {
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        _rebuiltTextures[source] = result;
        return result;
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
            case TextureFormat.ARGB4444:
            case TextureFormat.RGBAFloat:
            case TextureFormat.RGBAHalf:
            case TextureFormat.RGBA64:
            // Alpha-only. Nothing *but* alpha, in fact — this is what Unity's
            // dynamic font atlas ("Font Texture") and the TMP SDF atlases use.
            // Omitting it classified every legacy UI Text in the HUD as having
            // no usable alpha, which is the exact opposite of the truth, and
            // left them additive. Confirmed from a capture: Font Texture
            // 256x256 A8_UNORM was rendering with Add(SrcAlpha, One).
            case TextureFormat.Alpha8:
            case TextureFormat.DXT5:
            case TextureFormat.DXT5Crunched:
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
                // ...except when it does not. Some font assets in this build
                // (LiberationSans SDF TMPro Atlas) have an *additive* base
                // material, so copying it kept those draws on Add(One, One) —
                // visible in a capture, invisible in the log. Force the
                // non-additive variant instead of trusting the asset.
                var distanceField = GetDistanceFieldShader();
                if (distanceField != null && material.shader != distanceField)
                {
                    material.shader = distanceField;
                }
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

    private static Shader _distanceFieldShader;

    /// <summary>The alpha-blended TextMeshPro shader, as opposed to the
    /// additive variant some of this build's font assets ship with.</summary>
    private static Shader GetDistanceFieldShader()
    {
        return GetCachedUiShader(ref _distanceFieldShader, "TextMeshPro/Distance Field");
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
