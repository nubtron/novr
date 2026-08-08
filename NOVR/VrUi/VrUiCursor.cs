using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using UnityEngine.XR;

namespace NOVR.VrUi;

[DefaultExecutionOrder(-1000)]
public class VrUiCursor: NOVRBehaviour
{
    public static VrUiCursor? Instance { get; private set; }
    public static VrUiCursor? I => Instance;

    public bool IsActive => _cursor != null && _cursor.activeSelf;
    public Vector3 CursorPosition => _cursor != null ? _cursor.transform.position : Vector3.zero;

    protected override void Awake()
    {
        base.Awake();
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (_virtualMouse != null)
        {
            try
            {
                InputSystem.RemoveDevice(_virtualMouse);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[{nameof(VrUiCursor)}] Failed to remove VirtualMouse during OnDestroy: {ex}");
            }
            _virtualMouse = null;
        }
    }

    private Texture2D? _texture;
    private const float MaxYawDegrees = 65f;
    private const float MaxPitchDegrees = 45f;
    private const float DefaultProjectionDistance = 5;
    private const float CursorMinDistanceMeters = 1.0f;
    private const float CursorCanvasScale = 0.001f;
    private const int CursorTextureSize = 64;
    private const float CursorIdlePulseScale = 0.035f;
    private const float CursorIdlePulseSpeed = 5.5f;
    private const float CursorHoverScale = 1.18f;
    private const float CursorPressedScale = 0.84f;
    private const float CursorClickPulseScale = 0.22f;
    private const float CursorClickPulseDuration = 0.18f;
    private const float CursorAnimationLerpSpeed = 24f;
    private GameObject? _cursor;
    private RectTransform? _cursorRectTransform;
    private Canvas? _cursorCanvas;
    private RawImage? _cursorImage;
    private bool _cursorOverInteractive;
    private float _lastCursorClickTime = -100f;
    private bool _hasProjectionReferenceOverride;
    private Quaternion _projectionReferenceRotation = Quaternion.identity;

    private bool _hasInitializedEventSystem = false;
    private Mouse? _virtualMouse;
    private Mouse? _realMouse;
    private bool _loggedMissingRealMouse;

    private InputDevice _controllerDevice;
    private bool _controllerDeviceValid;
    private bool _controllerModeActive;
    private Vector3 _controllerAimDirection = Vector3.forward;
    private bool _controllerTriggerPressed;
    private bool _controllerTriggerClicked;
    private float _controllerSmoothing = 0.3f;
    
    
    private int ScreenWidth => Screen.width;
    private int ScreenHeight => Screen.height;
    public Camera? UiCamera
    {
        get
        {
            return APIBus.CockpitHudCamera;
        }
    }
    
    
    public Vector2 GetScreenPoint()
    {
        var camera = UiCamera;
        if (_cursor != null && camera != null)
        {
            Vector3 viewportPoint = camera.WorldToViewportPoint(_cursor.transform.position, Camera.MonoOrStereoscopicEye.Mono);
            float screenX = Mathf.Clamp(viewportPoint.x * Screen.width, 0f, Screen.width);
            float screenY = Mathf.Clamp(viewportPoint.y * Screen.height, 0f, Screen.height);
            return new Vector2(screenX, screenY);
        }
        return Vector2.zero;
    }

    public void SetProjectionReferenceRotation(Quaternion referenceRotation)
    {
        _projectionReferenceRotation = referenceRotation;
        _hasProjectionReferenceOverride = true;
    }

    public void ClearProjectionReferenceRotation()
    {
        _hasProjectionReferenceOverride = false;
    }
    
    
    private void Start()
    {
        _texture = CreateCursorTexture();
    }

