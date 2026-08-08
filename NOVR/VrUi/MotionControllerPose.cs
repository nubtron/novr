using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

namespace NOVR.VrUi;

/// <summary>
/// Reads a motion controller's pose, preferring the Input System XR
/// controller (how Unity OpenXR exposes interaction-profile devices) and
/// falling back to the legacy UnityEngine.XR device API. Also returns the
/// headset pose from the same API so callers can express the controller
/// pose relative to the head (independent of the tracking origin).
/// </summary>
public static class MotionControllerPose
{
    /// <summary>
    /// Unity XR exposes the controller's GRIP pose as deviceRotation; the
    /// grip's +Z is not the pointing direction. This constant maps the grip
    /// frame to the AIM frame (whose +Z points where the controller points),
    /// derived empirically from a Quest Touch held in a natural aiming pose
    /// (grip +Z read ~63 deg up of the aim).
    /// </summary>
    private static readonly Quaternion GripToAim = Quaternion.FromToRotation(
        Vector3.forward,
        new Vector3(0.225f, -0.864f, 0.45f).normalized);

    public static bool TryRead(XRNode node, out Vector3 position, out Quaternion rotation, out Quaternion headRotation, out Vector3 headPosition, out float trigger)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        headRotation = Quaternion.identity;
        headPosition = Vector3.zero;
        trigger = 0f;

        var inputSystemController = node == XRNode.RightHand ? XRController.rightHand : XRController.leftHand;
        if (inputSystemController != null)
        {
            var isTrackedControl = inputSystemController.TryGetChildControl<ButtonControl>("isTracked");
            var tracked = isTrackedControl == null || isTrackedControl.ReadValue() > 0.5f;
            var positionControl = inputSystemController.TryGetChildControl<Vector3Control>("devicePosition");
            var rotationControl = inputSystemController.TryGetChildControl<QuaternionControl>("deviceRotation");
            if (tracked && positionControl != null && rotationControl != null)
            {
                position = positionControl.ReadValue();
                rotation = rotationControl.ReadValue() * GripToAim;
                var triggerControl = inputSystemController.TryGetChildControl<AxisControl>("trigger");
                if (triggerControl != null)
                {
                    trigger = triggerControl.ReadValue();
                }

                return TryReadHeadPose(out headRotation, out headPosition);
            }
        }

        var legacy = InputDevices.GetDeviceAtXRNode(node);
        if (legacy.isValid)
        {
            var isTracked = !legacy.TryGetFeatureValue(XRCommonUsages.isTracked, out var trackedFlag) || trackedFlag;
            if (isTracked && legacy.TryGetFeatureValue(XRCommonUsages.deviceRotation, out var legacyRotation))
            {
                legacy.TryGetFeatureValue(XRCommonUsages.devicePosition, out var legacyPosition);
                legacy.TryGetFeatureValue(XRCommonUsages.trigger, out trigger);
                position = legacyPosition;
                rotation = legacyRotation * GripToAim;
                return TryReadHeadPose(out headRotation, out headPosition);
            }
        }

        return false;
    }

    private static bool TryReadHeadPose(out Quaternion headRotation, out Vector3 headPosition)
    {
        var headDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
        if (headDevice.isValid &&
            headDevice.TryGetFeatureValue(XRCommonUsages.deviceRotation, out headRotation))
        {
            headDevice.TryGetFeatureValue(XRCommonUsages.devicePosition, out headPosition);
            return true;
        }

        headRotation = Quaternion.identity;
        headPosition = Vector3.zero;
        return false;
    }
}
