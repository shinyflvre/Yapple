using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleCommands : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button addCommandButton;
    [SerializeField] private GameObject commandPrefab;
    [SerializeField] private Transform content;

    [Header("Refs")]
    [SerializeField] private YappleCore core;
    [SerializeField] private YappleVosk vosk;

    [Header("Behavior")]
    [SerializeField, Range(0f, 5f)] private float perWordCooldownSeconds = 2f;
    [SerializeField] private bool triggerOnPartial = false;
    [SerializeField] private bool allowMultiplePerWordRandom = true;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private List<AudioClip> clips = new List<AudioClip>();
    [SerializeField] private float minPitch = 0.8f;
    [SerializeField] private float maxPitch = 1.2f;

    // Keine Execution Safety mehr – Users können beliebige Commands eingeben und ausführen
    private readonly List<YappleCommandItem> items = new List<YappleCommandItem>();
    private readonly Dictionary<string, List<string>> wordToCommands = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastRunByWord = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private void Awake()
    {
        if (core == null)
            core = GetComponent<YappleCore>();

        if (vosk == null)
            vosk = GetComponent<YappleVosk>();
    }

    private void OnEnable()
    {
        if (addCommandButton != null)
            addCommandButton.onClick.AddListener(AddCommandClicked);

        if (vosk != null)
            vosk.OnKeyword += OnKeyword;

        if (core != null)
            core.CollectKeywords += OnCollectKeywords;
    }

    private void OnDisable()
    {
        if (addCommandButton != null)
            addCommandButton.onClick.RemoveListener(AddCommandClicked);

        if (vosk != null)
            vosk.OnKeyword -= OnKeyword;

        if (core != null)
            core.CollectKeywords -= OnCollectKeywords;
    }

    private void AddCommandClicked()
    {
        if (commandPrefab == null || content == null)
            return;

        GameObject go = Instantiate(commandPrefab);
        go.transform.SetParent(content, false);

        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.localScale = Vector3.one;
            rt.anchoredPosition3D = Vector3.zero;
        }

        var item = go.GetComponent<YappleCommandItem>();
        if (item == null)
        {
            Destroy(go);
            return;
        }

        item.OnDeleteRequested = HandleDeleteRequested;
        item.OnRunRequested = HandleRunRequested;
        item.OnChanged = HandleChanged;

        items.Add(item);
        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }

    private void HandleDeleteRequested(YappleCommandItem item)
    {
        if (item == null)
            return;

        items.Remove(item);
        Destroy(item.gameObject);

        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }

    private void HandleRunRequested(YappleCommandItem item)
    {
        if (item == null)
            return;

        string cmd = item.Command;
        if (string.IsNullOrWhiteSpace(cmd))
            return;

        Execute(cmd);
    }

    private void HandleChanged(YappleCommandItem item)
    {
        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }
    private void RebuildMap()
    {
        wordToCommands.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null)
                continue;

            string w = NormalizeToken(it.Word);
            if (string.IsNullOrWhiteSpace(w))
                continue;

            string cmd = (it.Command ?? string.Empty).Trim();
            if (cmd.Length == 0)
                continue;

            if (!wordToCommands.TryGetValue(w, out var list))
            {
                list = new List<string>(2);
                wordToCommands[w] = list;
            }

            list.Add(cmd);
        }
    }
    private void OnCollectKeywords(HashSet<string> set)
    {
        if (set == null)
            return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null)
                continue;

            string w = NormalizeToken(it.Word);
            if (string.IsNullOrWhiteSpace(w))
                continue;

            set.Add(w);
        }
    }
    private void OnKeyword(string word, float conf, bool partial)
    {
        if (!triggerOnPartial && partial)
            return;

        if (string.IsNullOrWhiteSpace(word))
            return;

        float now = Time.unscaledTime;

        if (lastRunByWord.TryGetValue(word, out float last))
        {
            if (now - last < perWordCooldownSeconds)
                return;
        }

        if (!wordToCommands.TryGetValue(word, out var cmds))
            return;

        if (cmds == null || cmds.Count == 0)
            return;

        string cmd = cmds[0];

        if (allowMultiplePerWordRandom && cmds.Count > 1)
            cmd = cmds[UnityEngine.Random.Range(0, cmds.Count)];

        if (Execute(cmd))
            lastRunByWord[word] = now;
    }

    private bool Execute(string command)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (string.IsNullOrWhiteSpace(command))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,          // Wichtig!
                CreateNoWindow = true,            // Kein Fenster
                WindowStyle = ProcessWindowStyle.Hidden,  // Extra Sicherheit
                RedirectStandardOutput = true,    // Optional: verhindert Konsolenausgabe
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                // Optional: warte kurz, falls nötig
                // process.WaitForExit();
            }

            // Sound abspielen
            if (audioSource != null && clips != null && clips.Count > 0)
            {
                AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Count)];
                if (clip != null)
                {
                    audioSource.pitch = UnityEngine.Random.Range(minPitch, maxPitch);
                    audioSource.PlayOneShot(clip);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("Command failed: " + ex.Message);
            return false;
        }
#else
    return false;
#endif
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