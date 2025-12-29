using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SFB;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public sealed class YappleCore : MonoBehaviour
{
    [Header("Main UI")]
    [SerializeField] private TMP_Dropdown inputDeviceDropdown;
    [SerializeField] private TMP_Dropdown outputDeviceDropdown;
    [SerializeField] private Slider inputVolumeSlider;
    [SerializeField] private Button addNewSoundButton;
    [SerializeField] private TMP_Text statusText;

    [Header("List")]
    [SerializeField] private Transform scrollContent;
    [SerializeField] private GameObject itemPrefab;

    [Header("Audio")]
    [SerializeField] private AudioSource playbackSource;

    [Header("Speech")]
    [SerializeField] private YappleVosk.SpeechMode speechMode = YappleVosk.SpeechMode.Keyword;
    [SerializeField, Range(0.05f, 1.5f)] private float rebuildDelay = 0.45f;
    [SerializeField, Range(0f, 3f)] private float wordCooldownSeconds = 0.55f;
    [SerializeField, Range(0f, 2f)] private float globalCooldownSeconds = 0.18f;
    [SerializeField] private bool autoStartListening = true;

    [Header("Input Meter")]
    [SerializeField, Range(256, 8192)] private int meterSampleWindow = 1024;
    [SerializeField, Range(0.1f, 10f)] private float meterGain = 1.0f;
    [SerializeField, Range(0.02f, 0.25f)] private float meterUpdateInterval = 0.06f;

    [Header("Provider")]
    [SerializeField] private YappleVosk vosk;

    private readonly List<YappleSoundItem> items = new List<YappleSoundItem>();
    private readonly Dictionary<string, List<YappleSoundItem>> wordMap =
        new Dictionary<string, List<YappleSoundItem>>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, float> lastPlayedByWord = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private Coroutine rebuildCoroutine;
    private struct PlayRequest
    {
        public AudioClip clip;
        public float volume01;

        public PlayRequest(AudioClip clip, float volume01)
        {
            this.clip = clip;
            this.volume01 = volume01;
        }
    }

    private readonly Queue<PlayRequest> playQueue = new Queue<PlayRequest>();
    private readonly object playLock = new object();


    private bool isBrowsing;

    private float lastAnyTriggerTime;

    private string lastHeard;
    private float nextStatusUpdate;

    public event Action<HashSet<string>> CollectKeywords;

    private void Awake()
    {
        if (vosk == null)
            vosk = GetComponent<YappleVosk>();

        if (playbackSource == null)
            playbackSource = GetComponent<AudioSource>();

        if (playbackSource != null)
            playbackSource.ignoreListenerPause = true;

        if (inputVolumeSlider != null)
        {
            inputVolumeSlider.minValue = 0f;
            inputVolumeSlider.maxValue = 100f;
            inputVolumeSlider.value = 0f;
            inputVolumeSlider.interactable = false;
        }

        if (addNewSoundButton != null)
            addNewSoundButton.onClick.AddListener(AddNewSoundClicked);

        if (inputDeviceDropdown != null)
            inputDeviceDropdown.onValueChanged.AddListener(OnInputDeviceChanged);

        if (outputDeviceDropdown != null)
            outputDeviceDropdown.onValueChanged.AddListener(OnOutputDeviceChanged);

        Application.runInBackground = true;
        AudioListener.pause = false;
    }

    private void Start()
    {
        if (itemPrefab != null && itemPrefab.scene.IsValid())
            SetStatus("Item Prefab ist eine Scene-Instanz. Zieh das Prefab-Asset aus dem Project-Fenster.");

        if (vosk != null)
        {
            vosk.OnKeyword += HandleKeywordFromProvider;
            vosk.OnFinalText += HandleFinalTextFromProvider;
            vosk.SetSpeechMode(speechMode);
            vosk.SetMeterConfig(meterSampleWindow, meterGain, meterUpdateInterval);
        }

        RefreshInputDevices();
        RefreshOutputDevices();

        if (autoStartListening && vosk != null)
        {
            vosk.StartListening();
            RequestRebuild();
        }

        SetStatus(BuildStatusLine());
    }

    private void OnDisable()
    {
        if (vosk != null)
        {
            vosk.OnKeyword -= HandleKeywordFromProvider;
            vosk.OnFinalText -= HandleFinalTextFromProvider;
            vosk.StopListening();
        }
    }

    private void Update()
    {
        UpdateMeterUI();
        DrainPlayQueue();
        UpdateStatusThrottled();
    }

    private void UpdateMeterUI()
    {
        if (inputVolumeSlider == null || vosk == null)
            return;

        float v01 = vosk.GetMeterLevel01();
        inputVolumeSlider.value = Mathf.Clamp01(v01) * 100f;
    }

    private void UpdateStatusThrottled()
    {
        float now = Time.unscaledTime;
        if (now < nextStatusUpdate)
            return;

        nextStatusUpdate = now + 0.05f;

        if (!string.IsNullOrWhiteSpace(lastHeard))
            SetStatus(BuildStatusLine() + " | Heard: " + lastHeard);
        else
            SetStatus(BuildStatusLine());
    }

    private void SetStatus(string msg)
    {
        if (statusText != null)
            statusText.text = msg;
    }

    private string BuildStatusLine()
    {
        int words = wordMap.Count;
        string listen = (vosk != null && vosk.IsListening) ? "Listening" : "Stopped";
        string model = (vosk != null && vosk.ModelOk) ? "Model OK" : "Model Missing";
        string mic = (vosk != null && !string.IsNullOrWhiteSpace(vosk.SelectedMicDevice) && vosk.MicHz > 0)
            ? (vosk.MicHz.ToString(CultureInfo.InvariantCulture) + "Hz " + vosk.MicChannels.ToString(CultureInfo.InvariantCulture) + "ch")
            : "Mic Off";
        string ck = (vosk != null && vosk.ChunkFrames > 0) ? ("Chunk " + vosk.ChunkFrames.ToString(CultureInfo.InvariantCulture)) : "Chunk ?";
        string g = (vosk != null && vosk.GrammarOn) ? "Grammar ON" : "Grammar OFF";
        return "Mode: " + speechMode.ToString() + " | " + listen + " | Active Words: " + words + " | " + model + " | " + mic + " | " + ck + " | " + g;
    }

    public void RefreshInputDevices()
    {
        if (inputDeviceDropdown == null)
            return;

        inputDeviceDropdown.ClearOptions();

        string[] devices = (vosk != null) ? vosk.GetInputDevices() : (Microphone.devices ?? Array.Empty<string>());
        var options = new List<string>(Mathf.Max(1, devices.Length));

        if (devices.Length == 0)
        {
            options.Add("No Microphone");
            inputDeviceDropdown.AddOptions(options);
            inputDeviceDropdown.value = 0;
            if (vosk != null)
                vosk.SetMicDevice(null);
            return;
        }

        for (int i = 0; i < devices.Length; i++)
            options.Add(devices[i]);

        inputDeviceDropdown.AddOptions(options);
        inputDeviceDropdown.value = 0;

        if (vosk != null)
            vosk.SetMicDevice(devices[0]);
    }

    public void RefreshOutputDevices()
    {
        if (outputDeviceDropdown == null)
            return;

        outputDeviceDropdown.ClearOptions();
        outputDeviceDropdown.AddOptions(new List<string> { "Default Output" });
        outputDeviceDropdown.value = 0;
    }

    private void OnInputDeviceChanged(int index)
    {
        if (vosk == null)
            return;

        string[] devices = vosk.GetInputDevices();
        if (devices == null || devices.Length == 0)
            return;

        index = Mathf.Clamp(index, 0, devices.Length - 1);
        vosk.SetMicDevice(devices[index]);

        if (autoStartListening)
            vosk.StartListening();

        RequestRebuild();
        lastHeard = null;
        SetStatus(BuildStatusLine());
    }

    private void OnOutputDeviceChanged(int index)
    {
        SetStatus(BuildStatusLine() + " | Output-Device Umschalten braucht ein Audio-Routing Plugin.");
    }

    private void HandleKeywordFromProvider(string word, float conf, bool partial)
    {
        float now = Time.unscaledTime;

        if (now - lastAnyTriggerTime < globalCooldownSeconds)
            return;

        if (TryTriggerWord(word, conf, partial))
        {
            lastAnyTriggerTime = now;
            if (partial && vosk != null)
                vosk.ResetRecognizer();
        }
    }

    private void HandleFinalTextFromProvider(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
    }
    private bool TryTriggerWord(string word, float conf, bool partial)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        if (!wordMap.TryGetValue(word, out var list))
            return false;

        if (list == null || list.Count == 0)
            return false;

        float now = Time.unscaledTime;

        if (lastPlayedByWord.TryGetValue(word, out float last))
        {
            if (now - last < wordCooldownSeconds)
                return false;
        }

        lastPlayedByWord[word] = now;

        YappleSoundItem item = (list.Count == 1) ? list[0] : list[UnityEngine.Random.Range(0, list.Count)];
        if (item == null || item.Clip == null)
            return false;

        lock (playLock)
            playQueue.Enqueue(new PlayRequest(item.Clip, item.Volume01));

        lastHeard = "Keyword: " + word + (partial ? " (partial)" : " (final)") + " conf=" + conf.ToString("0.00", CultureInfo.InvariantCulture);
        return true;
    }

    private void DrainPlayQueue()
    {
        if (playbackSource == null)
            return;

        PlayRequest req = default;
        bool has = false;

        lock (playLock)
        {
            if (playQueue.Count > 0)
            {
                req = playQueue.Dequeue();
                has = true;
            }
        }

        if (has && req.clip != null)
            playbackSource.PlayOneShot(req.clip, req.volume01);
    }
    private void AddNewSoundClicked()
    {
        if (isBrowsing)
            return;

        if (itemPrefab == null)
        {
            SetStatus(BuildStatusLine() + " | Item Prefab fehlt.");
            return;
        }

        if (scrollContent == null)
        {
            SetStatus(BuildStatusLine() + " | Scroll Content fehlt.");
            return;
        }

        isBrowsing = true;
        string path = OpenAudioFileDialog();
        isBrowsing = false;

        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus(BuildStatusLine() + " | No file selected.");
            return;
        }

        var item = CreateItemRow(path);
        if (item == null)
            return;

        StartCoroutine(LoadAudioIntoItem(path, item));
    }

    private YappleSoundItem CreateItemRow(string filePath)
    {
        var go = Instantiate(itemPrefab);
        go.transform.SetParent(scrollContent, false);

        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.localScale = Vector3.one;
            rt.anchoredPosition3D = Vector3.zero;
        }

        var item = go.GetComponent<YappleSoundItem>();
        if (item == null)
        {
            Destroy(go);
            SetStatus(BuildStatusLine() + " | Prefab hat keinen YappleSoundItem Component.");
            return null;
        }

        item.SetFile(filePath);
        item.OnPlayRequested = HandlePlayRequested;
        item.OnDeleteRequested = HandleDeleteRequested;
        item.OnWordChanged = HandleWordChanged;

        items.Add(item);
        RequestRebuild();

        return item;
    }

    private IEnumerator LoadAudioIntoItem(string path, YappleSoundItem item)
    {
        if (!File.Exists(path))
        {
            item.SetLoadFailed("File not found");
            yield break;
        }

        item.SetLoading();

        string uri = new Uri(path).AbsoluteUri;

        var types = GetAudioTypesToTry(path);
        for (int i = 0; i < types.Length; i++)
        {
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, types[i]))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (i == types.Length - 1)
                        item.SetLoadFailed(req.error ?? "Request failed");
                    continue;
                }

                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    if (i == types.Length - 1)
                        item.SetLoadFailed("Decode failed");
                    continue;
                }

                clip.name = Path.GetFileNameWithoutExtension(path);
                clip.LoadAudioData();

                float t = 0f;
                while (clip.loadState == AudioDataLoadState.Loading && t < 5f)
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }

                if (clip.loadState != AudioDataLoadState.Loaded)
                {
                    if (i == types.Length - 1)
                        item.SetLoadFailed("Audio not loaded");
                    continue;
                }

                item.SetClip(clip);
                RequestRebuild();
                SetStatus(BuildStatusLine());

                yield break;
            }
        }
    }

    private void HandlePlayRequested(YappleSoundItem item)
    {
        if (item == null || playbackSource == null)
            return;

        if (item.Clip == null)
            return;

        playbackSource.PlayOneShot(item.Clip, item.Volume01);
    }

    private void HandleDeleteRequested(YappleSoundItem item)
    {
        if (item == null)
            return;

        items.Remove(item);
        Destroy(item.gameObject);

        RequestRebuild();
        SetStatus(BuildStatusLine());
    }

    private void HandleWordChanged(YappleSoundItem item)
    {
        RequestRebuild();
    }

    public void RequestRebuild()
    {
        if (rebuildCoroutine != null)
            StopCoroutine(rebuildCoroutine);

        rebuildCoroutine = StartCoroutine(RebuildLater());
    }

    private IEnumerator RebuildLater()
    {
        yield return new WaitForSecondsRealtime(rebuildDelay);
        rebuildCoroutine = null;

        RebuildWordMap();

        if (vosk != null)
        {
            vosk.SetSpeechMode(speechMode);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in wordMap.Keys)
                set.Add(k);

            CollectKeywords?.Invoke(set);

            vosk.SetKeywords(set);
        }

        SetStatus(BuildStatusLine());
    }
    private void RebuildWordMap()
    {
        wordMap.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            if (it.Clip == null) continue;

            string w = it.Word;
            if (string.IsNullOrWhiteSpace(w)) continue;

            w = NormalizeToken(w);
            if (w.Length == 0) continue;

            if (!wordMap.TryGetValue(w, out var list) || list == null)
            {
                list = new List<YappleSoundItem>();
                wordMap[w] = list;
            }

            list.Add(it);
        }
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

        int w = 0;
        bool lastWasSpace = true;

        for (int i = start; i <= end; i++)
        {
            char c = buffer[i];
            if (c == ' ')
            {
                if (lastWasSpace)
                    continue;

                buffer[w++] = ' ';
                lastWasSpace = true;
            }
            else
            {
                buffer[w++] = c;
                lastWasSpace = false;
            }
        }

        if (w > 0 && buffer[w - 1] == ' ')
            w--;

        if (w <= 0)
            return string.Empty;

        return new string(buffer, 0, w);
    }

    private static AudioType[] GetAudioTypesToTry(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".wav") return new[] { AudioType.WAV, AudioType.UNKNOWN };
        if (ext == ".ogg") return new[] { AudioType.OGGVORBIS, AudioType.UNKNOWN };
        if (ext == ".mp3") return new[] { AudioType.MPEG, AudioType.UNKNOWN };

        return new[] { AudioType.UNKNOWN };
    }

    private string OpenAudioFileDialog()
    {
        var extensions = new[] { new ExtensionFilter("Audio Files", "mp3", "ogg", "wav") };
        string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Audio File", "", extensions, false);
        if (paths == null || paths.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(paths[0]))
            return null;

        return paths[0];
    }
}