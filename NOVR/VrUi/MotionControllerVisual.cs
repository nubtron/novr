using UnityEngine;
using UnityEngine.XR;

namespace NOVR.VrUi;

/// <summary>
/// Renders a simple stylized motion controller model at each tracked hand
/// (built from Unity primitives, no external assets) plus a laser pointer to
/// the cursor. The model is placed relative to the headset using the same
/// raw XR poses the cursor uses, so it stays glued to the real controller
/// regardless of tracking origin.
/// </summary>
public class MotionControllerVisual : MonoBehaviour
{
    private GameObject _rightModel;
    private GameObject _leftModel;
    private Transform _rightTip;
    private Transform _leftTip;
    private LineRenderer _rightLaser;
    private LineRenderer _leftLaser;
    private Transform _rightReticle;
    private Transform _leftReticle;

    private void Start()
    {
        _rightModel = BuildModel(XRNode.RightHand, out _rightTip, out _rightLaser, out _rightReticle);
        _leftModel = BuildModel(XRNode.LeftHand, out _leftTip, out _leftLaser, out _leftReticle);
    }

    private void Update()
    {
        var showModels = ModConfiguration.Instance.ShowMotionControllers.Value;
        var showLaser = ModConfiguration.Instance.ShowControllerLaser.Value;
        var cursorHand = ModConfiguration.Instance.CursorInputSource.Value switch
        {
            "Right Hand" => XRNode.RightHand,
            "Left Hand" => XRNode.LeftHand,
            _ => (XRNode?)null
        };

        // The laser follows only the hand that drives the cursor; the other
        // hand just shows the model.
        UpdateHand(XRNode.RightHand, _rightModel, _rightTip, _rightLaser, _rightReticle, showModels, showLaser && cursorHand == XRNode.RightHand);
        UpdateHand(XRNode.LeftHand, _leftModel, _leftTip, _leftLaser, _leftReticle, showModels, showLaser && cursorHand == XRNode.LeftHand);
    }

    private static void UpdateHand(
        XRNode node,
        GameObject model,
        Transform tip,
        LineRenderer laser,
        Transform reticle,
        bool showModels,
        bool showLaser)
    {
        if (model == null)
        {
            return;
        }

        var camera = APIBus.CockpitHudCamera;
        Vector3 position = Vector3.zero;
        Vector3 headPosition = Vector3.zero;
        Quaternion rotation = Quaternion.identity;
        Quaternion headRotation = Quaternion.identity;
        var tracked = camera != null &&
                      MotionControllerPose.TryRead(node, out position, out rotation, out headRotation, out headPosition, out _);

        if (!tracked)
        {
            model.SetActive(false);
            return;
        }

        model.SetActive(showModels);

        // Anchor to the headset exactly like the cursor: express the raw
        // controller pose relative to the raw headset pose, then apply it to
        // the pose-driven camera transform.
        var relativeRotation = Quaternion.Inverse(headRotation) * rotation;
        var relativePosition = Quaternion.Inverse(headRotation) * (position - headPosition);
        model.transform.SetPositionAndRotation(
            camera.transform.TransformPoint(relativePosition),
            camera.transform.rotation * relativeRotation);

        if (laser == null || tip == null)
        {
            return;
        }

        laser.enabled = showModels && showLaser;
        if (laser.enabled)
        {
            var start = tip.position;
            Vector3 end = VrUiCursor.Instance != null && VrUiCursor.Instance.IsActive
                ? VrUiCursor.Instance.CursorPosition
                : tip.position + model.transform.forward * 5f;

            laser.SetPosition(0, start);
            laser.SetPosition(1, end);

            if (reticle != null)
            {
                reticle.gameObject.SetActive(true);
                reticle.position = end;
            }
        }
        else if (reticle != null)
        {
            reticle.gameObject.SetActive(false);
        }
    }

    private GameObject BuildModel(XRNode node, out Transform tip, out LineRenderer laser, out Transform reticle)
    {
        var handName = node == XRNode.RightHand ? "Right" : "Left";
        var root = new GameObject($"{handName}ControllerVisual");
        // Parent to the persistent NOVR root so the model survives scene loads.
        root.transform.SetParent(transform, false);
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        var material = CreateMaterial();

        // Grip: a capsule along the pointing direction.
        var grip = CreatePart(root.transform, "Grip", PrimitiveType.Capsule, material);
        grip.localScale = new Vector3(0.04f, 0.055f, 0.07f);
        grip.localPosition = new Vector3(0f, -0.025f, -0.015f);
        grip.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // Thumb area: a sphere.
        var head = CreatePart(root.transform, "Head", PrimitiveType.Sphere, material);
        head.localScale = Vector3.one * 0.07f;
        head.localPosition = new Vector3(0f, 0.015f, 0.03f);

        // Pointing tip: a short cylinder the laser emits from.
        var tipPart = CreatePart(root.transform, "Tip", PrimitiveType.Cylinder, material);
        tipPart.localScale = new Vector3(0.02f, 0.035f, 0.02f);
        tipPart.localPosition = new Vector3(0f, 0.02f, 0.08f);
        tip = tipPart;

        laser = root.AddComponent<LineRenderer>();
        laser.material = material;
        laser.positionCount = 2;
        laser.useWorldSpace = true;
        laser.startWidth = 0.0025f;
        laser.endWidth = 0.0025f;
        laser.startColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        laser.endColor = new Color(0.2f, 0.9f, 1f, 0.4f);
        laser.enabled = false;

        var reticlePart = CreatePart(root.transform, "Reticle", PrimitiveType.Sphere, material);
        reticlePart.localScale = Vector3.one * 0.016f;
        reticle = reticlePart;
        reticle.gameObject.SetActive(false);

        LayerHelper.SetLayerRecursive(root.transform, LayerHelper.GetVrUiLayer());
        root.SetActive(false);
        return root;
    }

    private static Material CreateMaterial()
    {
        // The game strips unused built-in shaders (Unlit/Color and Standard
        // are gone), so prefer shaders that exist in the build.
        Material material = null;
        foreach (var shaderName in new[]
        {
            "Unlit/AdditiveTextShader",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default"
        })
        {
            var shader = Shader.Find(shaderName);
            if (shader != null)
            {
                material = new Material(shader);
                break;
            }
        }

        if (material == null)
        {
            // Last resort: the primitive default material (may render
            // magenta if its built-in shader is stripped).
            var dummy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            material = new Material(dummy.GetComponent<Renderer>().sharedMaterial);
            Destroy(dummy);
        }

        material.color = new Color(0.2f, 0.85f, 0.95f, 1f);
        return material;
    }

    private static Transform CreatePart(Transform parent, string name, PrimitiveType primitiveType, Material material)
    {
        var part = GameObject.CreatePrimitive(primitiveType);
        part.name = name;
        part.transform.SetParent(parent, false);
        if (material != null)
        {
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        var collider = part.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        return part.transform;
    }
}
