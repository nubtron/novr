using UnityEngine;
using UnityEngine.XR;
using NOVR.VrUi;

namespace NOVR.VrMap;

/// <summary>
/// Pointing at the model: a reticle that runs over the ground, a ring round
/// whatever symbol you are on, and a trigger that means the same thing it means
/// on the flat map.
///
/// <para><b>The ray needs no new tracking.</b> The controller models are already
/// posed relative to <c>APIBus.CockpitHudCamera</c> — the same overlay room the
/// model is drawn in — so the pose read for the laser is a ray in the model's own
/// space with no conversion at all. It follows the mod's existing cursor hand and
/// head-gaze setting rather than inventing its own.</para>
///
/// <para><b>Picking is angular, not a quad intersection.</b> A symbol is a
/// centimetre or two across at arm's length and the hand shakes; asking for an
/// exact hit on the rectangle would make the airbase harder to click in VR than
/// it is with a mouse. So each symbol is given its own angular size and the one
/// the ray is furthest *inside* wins — which makes the big symbols easy, the
/// small ones still reachable through a floor on the tolerance, and an overlap
/// resolve towards whatever the pointer is most nearly centred on.</para>
///
/// <para><b>What a click does is the game's decision, with one exception.</b> An
/// airbase goes straight to <c>MapIcon.ClickIcon</c>, which is the same call a
/// mouse makes — including its rule that you may only choose a spawn while you
/// have no aircraft. Units cannot: <c>UnitMapIcon.ClickIcon</c> begins with
/// <c>IsCursorInMapRectangle()</c>, which asks whether the *mouse* is inside the
/// flat map's rectangle, and this map hides that rectangle and has no mouse. So
/// the three lines behind that gate are reproduced instead — select through the
/// HUD while flying, through the map otherwise, and never a unit the target list
/// excludes. That is the one place this layer does not hand the decision back,
/// and the reason is that the gate is about a pointer that does not exist rather
/// than about what the pilot may do.</para>
/// </summary>
internal sealed class WorldMapPointer
{
    /// <summary>Smallest angular half-size any symbol gets, so a footprint is still clickable.</summary>
    private const float MinimumReachDegrees = 1.5f;

    /// <summary>Extra tolerance on top of a symbol's own size, for a hand that is not a mouse.</summary>
    private const float ExtraReachDegrees = 0.75f;

    private const int RingSegments = 36;
    private const float GroundRingRadius = 0.05f;

    private readonly Transform _room;
    private GameObject? _ring;
    private LineRenderer? _ringLine;
    private MapIcon? _hovered;
    private bool _triggerDown;
    private bool _sweptOnce;

    public WorldMapPointer(Transform room) => _room = room;

    public MapIcon? Hovered => _hovered;

    public void Refresh(WorldMapIcons icons, Transform model, float seaLevelY)
    {
        if (!TryRay(out var ray, out var trigger, out var handTracked))
        {
            Hide();
            return;
        }

        // With the self test on and no hand to point with — which is every
        // harness run, because the mock runtime produces no controllers — aim at
        // the biggest symbol instead of the horizon. Not a simulation of the
        // pick: it is the same ray going into the same code, so the ring, its
        // size and whether it draws over the terrain all end up in the frame.
        // The trigger stays where it is, so nothing is clicked.
        if (!handTracked && VrMapConfig.SelfTest != null && VrMapConfig.SelfTest.Value)
        {
            ray = AtBiggest(icons, ray);
        }

        var pick = Pick(icons, ray);
        _hovered = pick.Source;

        // Where on the ground the pointer is, whether or not it is on a symbol:
        // the model's own sea plane, in the room, is the surface being pointed at.
        var plane = new Plane(model.up, model.TransformPoint(new Vector3(0f, seaLevelY, 0f)));
        var onGround = plane.Raycast(new Ray(ray.origin, ray.direction), out var distance) && distance > 0f;

        if (pick.Source != null && pick.Transform != null)
        {
            DrawRing(pick.Transform.position, pick.Radius * 1.4f);
        }
        else if (onGround)
        {
            DrawRing(ray.GetPoint(distance), GroundRingRadius);
        }
        else
        {
            Hide();
        }

        var pressed = trigger > 0.6f;
        var clicked = pressed && !_triggerDown;
        _triggerDown = pressed && trigger > 0.4f;
        if (clicked && _hovered != null) Click(_hovered);
    }