    private void Update()
    {
        if (!Application.isFocused)
        {
            if (_cursor != null && _cursor.activeSelf)
            {
                _cursor.SetActive(false);
            }
            return;
        }

        if (!IsRealCursorVisible()) // This means we don't have to manually show and hide it every game update
        {
            if (_cursor != null)
            {
                _cursor.SetActive(false);
            }
            return;
        }
        
        if (_virtualMouse == null)
        {
            _virtualMouse = InputSystem.AddDevice<Mouse>("VirtualMouse");
            Debug.Log($"[NOVR] Added VirtualMouse device: name='{_virtualMouse.name}', path='{_virtualMouse.path}', displayName='{_virtualMouse.displayName}'");
        }

        if (!EnsureRealMouse()) return;

        if (!_hasInitializedEventSystem)
        {
            if (RestrictUIModuleToVirtualMouse())
            {
                _hasInitializedEventSystem = true;
            }
        }
        if (_texture == null) return;
        UpdateControllerInput();
        UpdateCursorAngles();
        
        var realMouse = _realMouse;
        if (realMouse == null || _virtualMouse == null) return;

        var leftPressed = realMouse.leftButton.isPressed || _controllerTriggerPressed;
        var leftClicked = realMouse.leftButton.wasPressedThisFrame || _controllerTriggerClicked;
        UpdateCursorAnimation(leftPressed, leftClicked);

        var screenPoint = GetScreenPoint();

        ushort buttons = 0;
        if (leftPressed) buttons |= 1;
        if (realMouse.rightButton.isPressed) buttons |= 2;
        if (realMouse.middleButton.isPressed) buttons |= 4;

        InputState.Change(_virtualMouse, new MouseState
        {
            position = screenPoint,
            delta = realMouse.delta.ReadValue(),
            scroll = realMouse.scroll.ReadValue(),
            buttons = buttons
        });

        if (realMouse.leftButton.wasPressedThisFrame)
        {
            LogRaycastAtCursor();
        }
    }
    

    /// <summary>
    /// Point <see cref="_realMouse"/> at a live hardware mouse, re-acquiring it
    /// when the one we were holding has been removed.
    ///
    /// <para><b>Why it is not cached for the session.</b> A removed
    /// <c>InputDevice</c> keeps its managed object but loses its state block,
    /// and <i>every</i> control read on it throws
    /// (<c>InputControl.GetDeviceIndex</c>: "Cannot query value of control ...
    /// before ... has been added to system"). The backend re-enumerates devices
    /// mid-session, not only at startup — a single run logged seven
    /// <c>OnNativeDeviceDiscovered</c> passes in fifty seconds — so a reference
    /// taken once and held was one re-enumeration away from throwing on every
    /// frame for the rest of the run. It threw *below* the cursor's own posing,
    /// which is what made it hard to read: the cursor still tracked the head,
    /// and nothing that clicks was ever reached again.</para>
    ///
    /// <para>Never selects our own VirtualMouse. <see cref="Mouse.current"/> is
    /// whichever mouse last changed state, and this component writes to the
    /// virtual one every frame, so <c>current</c> is almost always the wrong
    /// answer here.</para>
    /// </summary>
    private bool EnsureRealMouse()
    {
        if (_realMouse != null && _realMouse.added) return true;

        var reacquiring = _realMouse != null;
        _realMouse = null;

        foreach (var device in InputSystem.devices)
        {
            if (device is not Mouse mouse) continue;
            if (!mouse.added || ReferenceEquals(mouse, _virtualMouse)) continue;
            _realMouse = mouse;
            break;
        }

        if (_realMouse == null)
        {
            if (reacquiring || !_loggedMissingRealMouse)
            {
                _loggedMissingRealMouse = true;
                Debug.LogWarning($"[{nameof(VrUiCursor)}] No hardware Mouse device present; " +
                                 "the VR cursor cannot forward buttons until one appears.");
            }
            return false;
        }

        _loggedMissingRealMouse = false;
        Debug.Log($"[{nameof(VrUiCursor)}] {(reacquiring ? "Re-acquired" : "Acquired")} hardware mouse " +
                  $"'{_realMouse.name}' (path '{_realMouse.path}').");
        return true;
    }

    private void UpdateCursorAngles()
    {
        var camera = UiCamera;
        if (camera == null) return;

        EnsureCursorCanvas(camera);

        if (_cursor == null || _cursorRectTransform == null)
        {
            return;
        }

        if (!_cursor.activeSelf)
        {
            _cursor.SetActive(true);
        }

        Vector3 worldDirection;
        if (_controllerModeActive)
        {
            worldDirection = _controllerAimDirection;
        }
        else
        {
            var mouse = _realMouse;
            if (mouse == null) return;

            var mousePos = mouse.position.ReadValue();
            float cursorPitch = ProjectPitchAngle(mousePos.y);
            float cursorYaw = ProjectYawAngle(mousePos.x);

            Vector3 localDirection = Quaternion.Euler(-cursorPitch, cursorYaw, 0f) * Vector3.forward;
            Quaternion referenceRotation = GetProjectionReferenceRotation();
            worldDirection = referenceRotation * localDirection;
        }

        Vector3 viewportSpace = camera.WorldToViewportPoint(camera.transform.position + worldDirection * DefaultProjectionDistance, Camera.MonoOrStereoscopicEye.Mono);
        Vector2 inScreenSpace = new Vector2(viewportSpace.x * Screen.width, viewportSpace.y * Screen.height);
        float cursorDistance = GetDistanceUnderCursor(inScreenSpace);
        Vector3 pos = camera.transform.position + worldDirection * cursorDistance;
        _cursor.transform.position = pos;
        _cursor.transform.rotation = Quaternion.LookRotation(worldDirection, camera.transform.up);
    }

