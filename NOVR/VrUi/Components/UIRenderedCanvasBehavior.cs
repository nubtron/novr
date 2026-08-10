using System;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class UIRenderedCanvasBehavior : MonoBehaviour
{
    private bool _initialized;
    
    protected virtual bool ShouldInitializeCanvas => true;

    public virtual void Awake() => Initialize();

    public virtual void OnEnable() => Initialize();

    private void Initialize()
    {
        if (!ShouldInitializeCanvas) return;
        if (_initialized) return;
        _initialized = true;
        
        
        ApplyVrUiLayerRecursive(transform);
        transform.localPosition = transform.localPosition with { z = 0 };
        var canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null) return;
        
        
        canvas.renderMode = RenderMode.WorldSpace;
        Debug.Log($"{GetType().Name}: Set canvas render mode of {canvas.gameObject.name}. Is currently:  {canvas.renderMode}");
        canvas.worldCamera = APIBus.CockpitHudCamera;
        Debug.Log($"{GetType().Name}: Set canvas world camera of {canvas.gameObject.name}. Is currently:  {canvas.worldCamera}");
        canvas.planeDistance = 1f;

        // Materials are resolved once and cached on the graphic, so anything
        // already drawn under the old render mode never runs back through the
        // material modifiers. World space is exactly where masked content needs
        // MaskedUiQueuePatch to correct its queue, so ask for that pass now.
        foreach (var graphic in GetComponentsInChildren<MaskableGraphic>(true))
        {
            graphic.SetMaterialDirty();
        }
    }
    
    
    private static void ApplyVrUiLayerRecursive(Transform root)
    {
        LayerHelper.SetLayerRecursive(root, LayerHelper.GetVrUiLayer());
    }


    protected static Transform FindChildStartingWith(Transform parent, string childNamePrefix)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name.StartsWith(childNamePrefix))
            {
                return child;
            }

            var nestedChild = FindChildStartingWith(child, childNamePrefix);
            if (nestedChild != null)
            {
                return nestedChild;
            }
        }

        return null;
    }
}