    /// <summary>
    /// The pointing ray, in the room the model lives in. Head gaze when that is
    /// the mod's cursor mode, otherwise the hand that drives the cursor, with the
    /// same head-relative reconstruction the controller models use — which is
    /// what puts the ray in the same space as the model without a conversion.
    /// </summary>
    private static bool TryRay(out Ray ray, out float trigger, out bool handTracked)
    {
        ray = default;
        trigger = 0f;
        handTracked = false;

        var camera = APIBus.CockpitHudCamera;
        if (camera == null) return false;

        var configuration = ModConfiguration.Instance;
        var gaze = configuration != null && configuration.HeadGazeCursor.Value;
        var hand = configuration != null && configuration.CursorInputSource.Value == "Left Hand"
            ? XRNode.LeftHand
            : XRNode.RightHand;

        // The trigger is read from whichever hand has one, so a click still works
        // in head-gaze mode.
        if (MotionControllerPose.TryRead(XRNode.RightHand, out _, out _, out _, out _, out var right)) trigger = Mathf.Max(trigger, right);
        if (MotionControllerPose.TryRead(XRNode.LeftHand, out _, out _, out _, out _, out var left)) trigger = Mathf.Max(trigger, left);

        if (!gaze &&
            MotionControllerPose.TryRead(hand, out var position, out var rotation, out var headRotation, out var headPosition, out _))
        {
            var relativeRotation = Quaternion.Inverse(headRotation) * rotation;
            var relativePosition = Quaternion.Inverse(headRotation) * (position - headPosition);
            ray = new Ray(
                camera.transform.TransformPoint(relativePosition),
                camera.transform.rotation * relativeRotation * Vector3.forward);
            handTracked = true;
            return true;
        }

        ray = new Ray(camera.transform.position, camera.transform.forward);
        return true;
    }

    /// <summary>The ray from where we are to the largest symbol on the model.</summary>
    private static Ray AtBiggest(WorldMapIcons icons, Ray ray)
    {
        var biggest = default(WorldMapIcons.Placed);
        foreach (var symbol in icons.Symbols())
        {
            if (symbol.Transform == null) continue;
            if (biggest.Transform != null && symbol.Radius <= biggest.Radius) continue;
            biggest = symbol;
        }

        if (biggest.Transform == null) return ray;
        var direction = biggest.Transform.position - ray.origin;
        return direction.sqrMagnitude > 1e-6f ? new Ray(ray.origin, direction.normalized) : ray;
    }

    /// <summary>
    /// The symbol the ray is furthest inside, by angle. Nothing if the ray is
    /// inside none of them — a near miss is a miss, or the ground cursor would
    /// snap to whatever is vaguely over there.
    /// </summary>
    private static WorldMapIcons.Placed Pick(WorldMapIcons icons, Ray ray)
    {
        var best = default(WorldMapIcons.Placed);
        var bestSlack = float.MaxValue;

        foreach (var symbol in icons.Symbols())
        {
            if (symbol.Transform == null) continue;
            var toIcon = symbol.Transform.position - ray.origin;
            var distance = toIcon.magnitude;
            if (distance < 1e-3f || Vector3.Dot(toIcon, ray.direction) <= 0f) continue;

            var reach = Mathf.Max(MinimumReachDegrees, Mathf.Atan2(symbol.Radius, distance) * Mathf.Rad2Deg)
                        + ExtraReachDegrees;
            var slack = Vector3.Angle(ray.direction, toIcon) - reach;
            if (slack > 0f || slack >= bestSlack) continue;

            bestSlack = slack;
            best = symbol;
        }

        return best;
    }

    /// <summary>
    /// Hand the click back to the game. See the class remarks for why an airbase
    /// can go through <c>ClickIcon</c> and a unit cannot.
    /// </summary>
    private static void Click(MapIcon icon)
    {
        if (icon is AirbaseMapIcon)
        {
            icon.ClickIcon(MapIcon.ClickSource.Controller);
            return;
        }

        if (icon is not UnitMapIcon unitIcon || unitIcon.unit == null) return;

        var map = SceneSingleton<global::DynamicMap>.i;
        var hud = SceneSingleton<CombatHUD>.i;
        var selector = SceneSingleton<TargetListSelector>.i;
        var aircraft = hud != null ? hud.aircraft : null;
        var selected = map != null && map.selectedIcons.Contains(icon);

        if (aircraft != null && !aircraft.disabled)
        {
            if (unitIcon.unit == aircraft) return;
            if (selector != null && selector.CheckExclusions(unitIcon.unit)) return;
            if (selected) hud.DeSelectUnit(unitIcon.unit);
            else hud.SelectUnit(unitIcon.unit);
            return;
        }

        if (map == null) return;
        if (selected) map.DeselectIcon(unitIcon.unit);
        else map.SelectIcon(unitIcon.unit);
    }

