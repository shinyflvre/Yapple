using System;
using UnityEngine;
using Valve.VR;
using UnityEngine.Rendering;

public sealed class YapplVROverlayWrist : MonoBehaviour
{
    [Header("Render")]
    [SerializeField] private RenderTexture renderTexture;
    [SerializeField] private bool autoCreateRenderTexture = true;
    [SerializeField] private int autoTextureWidth = 1024;
    [SerializeField] private int autoTextureHeight = 1024;

    [SerializeField] private Camera captureCamera;
    [SerializeField] private bool autoUseMainCamera = true;

    [Header("Texture Orientation")]
    [SerializeField] private bool flipHorizontal = true;
    [SerializeField] private bool flipVertical = false;

    [SerializeField] private float overlayWidthMeters = 0.16f;

    [Header("Attach Left Wrist")]
    [SerializeField] private float positionX = 0.05f;
    [SerializeField] private float positionY = -0.03f;
    [SerializeField] private float positionZ = -0.08f;

    [SerializeField] private float rotationX = 20f;
    [SerializeField] private float rotationY = 180f;
    [SerializeField] private float rotationZ = 90f;

    [Header("Overlay Identity")]
    [SerializeField] private string overlayKey = "com.yappl.overlay.wrist";
    [SerializeField] private string overlayName = "YAPPL Wrist";

    [Header("State")]
    [SerializeField] private bool visible = true;

    private ulong overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    private bool initialized;
    private TrackedDevicePose_t[] poses;

    private CameraTap tap;
    private bool isSRP;

    public RenderTexture Texture => renderTexture;
    public float WidthMeters => overlayWidthMeters;
    public float HeightMeters => renderTexture == null ? 0f : overlayWidthMeters * ((float)renderTexture.height / Mathf.Max(1f, renderTexture.width));

    private void Awake()
    {
        Application.runInBackground = true;
        poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    }

    private void OnEnable()
    {
        isSRP = GraphicsSettings.currentRenderPipeline != null;
        EnsureCamera();
        EnsureRenderTexture();
        EnsureCaptureHook();
        TryInit();
    }

    private void OnDisable()
    {
        RemoveCaptureHook();
        Shutdown();
        ReleaseAutoTexture();
    }

    private void Update()
    {
        if (!initialized) return;
        if (renderTexture == null) return;

        UpdatePoseCache();
        UpdateWristTransform();

        if (isSRP)
            ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);

        ApplyTextureBounds();
        SubmitTexture();

