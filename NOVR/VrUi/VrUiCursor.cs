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

    private bool _controllerModeActive;
    private bool _hmdGazeActive;
    private Vector3 _controllerAimDirection = Vector3.forward;
    private bool _controllerTriggerPressed;
    private bool _controllerTriggerClicked;
    private float _controllerSmoothing = 0.3f;
    private bool _controllerModeLogged;
    private bool _hmdGazeLogged;
    private float _headGazeMultiplier = 1.5f;
    private bool _gazeKeyClickHeld;
    private bool _gazeAnchorCaptured;
    private Quaternion _gazeAnchorRotation = Quaternion.identity;
    
    
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
        _gazeAnchorCaptured = false;
    }

    public void ClearProjectionReferenceRotation()
    {
        _hasProjectionReferenceOverride = false;
        _gazeAnchorCaptured = false;
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
            _gazeAnchorCaptured = false;
            return;
        }

        if (!IsRealCursorVisible()) // This means we don't have to manually show and hide it every game update
        {
            if (_cursor != null)
            {
                _cursor.SetActive(false);
            }
            _gazeAnchorCaptured = false;
            return;
        }
        
        if (_virtualMouse == null)
        {
            _realMouse = Mouse.current ?? throw new System.InvalidOperationException(
                $"[{nameof(VrUiCursor)}] Unity InputSystem could not find an active hardware Mouse device during initialization.");
            _virtualMouse = InputSystem.AddDevice<Mouse>("VirtualMouse");
            Debug.Log($"[NOVR] Added VirtualMouse device: name='{_virtualMouse.name}', path='{_virtualMouse.path}', displayName='{_virtualMouse.displayName}'");
        }

        if (!_hasInitializedEventSystem)
        {
            if (RestrictUIModuleToVirtualMouse())
            {
                _hasInitializedEventSystem = true;
            }
        }
        if (_texture == null) return;
        UpdateCursorInput();
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
        if (_hmdGazeActive)
        {
            // Head-gaze: the cursor follows where the user looks, optionally
            // amplified by Head Gaze Multiplier so small head turns cover
            // more of the menu (less neck craning). Amplification is relative
            // to a fixed reference: the native menu anchor when one is up,
            // otherwise the direction the user was looking when the cursor
            // appeared. At 1.0x the cursor sits exactly at the view center.
            var referenceRotation = GetProjectionReferenceRotation();
            if (!_hasProjectionReferenceOverride)
            {
                if (!_gazeAnchorCaptured)
                {
                    var headEuler = camera.transform.eulerAngles;
                    _gazeAnchorRotation = Quaternion.Euler(headEuler.x, headEuler.y, 0f);
                    _gazeAnchorCaptured = true;
                }
                referenceRotation = _gazeAnchorRotation;
            }

            var localForward = Quaternion.Inverse(referenceRotation) * camera.transform.forward;
            var gazePitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(localForward.y, -1f, 1f)) * Mathf.Rad2Deg * _headGazeMultiplier, -MaxPitchDegrees, MaxPitchDegrees);
            var gazeYaw = Mathf.Clamp(Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg * _headGazeMultiplier, -MaxYawDegrees, MaxYawDegrees);
            worldDirection = referenceRotation * Quaternion.Euler(-gazePitch, gazeYaw, 0f) * Vector3.forward;
        }
        else if (_controllerModeActive)
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
    /// Selects and updates the active cursor input mode: head-gaze (the
    /// cursor follows the center of the HMD and the trigger clicks), an XR
    /// motion controller ray, or the desktop mouse. Head-gaze is enabled by
    /// default and while it is on, the mouse and motion controller cursor
    /// modes are disabled.
    /// </summary>
    private void UpdateCursorInput()
    {
        _hmdGazeActive = false;
        _controllerModeActive = false;

        if (ModConfiguration.Instance.HeadGazeCursor.Value)
        {
            _hmdGazeActive = true;
            _headGazeMultiplier = Mathf.Clamp(ModConfiguration.Instance.HeadGazeMultiplier.Value, 0.5f, 3.0f);
            UpdateGazeClickInput();
            if (!_hmdGazeLogged)
            {
                Debug.Log("[VrUiCursor] Head-gaze cursor active: cursor follows HMD center; clicks from trigger, Fire action, or the Head Gaze Click Key.");
                _hmdGazeLogged = true;
            }
            return;
        }

        var source = ModConfiguration.Instance.CursorInputSource.Value;

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

        if (!MotionControllerPose.TryRead(node, out var controllerPosition, out var controllerRotation, out _, out _, out var triggerValue))
        {
            if (_controllerModeLogged)
            {
                Debug.Log("[VrUiCursor] Controller not tracked this frame; falling back to mouse.");
                _controllerModeLogged = false;
            }
            return;
        }

        // Aim from the controller's direction relative to the SAME projection
        // reference the mouse uses (the native menu anchor, or world when no
        // override is set). The reference is fixed, so head movement does not
        // move the cursor — it tracks the controller only. Clamping to the
        // mouse's pitch/yaw bounds keeps the cursor inside the HUD.
        var referenceRotation = GetProjectionReferenceRotation();
        var localForward = Quaternion.Inverse(referenceRotation) * (controllerRotation * Vector3.forward);
        var pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(localForward.y, -1f, 1f)) * Mathf.Rad2Deg, -MaxPitchDegrees, MaxPitchDegrees);
        var yaw = Mathf.Clamp(Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg, -MaxYawDegrees, MaxYawDegrees);

        var localDirection = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
        var aimDirection = referenceRotation * localDirection;

        _controllerAimDirection = Vector3.Slerp(_controllerAimDirection, aimDirection, _controllerSmoothing);
        _controllerModeActive = true;

        if (!_controllerModeLogged)
        {
            Debug.Log($"[VrUiCursor] Controller cursor active: controller={controllerRotation.eulerAngles} relPitch={pitch:F1} relYaw={yaw:F1} trigger={triggerValue:F2}");
            _controllerModeLogged = true;
        }

        var triggerPressed = triggerValue > 0.5f;
        _controllerTriggerClicked = triggerPressed && !_controllerTriggerPressed;
        _controllerTriggerPressed = triggerPressed;
    }

    /// <summary>
    /// In head-gaze mode no controller drives the cursor, but a click can come
    /// from several sources: a press on either hand's trigger, the game's
    /// Fire action (Rewired keeps its maps enabled in menus, so Fire reads
    /// there too — whatever the player bound Fire to works as a click), or a
    /// configurable keyboard key. All are ORed into the virtual mouse's left
    /// button, so any of them clicks whatever the gaze cursor is over.
    /// </summary>
    private void UpdateGazeClickInput()
    {
        var triggerPressed =
            MotionControllerPose.TryRead(XRNode.RightHand, out _, out _, out _, out _, out var rightTrigger) && rightTrigger > 0.5f ||
            MotionControllerPose.TryRead(XRNode.LeftHand, out _, out _, out _, out _, out var leftTrigger) && leftTrigger > 0.5f;

        if (GameManager.playerInput != null && GameManager.playerInput.GetButton("Fire"))
        {
            triggerPressed = true;
        }

        // Edge-track the keyboard click key so even a quick tap registers as a
        // full press-and-release instead of being missed between frames.
        var clickKey = ModConfiguration.Instance.HeadGazeClickKey.Value;
        var keyboard = Keyboard.current;
        if (keyboard != null && clickKey != Key.None)
        {
            if (keyboard[clickKey].wasPressedThisFrame)
            {
                _gazeKeyClickHeld = true;
            }
            if (keyboard[clickKey].wasReleasedThisFrame)
            {
                _gazeKeyClickHeld = false;
            }
        }
        if (_gazeKeyClickHeld)
        {
            triggerPressed = true;
        }

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
        float distance = 0f;
        if (TryGetUiDistanceUnderCursor(screenPos, out var uiDistance, out var overInteractive))
        {
            _cursorOverInteractive = overInteractive;
            distance = uiDistance;
        }
        else
        {
            // No interactive UI under the cursor: snap to the tactical map
            // surface when hovering it, so the cursor sits on the map instead
            // of floating at the default distance behind it.
            var dynamicMap = SceneSingleton<global::DynamicMap>.i;
            var camera = UiCamera;
            if (dynamicMap != null && dynamicMap.mapImage != null && camera != null)
            {
                var mapRect = dynamicMap.mapImage.GetComponent<RectTransform>();
                if (mapRect != null && RectTransformUtility.RectangleContainsScreenPoint(mapRect, screenPos, camera))
                {
                    var ray = camera.ScreenPointToRay(screenPos);
                    var plane = new Plane(dynamicMap.mapImage.transform.forward, dynamicMap.mapImage.transform.position);
                    if (plane.Raycast(ray, out var mapDistance) && mapDistance > 0f)
                    {
                        distance = mapDistance;
                    }
                }
            }

            if (distance <= 0f)
            {
                distance = DefaultProjectionDistance;
            }
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