    /// <summary>
    /// Drives the cursor from an XR motion controller ray when the configured
    /// input source is a hand: the aim direction is the controller's forward,
    /// intersected with the plane facing the camera at the default projection
    /// distance so the cursor lands where the controller points. Falls back to
    /// the mouse when the controller is not tracked.
    /// </summary>
    private void UpdateControllerInput()
    {
        var source = ModConfiguration.Instance.CursorInputSource.Value;
        _controllerModeActive = false;

        XRNode node;
        switch (source)
        {
            case "Right Hand":
                node = XRNode.RightHand;
                break;
            case "Left Hand":
                node = XRNode.LeftHand;
                break;
            default:
                return;
        }

        _controllerSmoothing = Mathf.Clamp(ModConfiguration.Instance.CursorControllerSmoothing.Value, 0.05f, 0.95f);

        if (_controllerDeviceValid && _controllerDevice.node != node)
        {
            _controllerDeviceValid = false;
        }

        if (!_controllerDeviceValid)
        {
            _controllerDevice = InputDevices.GetDeviceAtXRNode(node);
            _controllerDeviceValid = _controllerDevice.isValid;
            if (!_controllerDeviceValid)
            {
                return;
            }
        }

        if (!_controllerDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var controllerRotation))
        {
            return;
        }

