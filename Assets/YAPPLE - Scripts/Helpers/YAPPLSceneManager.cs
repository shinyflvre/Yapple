using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class YAPPLSceneManager : MonoBehaviour
{
    private static YAPPLSceneManager instance;

    public bool isYapple;
    public bool isVoiceChanger;

#if UNITY_EDITOR
    [SerializeField] private UnityEditor.SceneAsset yappleScene;
    [SerializeField] private UnityEditor.SceneAsset voiceChangerScene;
#endif

    [SerializeField] private string yappleSceneName;
    [SerializeField] private string voiceChangerSceneName;

    public Toggle enableVCToggle;

    public string voiceChangerInstanceName = "YAPPL - VoiceChanger";

    public bool useSeparateVcExecutable = true;
    public string voiceChangerExeFileNameOverride = "";

    public const string VoiceChangerPrefix = "--yappl-vc";

    private const string VcNameArgPrefix = "--vc-name=";
    private const string VcQuitFile = "yappl_vc.quit";
    private const string VcPidFile = "yappl_vc.pid";

    private bool isQuitting;
    private bool listenersWired;

    private void OnValidate()
    {
#if UNITY_EDITOR
        if (yappleScene != null) yappleSceneName = yappleScene.name;
        if (voiceChangerScene != null) voiceChangerSceneName = voiceChangerScene.name;
#endif
        if (isYapple && isVoiceChanger)
        {
            isVoiceChanger = false;
        }
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);

        var args = Environment.GetCommandLineArgs();
        bool argIsVc = args.Any(a => string.Equals(a, VoiceChangerPrefix, StringComparison.OrdinalIgnoreCase));
        string vcNameFromArgs = GetArgValue(args, VcNameArgPrefix);

        bool shouldRunAsVc = argIsVc || isVoiceChanger;

        if (shouldRunAsVc)
        {
            WriteVcPid();
            ClearVcQuitSignal();

            if (!string.IsNullOrWhiteSpace(vcNameFromArgs))
            {
                voiceChangerInstanceName = vcNameFromArgs;
            }

            if (!string.IsNullOrEmpty(voiceChangerSceneName) && SceneManager.GetActiveScene().name != voiceChangerSceneName)
            {
                SceneManager.LoadScene(voiceChangerSceneName);
            }

            StartCoroutine(VcQuitWatcher());
            return;
        }

        if (!string.IsNullOrEmpty(yappleSceneName) && SceneManager.GetActiveScene().name != yappleSceneName)
        {
            SceneManager.LoadScene(yappleSceneName);
            return;
        }

        WireMainUiIfPresent();
    }

    private void OnEnable()
    {
        if (!listenersWired && IsMainProcess())
        {
            WireMainUiIfPresent();
        }
    }

    private void OnDisable()
    {
        if (enableVCToggle != null)
        {
            enableVCToggle.onValueChanged.RemoveListener(OnEnableVCToggleChanged);
        }
        listenersWired = false;
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
        if (IsVoiceChangerProcess())
        {
            TryDeleteFile(GetVcPidPath());
            TryDeleteFile(GetVcQuitPath());
        }
    }

    private void WireMainUiIfPresent()
    {
        if (enableVCToggle == null)
        {
            listenersWired = true;
            return;
        }

        enableVCToggle.onValueChanged.RemoveListener(OnEnableVCToggleChanged);
        enableVCToggle.onValueChanged.AddListener(OnEnableVCToggleChanged);

        enableVCToggle.SetIsOnWithoutNotify(IsVcRunning());

        listenersWired = true;
    }

    private void OnEnableVCToggleChanged(bool enabled)
    {
        if (!IsMainProcess()) return;

        if (enabled)
        {
            ClearVcQuitSignal();
            EnsureVcStarted();
        }
        else
        {
            SignalVcQuit();
            StartCoroutine(ForceCloseVcAfterDelay(1.5f));
        }
    }

    private void EnsureVcStarted()
    {
        if (Application.isEditor) return;
        if (IsVcRunning()) return;

        string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
        string exeDir = Path.GetDirectoryName(currentExePath);

        string startExePath = currentExePath;

        if (useSeparateVcExecutable)
        {
            string vcExeFileName = GetVcExeFileName();
            string vcExePath = Path.Combine(exeDir, vcExeFileName);

            if (TryEnsureVcExeAndDataLink(currentExePath, vcExePath))
            {
                startExePath = vcExePath;
            }
        }

        string nameArg = VcNameArgPrefix + Quote(voiceChangerInstanceName ?? "YAPPL - VoiceChanger");
        string args = VoiceChangerPrefix + " " + nameArg;

        var psi = new ProcessStartInfo
        {
            FileName = startExePath,
            Arguments = args,
            UseShellExecute = true,
            WorkingDirectory = exeDir
        };

        Process.Start(psi);
    }

    private IEnumerator ForceCloseVcAfterDelay(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (isQuitting) yield break;
            if (!IsVcRunning()) yield break;
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        TryKillVcByPid();
    }

    private IEnumerator VcQuitWatcher()
    {
        while (!isQuitting)
        {
            if (File.Exists(GetVcQuitPath()))
            {
                TryDeleteFile(GetVcQuitPath());
                Application.Quit();
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.2f);
        }
    }

    private bool IsMainProcess()
    {
        var args = Environment.GetCommandLineArgs();
        bool argIsVc = args.Any(a => string.Equals(a, VoiceChangerPrefix, StringComparison.OrdinalIgnoreCase));
        return !argIsVc;
    }

    private bool IsVoiceChangerProcess()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Any(a => string.Equals(a, VoiceChangerPrefix, StringComparison.OrdinalIgnoreCase)) || isVoiceChanger;
    }

    private bool IsVcRunning()
    {
        if (!TryReadPid(out int pid)) return false;
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void TryKillVcByPid()
    {
        if (!TryReadPid(out int pid)) return;
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.HasExited)
            {
                p.Kill();
            }
        }
        catch
        {
        }
        TryDeleteFile(GetVcPidPath());
    }

    private void WriteVcPid()
    {
        try
        {
            File.WriteAllText(GetVcPidPath(), Process.GetCurrentProcess().Id.ToString());
        }
        catch
        {
        }
    }

    private bool TryReadPid(out int pid)
    {
        pid = 0;
        try
        {
            string path = GetVcPidPath();
            if (!File.Exists(path)) return false;
            string s = File.ReadAllText(path).Trim();
            return int.TryParse(s, out pid) && pid > 0;
        }
        catch
        {
            return false;
        }
    }

    private void SignalVcQuit()
    {
        try
        {
            File.WriteAllText(GetVcQuitPath(), "1");
        }
        catch
        {
        }
    }

    private void ClearVcQuitSignal()
    {
        TryDeleteFile(GetVcQuitPath());
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private string GetVcQuitPath()
    {
        return Path.Combine(Application.persistentDataPath, VcQuitFile);
    }

    private string GetVcPidPath()
    {
        return Path.Combine(Application.persistentDataPath, VcPidFile);
    }

    private static string GetArgValue(string[] args, string prefix)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(prefix.Length).Trim().Trim('"');
            }
        }
        return null;
    }

    private static string Quote(string s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\"", "") + "\"";
    }

    private string GetVcExeFileName()
    {
        if (!string.IsNullOrWhiteSpace(voiceChangerExeFileNameOverride))
        {
            return EnsureExeSuffix(MakeSafeFileName(voiceChangerExeFileNameOverride));
        }

        string baseName = voiceChangerInstanceName;
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "YAPPL - VoiceChanger";
        return EnsureExeSuffix(MakeSafeFileName(baseName));
    }

    private static string EnsureExeSuffix(string fileName)
    {
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fileName;
        return fileName + ".exe";
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(filtered)) filtered = "YAPPL-VC";
        return filtered;
    }

    private static bool TryEnsureVcExeAndDataLink(string sourceExePath, string targetExePath)
    {
        try
        {
            string dir = Path.GetDirectoryName(sourceExePath);
            string sourceBase = Path.GetFileNameWithoutExtension(sourceExePath);
            string targetBase = Path.GetFileNameWithoutExtension(targetExePath);

            if (string.Equals(sourceBase, targetBase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string sourceData = Path.Combine(dir, sourceBase + "_Data");
            string targetData = Path.Combine(dir, targetBase + "_Data");

            if (!Directory.Exists(sourceData))
            {
                return false;
            }

            if (!EnsureExeUpToDate(sourceExePath, targetExePath))
            {
                return false;
            }

            EnsureJunction(targetData, sourceData);

            return Directory.Exists(targetData);
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureExeUpToDate(string sourceExePath, string targetExePath)
    {
        try
        {
            if (!File.Exists(targetExePath))
            {
                File.Copy(sourceExePath, targetExePath, false);
                return true;
            }

            var src = new FileInfo(sourceExePath);
            var dst = new FileInfo(targetExePath);

            bool same = src.Length == dst.Length && src.LastWriteTimeUtc == dst.LastWriteTimeUtc;
            if (same) return true;

            File.Copy(sourceExePath, targetExePath, true);
            File.SetLastWriteTimeUtc(targetExePath, src.LastWriteTimeUtc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureJunction(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
            {
                TryDeleteDirectory(junctionPath);
            }

            string args = "/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(junctionPath)
            };

            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