        if (visible) OpenVR.Overlay.ShowOverlay(overlayHandle);
        else OpenVR.Overlay.HideOverlay(overlayHandle);
    }

    public bool TryGetOverlayPlane(out Vector3 center, out Quaternion rotation, out float widthMeters, out float heightMeters)
    {
        center = default;
        rotation = default;
        widthMeters = overlayWidthMeters;
        heightMeters = HeightMeters;

        if (!initialized) return false;

        UpdatePoseCache();

        uint left = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
        if (left == OpenVR.k_unTrackedDeviceIndexInvalid) return false;
        if (!poses[left].bPoseIsValid) return false;

        PoseToUnity(poses[left].mDeviceToAbsoluteTracking, out Vector3 lPos, out Quaternion lRot);

        Vector3 offsetPos = new Vector3(positionX, positionY, positionZ);
        Vector3 offsetRot = new Vector3(rotationX, rotationY, rotationZ);

        Quaternion oRot = lRot * Quaternion.Euler(offsetRot);
        Vector3 oPos = lPos + (lRot * offsetPos);

        center = oPos;
        rotation = oRot;
        return true;
    }

    private void EnsureCamera()
    {
        if (captureCamera != null) return;

        if (autoUseMainCamera)
            captureCamera = Camera.main;
    }

    private void EnsureRenderTexture()
    {
        if (renderTexture != null) return;
        if (!autoCreateRenderTexture) return;

        int w = Mathf.Max(16, autoTextureWidth);
        int h = Mathf.Max(16, autoTextureHeight);

        renderTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        renderTexture.name = "YAPPL_VR_OverlayRT";
        renderTexture.useMipMap = false;
        renderTexture.autoGenerateMips = false;
        renderTexture.Create();
    }

    private void ReleaseAutoTexture()
    {
        if (!autoCreateRenderTexture) return;
        if (renderTexture == null) return;

        try { renderTexture.Release(); } catch { }
        Destroy(renderTexture);
        renderTexture = null;
    }

    private void EnsureCaptureHook()
    {
        if (isSRP) return;
        if (captureCamera == null) return;
        if (renderTexture == null) return;

        tap = captureCamera.GetComponent<CameraTap>();
        if (tap == null)
            tap = captureCamera.gameObject.AddComponent<CameraTap>();

        tap.SetTarget(renderTexture);
    }

    private void RemoveCaptureHook()
    {
        if (tap == null) return;

        try { tap.SetTarget(null); } catch { }
        Destroy(tap);
        tap = null;
    }

    private void TryInit()
    {
        if (initialized) return;
        if (renderTexture == null) return;

        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Overlay);
        if (err != EVRInitError.None) return;

        var e = OpenVR.Overlay.CreateOverlay(overlayKey, overlayName, ref overlayHandle);
        if (e != EVROverlayError.None) return;

        OpenVR.Overlay.SetOverlayWidthInMeters(overlayHandle, overlayWidthMeters);
        OpenVR.Overlay.SetOverlayInputMethod(overlayHandle, VROverlayInputMethod.None);

        ApplyTextureBounds();

        initialized = true;
    }

    private void ApplyTextureBounds()
    {
        if (!initialized) return;

        float uMin = 0f;
        float uMax = 1f;
        float vMin = 0f;
        float vMax = 1f;

        if (flipHorizontal)
        {
            float t = uMin;
            uMin = uMax;
            uMax = t;
        }

        if (flipVertical)
        {
            float t = vMin;
            vMin = vMax;
            vMax = t;
        }

        var b = new VRTextureBounds_t
        {
            uMin = uMin,
            uMax = uMax,
            vMin = vMin,
            vMax = vMax
        };

        OpenVR.Overlay.SetOverlayTextureBounds(overlayHandle, ref b);
    }

    private void UpdatePoseCache()
    {
        if (OpenVR.System == null) return;
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);
    }

    private void UpdateWristTransform()
    {
        uint left = OpenVR.System.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
        if (left == OpenVR.k_unTrackedDeviceIndexInvalid) left = OpenVR.k_unTrackedDeviceIndex_Hmd;
        if (left == OpenVR.k_unTrackedDeviceIndexInvalid) return;

        Vector3 offsetPos = new Vector3(positionX, positionY, positionZ);
        Vector3 offsetRot = new Vector3(rotationX, rotationY, rotationZ);

        HmdMatrix34_t rel = MakeRelativeMatrix(offsetPos, offsetRot);
        OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(overlayHandle, left, ref rel);
    }

    private void SubmitTexture()
    {
        if (renderTexture == null) return;

        IntPtr ptr = renderTexture.GetNativeTexturePtr();

        ETextureType type = ETextureType.DirectX;
        var g = SystemInfo.graphicsDeviceType;

        if (g == GraphicsDeviceType.OpenGLCore) type = ETextureType.OpenGL;
        else if (g == GraphicsDeviceType.Vulkan) type = ETextureType.Vulkan;
        else type = ETextureType.DirectX;

        var t = new Texture_t
        {
            handle = ptr,
            eType = type,
            eColorSpace = EColorSpace.Auto
        };

        OpenVR.Overlay.SetOverlayTexture(overlayHandle, ref t);
    }

    private void Shutdown()
    {
        if (overlayHandle != OpenVR.k_ulOverlayHandleInvalid)
        {
            OpenVR.Overlay.HideOverlay(overlayHandle);
            OpenVR.Overlay.DestroyOverlay(overlayHandle);
            overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
        }

        if (initialized)
        {
            OpenVR.Shutdown();
            initialized = false;
        }
    }

    private static HmdMatrix34_t MakeRelativeMatrix(Vector3 pos, Vector3 euler)
    {
        Matrix4x4 m = Matrix4x4.TRS(pos, Quaternion.Euler(euler), Vector3.one);

        return new HmdMatrix34_t
        {
            m0 = m.m00,
            m1 = m.m01,
            m2 = m.m02,
            m3 = m.m03,
            m4 = m.m10,
            m5 = m.m11,
            m6 = m.m12,
            m7 = m.m13,
            m8 = m.m20,
            m9 = m.m21,
            m10 = m.m22,
            m11 = m.m23
        };
    }

    private static void PoseToUnity(HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
    {
        Vector3 up = new Vector3(m.m1, m.m5, m.m9);
        Vector3 forward = new Vector3(-m.m2, -m.m6, -m.m10);

        pos = new Vector3(m.m3, m.m7, -m.m11);
        rot = Quaternion.LookRotation(forward, up);
    }

    private sealed class CameraTap : MonoBehaviour
    {
        private RenderTexture target;

        public void SetTarget(RenderTexture rt)
        {
            target = rt;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            Graphics.Blit(src, dst);

            if (target != null)
                Graphics.Blit(src, target);
        }
    }
}