    private void DrawRing(Vector3 centre, float radius)
    {
        EnsureRing();
        if (_ringLine == null || _ring == null) return;

        _ring.SetActive(true);

        var camera = APIBus.CockpitHudCamera;
        var toHead = camera != null ? camera.transform.position - centre : Vector3.up;

        // Width in angle, not in metres. A fixed 4 mm line is about one pixel at
        // the far side of a 68 m model — drawn, and invisible, which is exactly
        // what the first version did.
        _ringLine.widthMultiplier = Mathf.Clamp(toHead.magnitude * 0.008f, 0.004f, 0.2f);

        var forward = toHead.sqrMagnitude > 1e-6f ? toHead.normalized : Vector3.up;
        var right = Vector3.Cross(forward, Vector3.up);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(forward, Vector3.forward);
        right.Normalize();
        var up = Vector3.Cross(right, forward);

        for (var i = 0; i < RingSegments; i++)
        {
            var angle = i / (float)(RingSegments - 1) * Mathf.PI * 2f;
            _ringLine.SetPosition(i, centre + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius);
        }
    }

    private void EnsureRing()
    {
        if (_ring != null) return;

        _ring = new GameObject("NOVR World Map Pointer");
        _ring.transform.SetParent(_room, false);
        _ring.layer = (int)LayerHelper.GetVrUiLayer();

        _ringLine = _ring.AddComponent<LineRenderer>();
        _ringLine.useWorldSpace = true;
        _ringLine.positionCount = RingSegments;
        _ringLine.startWidth = 0.004f;
        _ringLine.endWidth = 0.004f;
        _ringLine.startColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        _ringLine.endColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        _ringLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _ringLine.receiveShadows = false;

        // Over the model, not into it — the same rule the symbols follow, and for
        // the same reason: a reticle that a ridge can hide is a reticle you
        // cannot use. Missing this is why the first ring never appeared. The
        // airbase it was drawn around sits in a valley past a ridge and is itself
        // only visible because its symbol ignores depth; the ring, which did not,
        // was behind the hill in every frame.
        var shader = Shader.Find("UI/Default");
        if (shader != null)
        {
            var material = new Material(shader) { name = "NOVR World Map Pointer" };
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            _ringLine.material = material;
            return;
        }

        var fallback = Shader.Find("Sprites/Default");
        if (fallback != null) _ringLine.material = new Material(fallback);
    }

    public void Hide()
    {
        _hovered = null;
        if (_ring != null && _ring.activeSelf) _ring.SetActive(false);
    }

    public void Destroy()
    {
        if (_ring != null) Object.Destroy(_ring);
        _ring = null;
        _ringLine = null;
    }

    /// <summary>
    /// What the pick would choose, swept down through the model, once.
    ///
    /// <para>There is no other way to test this without a headset: the harness
    /// runs against a mock runtime that produces no controllers at all — the log
    /// says so every launch — so the live path cannot fire and the head looks at
    /// the horizon. Sweeping a synthetic ray through the pitches the model
    /// occupies exercises the same <see cref="Pick"/> the trigger uses and says
    /// which symbol each angle lands on.</para>
    /// </summary>
    public void Sweep(WorldMapIcons icons)
    {
        if (_sweptOnce) return;
        _sweptOnce = true;

        var camera = APIBus.CockpitHudCamera;
        if (camera == null) return;

        var origin = camera.transform.position;
        var found = 0;
        var report = new System.Text.StringBuilder();

        for (var pitch = 0f; pitch <= 89f; pitch += 0.5f)
        {
            for (var yaw = -60f; yaw <= 60f; yaw += 0.5f)
            {
                var direction = camera.transform.rotation * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
                var pick = Pick(icons, new Ray(origin, direction));
                if (pick.Source == null) continue;

                found++;
                report.Append(found == 1 ? "" : ", ")
                      .Append($"{pick.Source.name} at pitch {pitch:F1} yaw {yaw:F1}");
                // One hit per symbol is the useful part; stop the inner sweep so
                // a big symbol does not fill the line with its own name.
                yaw = 60f;
                pitch += 2f;
            }
        }

        Debug.Log(found == 0
            ? "[NOVR] World map pointer sweep: nothing pickable in a 120x89 degree sweep below the head."
            : $"[NOVR] World map pointer sweep: {found} hit(s) — {report}.");
    }
}
