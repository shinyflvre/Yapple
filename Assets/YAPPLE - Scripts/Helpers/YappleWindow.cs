using UnityEngine;
using UnityEngine.UI;

public sealed class YappleWindow : MonoBehaviour
{
    [SerializeField] Button closeButton;
    [SerializeField] Button minimizeButton;

    void OnEnable()
    {
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        if (minimizeButton != null) minimizeButton.onClick.AddListener(Minimize);
    }

    void OnDisable()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(Close);
        if (minimizeButton != null) minimizeButton.onClick.RemoveListener(Minimize);
    }

    public void Close()
    {
        Application.Quit();
    }

    public void Minimize()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        MinimizeWindows();
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern System.IntPtr GetActiveWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

    const int SW_MINIMIZE = 6;

    static void MinimizeWindows()
    {
        var hWnd = GetActiveWindow();
        if (hWnd != System.IntPtr.Zero) ShowWindow(hWnd, SW_MINIMIZE);
    }
#endif
}
