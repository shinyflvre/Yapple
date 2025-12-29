using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleSystemTriggers : MonoBehaviour
{
    [Serializable]
    private sealed class SystemSettings
    {
        public int version = 1;
        public string triggerWord = string.Empty;
    }

    [Header("Refs")]
    [SerializeField] private YappleCore core;
    [SerializeField] private YappleVosk vosk;

    [Header("UI")]
    [SerializeField] private Button runButton;
    [SerializeField] private TMP_InputField triggerInput;

    [Header("Behavior")]
    [SerializeField, Range(0f, 5f)] private float perWordCooldownSeconds = 0.4f;
    [SerializeField] private bool triggerOnPartial = false;

    [Header("Audio Source")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private List<AudioClip> clips = new List<AudioClip>();
    [SerializeField] private float minPitch = 0.8f;
    [SerializeField] private float maxPitch = 1.2f;

    private const string SettingsFileName = "system_settings.json";

    private string triggerToken = string.Empty;
    private float lastRunTime;
    private AudioSource corePlaybackSource;

    private string lastKnownTriggerWord = string.Empty;
    private bool didLoadSettings;
    private bool isQuitting;

    private void Awake()
    {
        if (core == null)
            core = GetComponent<YappleCore>();

        if (vosk == null)
            vosk = GetComponent<YappleVosk>();

        CacheCorePlaybackSource();
        LoadSystemSettings();

        if (triggerInput != null)
            lastKnownTriggerWord = triggerInput.text ?? string.Empty;

        RefreshTriggerToken();
        ForceRunButtonInteractable();
    }

    private void Start()
    {
        ForceRunButtonInteractable();
    }

    private void OnEnable()
    {
        if (runButton != null)
            runButton.onClick.AddListener(Run);

        if (triggerInput != null)
        {
            triggerInput.onValueChanged.AddListener(OnTriggerValueChanged);
            triggerInput.onEndEdit.AddListener(OnTriggerEndEdit);
        }

        if (vosk != null)
            vosk.OnKeyword += OnKeyword;

        if (core != null)
            core.CollectKeywords += OnCollectKeywords;

        ForceRunButtonInteractable();

        if (core != null)
            core.RequestRebuild();
    }

    private void OnDisable()
    {
        if (runButton != null)
            runButton.onClick.RemoveListener(Run);

        if (triggerInput != null)
        {
            triggerInput.onValueChanged.RemoveListener(OnTriggerValueChanged);
            triggerInput.onEndEdit.RemoveListener(OnTriggerEndEdit);
        }

        if (vosk != null)
            vosk.OnKeyword -= OnKeyword;

        if (core != null)
            core.CollectKeywords -= OnCollectKeywords;
    }

    private void OnApplicationQuit()
    {
        isQuitting = true;
        SaveSystemSettings();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
            SaveSystemSettings();
    }

    private void OnApplicationFocus(bool focus)
    {
        if (!focus)
            SaveSystemSettings();
    }

    private void ForceRunButtonInteractable()
    {
        if (runButton != null)
            runButton.interactable = true;
    }

    private void OnTriggerValueChanged(string _)
    {
        HandleTriggerChanged();
    }

    private void OnTriggerEndEdit(string _)
    {
        HandleTriggerChanged();
    }

    private void HandleTriggerChanged()
    {
        if (triggerInput != null)
            lastKnownTriggerWord = triggerInput.text ?? string.Empty;

        RefreshTriggerToken();
        SaveSystemSettings();

        if (core != null)
            core.RequestRebuild();
    }

    private void RefreshTriggerToken()
    {
        string raw = triggerInput != null ? triggerInput.text : lastKnownTriggerWord;
        triggerToken = NormalizeToken(raw);
    }

    private void OnCollectKeywords(HashSet<string> set)
    {
        if (set == null)
            return;

        if (!string.IsNullOrWhiteSpace(triggerToken))
            set.Add(triggerToken);
    }

    private void OnKeyword(string word, float conf, bool partial)
    {
        if (!triggerOnPartial && partial)
            return;

        if (string.IsNullOrWhiteSpace(word))
            return;

        string w = NormalizeToken(word);
        if (string.IsNullOrWhiteSpace(w))
            return;

        if (string.IsNullOrWhiteSpace(triggerToken))
            return;

        if (!string.Equals(w, triggerToken, StringComparison.OrdinalIgnoreCase))
            return;

        float now = Time.unscaledTime;
        if (now - lastRunTime < perWordCooldownSeconds)
            return;

        if (RunInternal())
            lastRunTime = now;
    }

    public void Run()
    {
        float now = Time.unscaledTime;
        if (now - lastRunTime < 0.05f)
            return;

        if (RunInternal())
            lastRunTime = now;
    }

    private bool RunInternal()
    {
        ForceRunButtonInteractable();

        if (corePlaybackSource == null)
            CacheCorePlaybackSource();

        if (corePlaybackSource != null)
        {
            corePlaybackSource.Stop();
            PlayFeedback();
            return true;
        }

        return false;
    }

    private void PlayFeedback()
    {
        if (audioSource == null || clips == null || clips.Count == 0)
            return;

        AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Count)];
        if (clip == null)
            return;

        audioSource.pitch = UnityEngine.Random.Range(minPitch, maxPitch);
        audioSource.PlayOneShot(clip);
    }

    private void CacheCorePlaybackSource()
    {
        corePlaybackSource = null;

        if (core == null)
            return;

        var t = typeof(YappleCore);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var fi = t.GetField("playbackSource", flags);
        if (fi != null && typeof(AudioSource).IsAssignableFrom(fi.FieldType))
        {
            corePlaybackSource = fi.GetValue(core) as AudioSource;
            if (corePlaybackSource != null)
                return;
        }

        FieldInfo best = null;
        var fields = t.GetFields(flags);
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (!typeof(AudioSource).IsAssignableFrom(f.FieldType))
                continue;

            if (best == null)
                best = f;

            if (f.Name.IndexOf("playback", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                best = f;
                break;
            }
        }

        if (best != null)
            corePlaybackSource = best.GetValue(core) as AudioSource;
    }

    private void LoadSystemSettings()
    {
        if (triggerInput == null)
            return;

        string path = GetSystemSettingsPath();
        if (!File.Exists(path))
        {
            didLoadSettings = true;
            return;
        }

        string json;
        try { json = File.ReadAllText(path); }
        catch { didLoadSettings = true; return; }

        if (string.IsNullOrWhiteSpace(json))
        {
            didLoadSettings = true;
            return;
        }

        SystemSettings s;
        try { s = JsonUtility.FromJson<SystemSettings>(json); }
        catch { didLoadSettings = true; return; }

        if (s == null)
        {
            didLoadSettings = true;
            return;
        }

        string loaded = s.triggerWord ?? string.Empty;
        triggerInput.SetTextWithoutNotify(loaded);
        triggerInput.ForceLabelUpdate();
        lastKnownTriggerWord = loaded;

        didLoadSettings = true;
    }

    private void SaveSystemSettings()
    {
        if (!didLoadSettings && !isQuitting)
            return;

        string dir = GetLocalLowYappleDir();
        try { Directory.CreateDirectory(dir); }
        catch { return; }

        string value = triggerInput != null ? (triggerInput.text ?? string.Empty) : (lastKnownTriggerWord ?? string.Empty);
        lastKnownTriggerWord = value;

        var s = new SystemSettings
        {
            triggerWord = lastKnownTriggerWord
        };

        string json;
        try { json = JsonUtility.ToJson(s, true); }
        catch { return; }

        try { File.WriteAllText(GetSystemSettingsPath(), json); }
        catch { }
    }

    private static string GetSystemSettingsPath()
    {
        return Path.Combine(GetLocalLowYappleDir(), SettingsFileName);
    }

    private static string GetLocalLowYappleDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Path.Combine(Application.persistentDataPath, "Yapple");

        return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "Yapple"));
    }

    private static string NormalizeToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string s = input.Trim().ToLowerInvariant();
        if (s.Length == 0)
            return s;

        char[] buffer = new char[s.Length];
        int n = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c))
                buffer[n++] = c;
            else
                buffer[n++] = ' ';
        }

        int start = 0;
        while (start < n && buffer[start] == ' ')
            start++;

        int end = n - 1;
        while (end >= start && buffer[end] == ' ')
            end--;

        if (end < start)
            return string.Empty;

        return new string(buffer, start, end - start + 1);
    }
}
