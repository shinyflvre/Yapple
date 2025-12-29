using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using Valve.VR;

public sealed class YapplVROverlayPointer : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private YapplVROverlayWrist overlay;
    [SerializeField] private Camera uiCamera;
    [SerializeField] private EventSystem eventSystem;

    [Header("Laser Visual")]
    [SerializeField] private float laserLengthMeters = 4.0f;
    [SerializeField] private float laserThicknessMeters = 0.01f;
    [SerializeField] private float dotSizeMeters = 0.03f;

    [Header("Controller Ray")]
    [SerializeField] private bool invertRayForward = false;

    [Header("Match Wrist Texture Flip")]
    [SerializeField] private bool flipHorizontal = true;
    [SerializeField] private bool flipVertical = false;

    private LineRenderer line;
    private Transform dot;

    private PointerEventData ped;
    private readonly List<RaycastResult> hits = new List<RaycastResult>(32);

    private TrackedDevicePose_t[] poses;
    private VRControllerState_t ctrlState;

    private GameObject currentHover;
    private GameObject currentPress;
    private bool lastPressed;

    private void Awake()
    {
        Application.runInBackground = true;

        if (uiCamera == null)
            uiCamera = Camera.main;

        if (eventSystem == null)
            eventSystem = FindObjectOfType<EventSystem>();

        if (eventSystem == null)
        {
            var go = new GameObject("EventSystem");
            eventSystem = go.AddComponent<EventSystem>();
        }

        poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        ped = new PointerEventData(eventSystem);

        EnsureVisuals();
    }

    private void EnsureVisuals()
    {
        if (line == null)
        {
            var go = new GameObject("Yapple_Laser");
            go.transform.SetParent(transform, false);

            line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = laserThicknessMeters;
            line.endWidth = laserThicknessMeters;
            line.numCapVertices = 12;
            line.alignment = LineAlignment.View;

            var sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            mat.color = Color.white;
            line.material = mat;
        }

        if (dot == null)
        {
            var d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(d.GetComponent<Collider>());
            d.name = "Yapple_Dot";
            d.transform.SetParent(transform, false);
            d.transform.localScale = Vector3.one * dotSizeMeters;

            var mr = d.GetComponent<MeshRenderer>();
            mr.sharedMaterial = line.material;
            dot = d.transform;
        }
    }

    private void Update()
    {
        EnsureVisuals();

        if (OpenVR.System == null)
        {
            RenderLaserFallback();
            return;
        }

        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

        uint right = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
        if (right == OpenVR.k_unTrackedDeviceIndexInvalid || !poses[right].bPoseIsValid)
        {
            RenderLaserFallback();
            ClearHover();
            lastPressed = false;
            return;
        }

        PoseToUnity(poses[right].mDeviceToAbsoluteTracking, out Vector3 rPos, out Quaternion rRot);

        Vector3 dir = invertRayForward ? -(rRot * Vector3.forward) : (rRot * Vector3.forward);

        Vector3 end = rPos + dir * laserLengthMeters;
        bool onUi = false;

        if (overlay != null && overlay.TryGetOverlayPlane(out Vector3 planeCenter, out Quaternion planeRot, out float planeW, out float planeH))
        {
            if (TryRayPlane(new Ray(rPos, dir), planeCenter, planeRot * Vector3.forward, laserLengthMeters, out Vector3 hitWorld))
            {
                Vector3 local = Quaternion.Inverse(planeRot) * (hitWorld - planeCenter);

                float u = (local.x / planeW) + 0.5f;
                float v = (local.y / planeH) + 0.5f;

                if (flipHorizontal) u = 1f - u;
                if (flipVertical) v = 1f - v;

                if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
                {
                    end = hitWorld;
                    onUi = true;

                    GetReferenceResolution(out int refW, out int refH);
                    float px = u * refW;
                    float py = v * refH;

                    bool pressed = GetRightTriggerPressed(right);
                    bool down = pressed && !lastPressed;
                    bool up = !pressed && lastPressed;

                    UpdateUI(px, py, down, up);

                    lastPressed = pressed;
                }
            }
        }

        if (!onUi)
        {
            ClearHover();
            currentPress = null;
            lastPressed = false;
        }

        RenderLaser(rPos, end);
    }

    private void RenderLaser(Vector3 start, Vector3 end)
    {
        line.startWidth = laserThicknessMeters;
        line.endWidth = laserThicknessMeters;

        line.SetPosition(0, start);
        line.SetPosition(1, end);

        dot.position = end;
        dot.localScale = Vector3.one * dotSizeMeters;

        if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
        if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
    }

    private void RenderLaserFallback()
    {
        Vector3 start = transform.position;
        Vector3 end = start + transform.forward * laserLengthMeters;
        RenderLaser(start, end);
    }

    private void GetReferenceResolution(out int w, out int h)
    {
        if (uiCamera != null)
        {
            if (uiCamera.targetTexture != null)
            {
                w = Mathf.Max(1, uiCamera.targetTexture.width);
                h = Mathf.Max(1, uiCamera.targetTexture.height);
                return;
            }

            w = Mathf.Max(1, uiCamera.pixelWidth);
            h = Mathf.Max(1, uiCamera.pixelHeight);
            return;
        }

        w = Mathf.Max(1, Screen.width);
        h = Mathf.Max(1, Screen.height);
    }

    private void UpdateUI(float x, float y, bool down, bool up)
    {
        if (eventSystem == null) return;

        ped.Reset();
        ped.position = new Vector2(x, y);
        ped.delta = Vector2.zero;
        ped.scrollDelta = Vector2.zero;

        hits.Clear();
        eventSystem.RaycastAll(ped, hits);

        RaycastResult rr = default;
        for (int i = 0; i < hits.Count; i++)
        {
            if (hits[i].gameObject != null)
            {
                rr = hits[i];
                break;
            }
        }

        GameObject newHover = rr.gameObject;

        if (newHover != currentHover)
        {
            if (currentHover != null)
                ExecuteEvents.Execute(currentHover, ped, ExecuteEvents.pointerExitHandler);

            currentHover = newHover;

            if (currentHover != null)
                ExecuteEvents.Execute(currentHover, ped, ExecuteEvents.pointerEnterHandler);
        }

        if (down && currentHover != null)
        {
            ped.pointerPressRaycast = rr;
            ped.pointerCurrentRaycast = rr;
            ped.pressPosition = ped.position;
            ped.eligibleForClick = true;

            currentPress = ExecuteEvents.ExecuteHierarchy(currentHover, ped, ExecuteEvents.pointerDownHandler);
            if (currentPress == null)
                currentPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentHover);

            ped.pointerPress = currentPress;
            ped.rawPointerPress = currentHover;
        }

        if (up)
        {
            if (currentPress != null)
                ExecuteEvents.Execute(currentPress, ped, ExecuteEvents.pointerUpHandler);

            GameObject clickHandler = currentHover != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentHover) : null;
            if (ped.eligibleForClick && clickHandler != null && clickHandler == currentPress)
                ExecuteEvents.Execute(clickHandler, ped, ExecuteEvents.pointerClickHandler);

            ped.eligibleForClick = false;
            ped.pointerPress = null;
            ped.rawPointerPress = null;
            currentPress = null;
        }
    }

    private void ClearHover()
    {
        if (currentHover != null)
        {
            ExecuteEvents.Execute(currentHover, ped, ExecuteEvents.pointerExitHandler);
            currentHover = null;
        }
    }

    private bool GetRightTriggerPressed(uint deviceIndex)
    {
        uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VRControllerState_t));
        if (!OpenVR.System.GetControllerState(deviceIndex, ref ctrlState, size))
            return false;

        ulong mask = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
        return (ctrlState.ulButtonPressed & mask) != 0UL;
    }

    private static bool TryRayPlane(Ray ray, Vector3 planePoint, Vector3 planeNormal, float maxDist, out Vector3 hit)
    {
        hit = default;

        float denom = Vector3.Dot(planeNormal, ray.direction);
        if (Mathf.Abs(denom) < 1e-5f) return false;

        float t = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
        if (t < 0f || t > maxDist) return false;

        hit = ray.origin + ray.direction * t;
        return true;
    }

    private static void PoseToUnity(HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
    {
        Vector3 up = new Vector3(m.m1, m.m5, m.m9);
        Vector3 forward = new Vector3(-m.m2, -m.m6, -m.m10);

        pos = new Vector3(m.m3, m.m7, -m.m11);
        rot = Quaternion.LookRotation(forward, up);
    }
}
