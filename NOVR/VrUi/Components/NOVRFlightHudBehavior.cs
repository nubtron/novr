using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRFlightHudBehavior : UIRenderedCanvasBehavior
{
    private readonly Dictionary<Image, Vector2> _originalLineSizes = new Dictionary<Image, Vector2>();
    private float _appliedLineThickness = -1f;
    private int _frameCount;

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
        if (_frameCount > 30)
        {
            ApplyHudLineThickness();
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