        var camera = UiCamera;
        var rayOrigin = camera != null ? camera.transform.position : Vector3.zero;
        if (_controllerDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var controllerPosition) &&
            controllerPosition.sqrMagnitude > 0.0001f)
        {
            rayOrigin = controllerPosition;
        }

        var rawDirection = controllerRotation * Vector3.forward;
        var aimDirection = rawDirection;

        if (camera != null)
        {
            var planePoint = camera.transform.position + camera.transform.forward * DefaultProjectionDistance;
            var planeNormal = camera.transform.forward;
            var denominator = Vector3.Dot(rawDirection, planeNormal);
            if (Mathf.Abs(denominator) > 0.05f)
            {
                var t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denominator;
                if (t > 0f)
                {
                    var hitPoint = rayOrigin + rawDirection * t;
                    aimDirection = (hitPoint - camera.transform.position).normalized;
                }
            }
        }

        _controllerAimDirection = Vector3.Slerp(_controllerAimDirection, aimDirection, _controllerSmoothing);
        _controllerModeActive = true;

        var triggerPressed = _controllerDevice.TryGetFeatureValue(CommonUsages.trigger, out var trigger) && trigger > 0.5f;
        _controllerTriggerClicked = triggerPressed && !_controllerTriggerPressed;
        _controllerTriggerPressed = triggerPressed;
    }

    private Quaternion GetProjectionReferenceRotation()
    {
        if (_hasProjectionReferenceOverride)
        {
            return _projectionReferenceRotation;
        }

        return transform.parent != null ? transform.parent.rotation : Quaternion.identity;
    }
    
    private void EnsureCursorCanvas(Camera uiCaptureCamera)
    {
        if (_cursor != null)
        {
            if (_cursorImage != null)
            {
                _cursorImage.texture = _texture;
            }
            return;
        }

        _cursor = new GameObject("VrUiCursorCanvas");
        _cursor.transform.localScale = Vector3.one * CursorCanvasScale;
        _cursorCanvas = _cursor.AddComponent<Canvas>();
        _cursorCanvas.renderMode = RenderMode.WorldSpace;
        _cursorCanvas.planeDistance = Mathf.Max(uiCaptureCamera.nearClipPlane + 0.01f, 0.11f);
        _cursorCanvas.overrideSorting = true;
        _cursorCanvas.sortingOrder = short.MaxValue;
        _cursorCanvas.pixelPerfect = true;

        _cursorRectTransform = _cursor.GetComponent<RectTransform>();
        var sizeMultiplier = Mathf.Clamp(ModConfiguration.Instance.CursorSizeMultiplier.Value, 1.0f, 3.0f);
        _cursorRectTransform.sizeDelta = Vector2.one * (CursorTextureSize * sizeMultiplier);
        _cursorImage = _cursor.AddComponent<RawImage>();
        _cursorImage.raycastTarget = false;
        _cursorImage.texture = _texture;
        _cursorImage.color = Color.white;
        LayerHelper.SetLayerRecursive(_cursor.transform, LayerHelper.GetVrUiLayer());
        
        
        
        
    }


    private float GetDistanceUnderCursor(Vector2 screenPos)
    {
        _cursorOverInteractive = false;
        float distance;
        if (TryGetUiDistanceUnderCursor(screenPos, out var uiDistance, out var overInteractive))
        {
            _cursorOverInteractive = overInteractive;
            distance = uiDistance;
        }
        else
        {
            distance = DefaultProjectionDistance;
        }

        // Keep the cursor at least CursorMinDistanceMeters away from the
        // camera: a UI element that nearly touches the lens would otherwise
        // make the cursor gigantic on screen.
        return Mathf.Max(distance, CursorMinDistanceMeters);
    }

    private bool TryGetUiDistanceUnderCursor(Vector2 screenPos, out float distance, out bool overInteractive)
    {
        distance = default;
        overInteractive = false;

        if (EventSystem.current == null)
        {
            return false;
        }

        var pointerEventData = new PointerEventData(EventSystem.current)
        {
            position = screenPos
        };

        var results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerEventData, results);

        var camera = UiCamera;
        Vector3 cameraPos = camera != null ? camera.transform.position : Vector3.zero;

        foreach (var result in results)
        {
            if (result.gameObject == _cursor || 
                result.distance < 0f ||
                result.gameObject.GetComponentInParent<global::MapIcon>() != null)
            {
                continue;
            }

            overInteractive = IsInteractiveRaycastTarget(result.gameObject);
            distance = result.worldPosition == Vector3.zero
                ? result.distance
                : Vector3.Distance(cameraPos, result.worldPosition);

            return distance > 0f;
        }

        return false;
    }

    private static bool IsInteractiveRaycastTarget(GameObject gameObject)
    {
        var selectable = gameObject.GetComponentInParent<Selectable>();
        if (selectable != null)
        {
            return selectable.IsInteractable();
        }

        return ExecuteEvents.GetEventHandler<IPointerClickHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<IPointerDownHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<ISubmitHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<IDragHandler>(gameObject) != null;
    }

    private void UpdateCursorAnimation(bool isPressed, bool wasClicked)
    {
        if (_cursor == null || _cursorImage == null) return;

        if (wasClicked)
        {
            _lastCursorClickTime = Time.unscaledTime;
        }
        var idlePulse = Mathf.Sin(Time.unscaledTime * CursorIdlePulseSpeed) * CursorIdlePulseScale;
        var clickProgress = Mathf.Clamp01((Time.unscaledTime - _lastCursorClickTime) / CursorClickPulseDuration);
        var clickPulse = clickProgress < 1f
            ? Mathf.Sin((1f - clickProgress) * Mathf.PI) * CursorClickPulseScale
            : 0f;

        var targetVisualScale = 1f + idlePulse + clickPulse;
        if (_cursorOverInteractive)
        {
            targetVisualScale *= CursorHoverScale;
        }
        if (isPressed)
        {
            targetVisualScale *= CursorPressedScale;
        }

        var targetScale = Vector3.one * (CursorCanvasScale * targetVisualScale);
        _cursor.transform.localScale = Vector3.Lerp(_cursor.transform.localScale, targetScale, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);

        _cursorImage.color = Color.white;
    }


    private float ProjectPitchAngle(float y)
    {
        int height = ScreenHeight;
        if (height <= 0) throw new System.InvalidOperationException($"[{nameof(VrUiCursor)}] Screen height is invalid ({height}).");
        return Mathf.Lerp(-MaxPitchDegrees, MaxPitchDegrees, y / height);
    }
    private float ProjectYawAngle(float x)
    {
        int width = ScreenWidth;
        if (width <= 0) throw new System.InvalidOperationException($"[{nameof(VrUiCursor)}] Screen width is invalid ({width}).");
        return Mathf.Lerp(-MaxYawDegrees, MaxYawDegrees, x / width);
    }
    private static bool IsRealCursorVisible() => Cursor.visible && Cursor.lockState != CursorLockMode.Locked;

    private static Texture2D CreateCursorTexture()
    {
        var texture = new Texture2D(CursorTextureSize, CursorTextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var colors = new Color32[CursorTextureSize * CursorTextureSize];
        var center = new Vector2((CursorTextureSize - 1) * 0.5f, (CursorTextureSize - 1) * 0.5f);
        var transparent = new Color32(0, 0, 0, 0);
        var outline = new Color32(0, 0, 0, 255);
        var highlight = new Color32(0, 255, 255, 255);

        for (var y = 0; y < CursorTextureSize; y++)
        {
            for (var x = 0; x < CursorTextureSize; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), center);
                var color = transparent;

                if (distance >= 10.5f && distance <= 19.5f)
                {
                    color = outline;
                }
                if (distance >= 13.0f && distance <= 17.0f)
                {
                    color = highlight;
                }
                if (distance <= 4.0f)
                {
                    color = outline;
                }
                if (distance <= 2.0f)
                {
                    color = highlight;
                }

                colors[y * CursorTextureSize + x] = color;
            }
        }

        texture.SetPixels32(colors);
        texture.Apply();
        return texture;
    }
    
    private void LogRaycastAtCursor()
    {
        if (EventSystem.current == null) return;
        
        var screenPos = GetScreenPoint();
        var pointerEventData = new PointerEventData(EventSystem.current)
        {
            position = screenPos
        };

        var results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerEventData, results);
        
        Debug.Log($"[VrUiCursor] Click Raycast at screenPos={screenPos}: found {results.Count} results");
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.gameObject == _cursor) continue;
            
            var canvas = result.gameObject.GetComponentInParent<Canvas>();
            var cg = result.gameObject.GetComponentInParent<CanvasGroup>();
            string cgInfo = cg != null ? $", CanvasGroup(alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts})" : "";
            string rectInfo = "";
            var rt = result.gameObject.GetComponent<RectTransform>();
            if (rt != null)
            {
                rectInfo = $", localPos={rt.localPosition}, size={rt.sizeDelta}";
            }
            Debug.Log($"[VrUiCursor]   Hit[{i}]: name='{result.gameObject.name}', path='{GetGameObjectPath(result.gameObject)}', canvas='{(canvas != null ? canvas.name : "None")}'{rectInfo}{cgInfo}");
        }
    }
    
    private static string GetGameObjectPath(GameObject go)
    {
        string path = go.name;
        Transform p = go.transform.parent;
        while (p != null)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }

    private bool RestrictUIModuleToVirtualMouse()
    {
        try
        {
            if (_virtualMouse == null) return false;
            var uiModule = FindObjectOfType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            if (uiModule != null)
            {
                Debug.Log($"[NOVR] Restricting InputSystemUIInputModule actions to VirtualMouse (path: {_virtualMouse.path})");
                RestrictActionToVirtualMouse(uiModule.point?.action, _virtualMouse.path);
                RestrictActionToVirtualMouse(uiModule.leftClick?.action, _virtualMouse.path);
                RestrictActionToVirtualMouse(uiModule.middleClick?.action, _virtualMouse.path);
                RestrictActionToVirtualMouse(uiModule.rightClick?.action, _virtualMouse.path);
                RestrictActionToVirtualMouse(uiModule.scrollWheel?.action, _virtualMouse.path);
                return true;
            }
            else
            {
                Debug.LogWarning("[NOVR] InputSystemUIInputModule not found in scene yet, retrying next frame...");
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[NOVR] Exception while restricting UI actions to VirtualMouse: {ex}");
            return false;
        }
    }

    private static void RestrictActionToVirtualMouse(InputAction? action, string devicePath)
    {
        if (action == null) return;
        for (int i = 0; i < action.bindings.Count; i++)
        {
            var binding = action.bindings[i];
            if (binding.path.Contains("<Mouse>"))
            {
                var newPath = binding.path.Replace("<Mouse>", devicePath);
                action.ApplyBindingOverride(i, newPath);
                Debug.Log($"[NOVR] Overriding UI binding path: '{binding.path}' -> '{newPath}' for action '{action.name}'");
            }
        }
    }
}
