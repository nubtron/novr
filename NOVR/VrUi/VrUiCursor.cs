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
    // Shared with MotionControllerVisual on purpose: see IsControllerIdle.
    private const float ControllerIdleMoveMeters = 0.02f;
    private const float ControllerIdleMoveDegrees = 3f;
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

    private bool _controllerModeActive;
    private Vector3 _controllerIdleLastPosition;
    private Quaternion _controllerIdleLastRotation = Quaternion.identity;
    private float _controllerIdleTime;
    private bool _controllerIdleTracked;
    private bool _hmdGazeActive;
    private bool _stickModeActive;
    private bool _stickModeLogged;
    private Vector2 _stickScreenPosition;
    private bool _stickPositionValid;
    private Vector2 _stickScrollDelta;
    private Vector3 _controllerAimDirection = Vector3.forward;
    private bool _controllerTriggerPressed;
    private bool _controllerTriggerClicked;
    private float _controllerSmoothing = 0.3f;
    private bool _controllerModeLogged;
    private bool _hmdGazeLogged;
    private float _headGazeMultiplier = 2.0f;
    private bool _gazeKeyClickHeld;
    private bool _gazeAnchorCaptured;
    private Quaternion _gazeAnchorRotation = Quaternion.identity;
    private Vector3 _gazeAnchorCenter;
    private int _gazeAnchorCenterPriority;
    private int _gazeAnchorCenterFrame = -1;
    
    
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
        // With the captured-menu backend the game's canvases are still screen
        // space, so every raycaster expects real screen pixels — which the
        // panel, not the UI camera's viewport, is what maps back to them.
        if (_cursor != null && Capture.MenuCaptureBackend.TryGetScreenPoint(_cursor.transform.position, out var panelPoint))
        {
            return panelPoint;
        }

        if (_cursor != null && camera != null)
        {
            Vector3 viewportPoint = camera.WorldToViewportPoint(_cursor.transform.position, Camera.MonoOrStereoscopicEye.Mono);
            float screenX = Mathf.Clamp(viewportPoint.x * Screen.width, 0f, Screen.width);
            float screenY = Mathf.Clamp(viewportPoint.y * Screen.height, 0f, Screen.height);
            return new Vector2(screenX, screenY);
        }
        return Vector2.zero;
    }

    /// <summary>
    /// Report the world-space centre of the surface the cursor is being driven
    /// against, once per frame while it is visible. Head-gaze amplification is
    /// measured from the direction of this point, so looking at the centre of a
    /// menu always puts the cursor at its centre.
    ///
    /// The alternative — the head pose captured when the cursor appeared — is
    /// only correct until that pose stops meaning anything: lift the headset
    /// and put it back down and the anchor is left pointing wherever the
    /// headset happened to be, taking the whole amplified range with it.
    /// Geometry cannot go stale that way.
    ///
    /// Highest priority wins within a frame, so a menu drawn on top of another
    /// surface owns the cursor without depending on script execution order.
    /// </summary>
    public void SetGazeAnchorCenter(Vector3 worldCenter, int priority)
    {
        if (_gazeAnchorCenterFrame == Time.frameCount && priority < _gazeAnchorCenterPriority) return;

        _gazeAnchorCenter = worldCenter;
        _gazeAnchorCenterPriority = priority;
        _gazeAnchorCenterFrame = Time.frameCount;
    }

    private bool TryGetGazeAnchorRotation(Camera camera, out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        // Accept the previous frame too: providers run in Update, and nothing
        // guarantees they run before the cursor does.
        if (_gazeAnchorCenterFrame < Time.frameCount - 1) return false;

        var toCenter = _gazeAnchorCenter - camera.transform.position;
        if (toCenter.sqrMagnitude < 0.0001f) return false;

        rotation = Quaternion.LookRotation(toCenter, Vector3.up);
        return true;
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
            _gazeAnchorCaptured = false;
            _stickPositionValid = false;
            return;
        }

        if (!IsRealCursorVisible()) // This means we don't have to manually show and hide it every game update
        {
            if (_cursor != null)
            {
                _cursor.SetActive(false);
            }
            _gazeAnchorCaptured = false;
            _stickPositionValid = false;
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
            scroll = realMouse.scroll.ReadValue() + _stickScrollDelta,
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
        if (_hmdGazeActive)
        {
            // Head-gaze: the cursor follows where the user looks, optionally
            // amplified by Head Gaze Multiplier so small head turns cover
            // more of the menu (less neck craning). Amplification is relative
            // to a fixed reference: the native menu anchor when one is up,
            // otherwise the direction the user was looking when the cursor
            // appeared. At 1.0x the cursor sits exactly at the view center.
            var referenceRotation = GetProjectionReferenceRotation();
            if (TryGetGazeAnchorRotation(camera, out var centreRotation))
            {
                // Anchored on the surface itself: looking at its centre puts
                // the cursor at its centre, however the headset got here.
                referenceRotation = centreRotation;
            }
            else if (!_hasProjectionReferenceOverride)
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
            // The stick cursor is the mouse cursor with a different source of
            // screen position: same projection, same bounds, same reference
            // rotation — which is what makes it stay put on the panel through
            // a recentre, and what lets the mouse take over mid-menu.
            Vector2 pointerPosition;
            if (_stickModeActive)
            {
                pointerPosition = _stickScreenPosition;
            }
            else
            {
                var mouse = _realMouse;
                if (mouse == null) return;
                pointerPosition = mouse.position.ReadValue();
            }

            float cursorPitch = ProjectPitchAngle(pointerPosition.y);
            float cursorYaw = ProjectYawAngle(pointerPosition.x);

            Vector3 localDirection = Quaternion.Euler(-cursorPitch, cursorYaw, 0f) * Vector3.forward;
            Quaternion referenceRotation = GetProjectionReferenceRotation();
            worldDirection = referenceRotation * localDirection;
        }

        Vector3 viewportSpace = camera.WorldToViewportPoint(camera.transform.position + worldDirection * DefaultProjectionDistance, Camera.MonoOrStereoscopicEye.Mono);
        Vector2 inScreenSpace = new Vector2(viewportSpace.x * Screen.width, viewportSpace.y * Screen.height);
        // The captured menu is a flat panel, so the cursor belongs on its
        // surface; the world-space UI probing below has nothing to hit.
        float cursorDistance = Capture.MenuCaptureBackend.TryGetPanelDistance(camera.transform.position, worldDirection, out var panelDistance)
            ? panelDistance
            : GetDistanceUnderCursor(inScreenSpace);
        Vector3 pos = camera.transform.position + worldDirection * cursorDistance;
        _cursor.transform.position = pos;
        _cursor.transform.rotation = Quaternion.LookRotation(worldDirection, camera.transform.up);
    }

    /// <summary>
    /// Selects and updates the active cursor input mode.
    ///
    /// <para>Two of them are modes: chosen in the config, mutually exclusive,
    /// and one of them is always the one underneath. The stick cursor moves a
    /// position on the panel from the game's own view axes (on by default),
    /// and it takes precedence over head-gaze when both are on. Head-gaze
    /// (off by default) sits the cursor at the center of the view. With both
    /// off it is the mouse, which is why the mouse has no setting of its own:
    /// it is what is left.</para>
    ///
    /// <para>The mouse is not exclusive with the stick cursor — moving it
    /// takes the cursor at once and the stick carries on from where the mouse
    /// left it, so a pilot with both never has to choose. It <i>is</i>
    /// exclusive with head-gaze: a cursor pinned to the center of the view
    /// cannot also be where the mouse put it.</para>
    ///
    /// <para>The motion controller is not a mode at all but a temporary
    /// takeover. While the configured hand is actually being held it outranks
    /// whichever mode is underneath, and Controller Idle Timeout of stillness
    /// hands the cursor straight back to it.</para>
    /// </summary>
    private void UpdateCursorInput()
    {
        _hmdGazeActive = false;
        _controllerModeActive = false;
        _stickModeActive = false;
        _stickScrollDelta = Vector2.zero;

        // A controller in the hand outranks whichever mode is configured, and
        // only for as long as it is held: UpdateControllerInput hands the
        // cursor back after Controller Idle Timeout of stillness. That is what
        // lets a controller work alongside the stick cursor and head-gaze
        // rather than in place of them — pick it up to point at something, put
        // it down and the mode underneath has the cursor again.
        UpdateControllerInput();
        if (_controllerModeActive)
        {
            // Give the stick cursor the position the controller is pointing
            // at, so putting the controller down carries on from there instead
            // of snapping back to wherever the stick left the cursor before it
            // was picked up. GetScreenPoint reads the cursor as it was placed
            // last frame — one frame behind, which at this scale is invisible.
            if (StickCursorConfig.Enabled)
            {
                _stickScreenPosition = GetScreenPoint();
                _stickPositionValid = true;
            }
            _stickModeLogged = false;
            return;
        }

        if (StickCursorConfig.Enabled)
        {
            _stickModeActive = true;
            UpdateStickCursorInput();
            if (!_stickModeLogged)
            {
                Debug.Log("[VrUiCursor] Stick cursor active: the cursor is driven by the game's own view axes " +
                          $"(map axes as well: {StickCursorConfig.UseMapAxes}; moves over the maximized map: " +
                          $"{StickCursorConfig.MoveOverMap}); clicks from trigger, Fire or Select.");
                _stickModeLogged = true;
            }
            return;
        }
        _stickModeLogged = false;

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
    }

    /// <summary>
    /// Drives the cursor from an XR motion controller ray when the configured
    /// input source is a hand and that controller is being held. Leaves
    /// <see cref="_controllerModeActive"/> false — i.e. hands the cursor to
    /// whichever mode is configured — when the source is the mouse, the
    /// controller is not tracked, or it has been put down.
    /// </summary>
    private void UpdateControllerInput()
    {
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
            _controllerIdleTracked = false;
            return;
        }

        // A controller drives the cursor while it is being held, and hands it
        // back when it is put down. Without this, choosing a hand as the
        // Cursor Input Source kills the mouse for the rest of the session the
        // moment a tracked controller is switched on, even while it lies on
        // the desk — and the controller is the one input the pilot cannot
        // reach without letting go of something else.
        if (IsControllerIdle(controllerPosition, controllerRotation))
        {
            if (_controllerModeLogged)
            {
                Debug.Log("[VrUiCursor] Controller idle; the cursor goes back to the other input.");
                _controllerModeLogged = false;
            }

            // Do not leave a held trigger behind: the next thing to read these
            // is whatever mode takes over.
            _controllerTriggerPressed = false;
            _controllerTriggerClicked = false;
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
    /// Whether the configured controller has been still long enough to count
    /// as put down.
    ///
    /// <para>Deliberately the same rule <see cref="MotionControllerVisual"/>
    /// hides the model by — the same 2 cm / 3° thresholds and the same
    /// <c>Controller Idle Timeout</c> — so the controller the pilot can see
    /// and the controller that owns the cursor appear and disappear together.
    /// Two different idle rules would give a visible controller that does not
    /// point at anything, or an invisible one that still holds the cursor.</para>
    ///
    /// <para>A timeout of 0 disables it, which is the old behaviour: the
    /// controller keeps the cursor for as long as it is tracked.</para>
    /// </summary>
    private bool IsControllerIdle(Vector3 position, Quaternion rotation)
    {
        var timeout = ModConfiguration.Instance.ControllerIdleTimeout.Value;
        if (timeout <= 0f)
        {
            _controllerIdleTime = 0f;
            _controllerIdleTracked = false;
            return false;
        }

        if (!_controllerIdleTracked)
        {
            // The first tracked frame counts as movement: picking a controller
            // up is precisely the case this must not sit out.
            _controllerIdleTracked = true;
            _controllerIdleTime = 0f;
        }
        else
        {
            var moved = Vector3.Distance(position, _controllerIdleLastPosition) > ControllerIdleMoveMeters ||
                        Quaternion.Angle(_controllerIdleLastRotation, rotation) > ControllerIdleMoveDegrees;
            _controllerIdleTime = moved ? 0f : _controllerIdleTime + Time.unscaledDeltaTime;
        }

        _controllerIdleLastPosition = position;
        _controllerIdleLastRotation = rotation;
        return _controllerIdleTime > timeout;
    }

    /// <summary>
    /// Move the cursor from the game's own axes, in screen space, and clamp it
    /// to the screen rect the projection maps from.
    ///
    /// <para><b>Which axes, and why the game's own.</b> "Pan View"/"Tilt View"
    /// are dead sticks in a VR cockpit — the head does the looking, and
    /// <c>CameraCockpitStatePatch</c> already zeroes the state's panView and
    /// tiltView every frame — so taking them costs nothing and needs no new
    /// binding from the pilot. "Move Map Horizontal"/"Move Map Vertical" are
    /// only read by <c>DynamicMap.MapControls</c>, which the game runs solely
    /// while the map is maximized; everywhere else they are free, so the
    /// cursor gets them there and gives them back over the map.</para>
    ///
    /// <para><b>Velocity, not position.</b> The axis is integrated as a rate,
    /// which is how the flat game treats it too (<c>panView +=
    /// GetAxis("Pan View") * ...</c>). It also makes the same action work
    /// whether it is bound to a self-centring stick, a hat or a mouse axis —
    /// an absolute mapping would only be meaningful for the first.</para>
    ///
    /// <para><b>Who owns the stick over the map.</b> "Free everywhere else"
    /// is true of the *actions* and says nothing about the hardware under
    /// them. On the one pad this was measured on, a saved gamepad map bound
    /// the view axes and the map axes to the same two stick elements — so
    /// over the maximized map one stick would scroll the map and drag the
    /// cursor off the icon in the same motion. There the map wins by default
    /// (<c>Stick Cursor Over Map</c>), which is also what the flat game does
    /// for a pad player: the map moves under a stationary cursor and "Select"
    /// takes whatever is nearest it. The mouse and a motion controller still
    /// move the cursor there — neither of them is the stick the map is
    /// using.</para>
    /// </summary>
    private void UpdateStickCursorInput()
    {
        if (!_stickPositionValid)
        {
            // Centre, rather than wherever the desktop pointer was parked: in
            // a headset the mouse is somewhere you cannot see, and a menu that
            // opens with the cursor already off in a corner reads as a cursor
            // that failed to appear.
            _stickScreenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            _stickPositionValid = true;
        }

        var realMouse = _realMouse;
        if (realMouse != null && realMouse.delta.ReadValue() != Vector2.zero)
        {
            // The mouse still works, and wins the moment it actually moves.
            _stickScreenPosition = realMouse.position.ReadValue();
        }
        else
        {
            var player = GameManager.playerInput;
            if (player != null)
            {
                var mapMaximized = global::DynamicMap.mapMaximized;
                // Over the map, a held (real) mouse button is the game's own
                // drag-pan, which reads these same two axes. Moving the cursor
                // as well would fight it, so the drag wins.
                var dragPanning = mapMaximized && Input.GetMouseButton(0);
                // And over the maximized map the stick belongs to the map: see
                // the "Who owns the stick over the map" paragraph above.
                var mapOwnsStick = mapMaximized && !StickCursorConfig.MoveOverMap;

                var axis = Vector2.zero;
                if (!dragPanning && !mapOwnsStick)
                {
                    // Screen-space signs, not view signs: the game's view axes
                    // mean "+Pan View = right, +Tilt View = down". Both are
                    // readable off the flat game twice over — CameraCockpitState
                    // feeds tiltView straight into Euler X (positive = looking
                    // down), and the radial menu, the one screen-space pointer
                    // the flat game builds out of these same two axes, takes
                    // "GetAxis("Pan View") * right - GetAxis("Tilt View") * up".
                    axis.x += ApplyStickDeadzone(player.GetAxis("Pan View"));
                    axis.y -= ApplyStickDeadzone(player.GetAxis("Tilt View"));
                }
                if (StickCursorConfig.UseMapAxes && !mapMaximized)
                {
                    // These two are already screen-space: DynamicMap adds them
                    // to positionOffset and applies -offset to the map image,
                    // so positive scrolls the view right and up.
                    axis.x += ApplyStickDeadzone(player.GetAxis("Move Map Horizontal"));
                    axis.y += ApplyStickDeadzone(player.GetAxis("Move Map Vertical"));
                }
                if (StickCursorConfig.InvertVertical)
                {
                    axis.y = -axis.y;
                }
                // Two sources can push the same way; a diagonal must not be
                // faster than a straight line either.
                if (axis.sqrMagnitude > 1f)
                {
                    axis.Normalize();
                }

                // Unscaled, because every surface this cursor is for runs at
                // timeScale 0, and capped, because a frame lost to a scene load
                // must not fling the cursor across the panel.
                var deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                var speed = StickCursorConfig.Speed * deltaTime;
                _stickScreenPosition += new Vector2(axis.x * Screen.width, axis.y * Screen.height) * speed;
            }
        }

        _stickScreenPosition.x = Mathf.Clamp(_stickScreenPosition.x, 0f, Screen.width);
        _stickScreenPosition.y = Mathf.Clamp(_stickScreenPosition.y, 0f, Screen.height);

        UpdateStickScrollInput();
        UpdateStickClickInput();
    }

    private static float ApplyStickDeadzone(float value)
    {
        var deadzone = Mathf.Clamp(StickCursorConfig.Deadzone, 0f, 0.9f);
        var magnitude = Mathf.Abs(value);
        if (magnitude <= deadzone) return 0f;

        // Rescaled rather than merely clipped, so the first millimetre past the
        // deadzone is a crawl instead of a jump.
        return Mathf.Sign(value) * Mathf.Clamp01((magnitude - deadzone) / (1f - deadzone));
    }

    /// <summary>
    /// Scroll the list under the cursor with the "Zoom View" axis, so a long
    /// settings page is reachable without a wheel.
    ///
    /// <para>Only while flight controls are off and the map is not maximized:
    /// those are exactly the two states in which something else already owns
    /// that axis — <c>VrZoomController</c> in the cockpit and the map's own
    /// zoom over the map — and both of those are worth more than scrolling.</para>
    /// </summary>
    private void UpdateStickScrollInput()
    {
        _stickScrollDelta = Vector2.zero;

        var speed = StickCursorConfig.ScrollSpeed;
        if (speed <= 0f) return;
        if (GameManager.flightControlsEnabled || global::DynamicMap.mapMaximized) return;

        var player = GameManager.playerInput;
        if (player == null) return;

        var axis = ApplyStickDeadzone(player.GetAxis("Zoom View"));
        if (axis == 0f) return;

        // 120 units is one wheel notch, which is what InputSystemUIInputModule
        // divides by; the rest is notches per second.
        var deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        _stickScrollDelta = new Vector2(0f, axis * 120f * speed * deltaTime);
    }

    /// <summary>
    /// Clicks for the stick cursor: either trigger, the game's "Fire", or the
    /// game's "Select" — the action the flat game already uses to pick an icon
    /// off the tactical map, so the button the pilot reaches for there also
    /// works on every other panel.
    /// </summary>
    private void UpdateStickClickInput()
    {
        var pressed =
            MotionControllerPose.TryRead(XRNode.RightHand, out _, out _, out _, out _, out var rightTrigger) && rightTrigger > 0.5f ||
            MotionControllerPose.TryRead(XRNode.LeftHand, out _, out _, out _, out _, out var leftTrigger) && leftTrigger > 0.5f;

        var player = GameManager.playerInput;
        if (player != null && (player.GetButton("Fire") || player.GetButton("Select")))
        {
            pressed = true;
        }

        _controllerTriggerClicked = pressed && !_controllerTriggerPressed;
        _controllerTriggerPressed = pressed;
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

        // Held while the key is down, plus the frame it went down so a tap
        // shorter than a frame still clicks.
        //
        // What this must not do is latch on the press edge and clear on the
        // release edge. A release edge only exists in a frame this component
        // runs, and Update() returns above here whenever the window is not
        // focused — so an alt-tab loses it. That is not a corner case: the
        // default click key is LeftAlt, so *Alt+Tab presses it*. The keydown
        // landed while the game still had focus, the release never did, and
        // the latch survived the focus cycle holding the virtual mouse's left
        // button down for the rest of the session. A button that is already
        // down produces no press edge, so nothing was ever clickable again,
        // while the cursor — posed earlier in Update() — went on tracking the
        // head as if nothing were wrong. Reading the key's own state cannot
        // miss a transition it did not see.
        var clickKey = ModConfiguration.Instance.HeadGazeClickKey.Value;
        var keyboard = Keyboard.current;
        if (keyboard != null && clickKey != Key.None)
        {
            var key = keyboard[clickKey];
            _gazeKeyClickHeld = key.isPressed || key.wasPressedThisFrame;
        }
        else
        {
            _gazeKeyClickHeld = false;
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
