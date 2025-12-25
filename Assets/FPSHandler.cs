using UnityEngine;

public sealed class FPSHandler : MonoBehaviour
{
    [SerializeField, Range(1, 240)] private int foregroundFps = 30;
    [SerializeField, Range(1, 60)] private int backgroundFps = 1;
    [SerializeField] private bool disableVSync = true;

    private bool isFocused;

    private void Awake()
    {
        if (disableVSync)
            QualitySettings.vSyncCount = 0;

        isFocused = Application.isFocused;
        Apply();
    }

    private void OnApplicationFocus(bool focus)
    {
        isFocused = focus;
        Apply();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
            isFocused = false;

        Apply();
    }

    private void Apply()
    {
        int target = isFocused ? foregroundFps : backgroundFps;
        if (target < 1)
            target = 1;

        Application.targetFrameRate = target;
    }
}