/*
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using SFB;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Vosk;

#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class YappleHandler : MonoBehaviour
{
    public enum SpeechMode
    {
        Keyword,
        Dictation
    }

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

    [Header("Input Meter")]
    [SerializeField, Range(256, 8192)] private int meterSampleWindow = 1024;
    [SerializeField, Range(0.1f, 10f)] private float meterGain = 1.0f;
    [SerializeField, Range(0.02f, 0.25f)] private float meterUpdateInterval = 0.06f;

    [Header("Speech")]
    [SerializeField] private SpeechMode speechMode = SpeechMode.Keyword;
    [SerializeField, Range(0.05f, 1.5f)] private float rebuildDelay = 0.45f;
    [SerializeField, Range(0f, 3f)] private float wordCooldownSeconds = 0.55f;
    [SerializeField] private bool autoStartListening = true;

    [Header("Vosk")]
    [SerializeField] private string voskModelFolder = "vosk-model-small-en-us-0.15";
    [SerializeField, Range(8000, 48000)] private int requestedMicHz = 16000;
    [SerializeField, Range(128, 8192)] private int targetChunkFrames = 128;
    [SerializeField, Range(1, 60)] private int micRingSeconds = 10;
    [SerializeField, Range(1, 64)] private int maxChunksPerUpdate = 12;

    [Header("Vosk Performance")]
    [SerializeField] private bool requestWordDetails = false;
    [SerializeField, Range(0.05f, 0.5f)] private float partialSendInterval = 0.12f;

    [Header("Keyword Filtering")]
    [SerializeField] private bool useGrammarInKeywordMode = true;
    [SerializeField] private bool includeUnknownToken = true;
    [SerializeField] private bool triggerOnlyBestMatch = true;
    [SerializeField, Range(0f, 1f)] private float minWordConfidence = 0.85f;
    [SerializeField, Range(0f, 2f)] private float globalCooldownSeconds = 0.18f;

    [Header("Partial")]
    [SerializeField] private bool enablePartial = true;
    [SerializeField, Range(1, 6)] private int partialStabilityFrames = 2;

    [Header("Debug")]
    [SerializeField] private bool logStatusToConsole = false;
    [SerializeField] private bool logFinalText = false;
    [SerializeField] private bool logRejected = false;

    private readonly List<YappleSoundItem> items = new List<YappleSoundItem>();
    private readonly Dictionary<string, YappleSoundItem> wordMap = new Dictionary<string, YappleSoundItem>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastPlayedByWord = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private string selectedMicDevice;
    private AudioClip micClip;

    private float[] meterBuffer;
    private float meterNextUpdate;

    private Coroutine rebuildCoroutine;

    private readonly Queue<AudioClip> playQueue = new Queue<AudioClip>();
    private readonly object playLock = new object();

    private bool isBrowsing;

    private Model voskModel;

    private int micReadPosFrames;
    private int micChannels;
    private int micFrequency;
    private int chunkFrames;
    private float chunkSeconds;

    private float[] interleaved;

    private float lastAnyTriggerTime;

    private readonly Queue<string> heardQueue = new Queue<string>();
    private readonly object heardLock = new object();

    private string lastPartialWord;
    private int partialStableCount;

    private static readonly char[] SplitSpaces = new[] { ' ' };
    private const string UnknownToken = "[unk]";

    private struct PcmChunk
    {
        public byte[] Data;
        public int Length;
    }

    private struct VoskPacket
    {
        public bool Finalized;
        public string Json;
    }

    private struct WorkerCommand
    {
        public int Type;
        public int Frequency;
        public string Grammar;
        public bool EnablePartial;
        public bool RequestWordDetails;
        public int PartialStrideChunks;
    }

    private readonly ConcurrentQueue<PcmChunk> pcmQueue = new ConcurrentQueue<PcmChunk>();
    private readonly ConcurrentQueue<VoskPacket> voskOutQueue = new ConcurrentQueue<VoskPacket>();
    private readonly ConcurrentQueue<WorkerCommand> cmdQueue = new ConcurrentQueue<WorkerCommand>();

    private readonly object pcmPoolLock = new object();
    private readonly Stack<byte[]> pcmPool = new Stack<byte[]>();
    private int pcmBytesPerChunk;
    private volatile int pcmQueuedChunks;

    private Thread workerThread;
    private AutoResetEvent workerEvent;
    private volatile bool workerRunning;
    private volatile bool workerRecognizerReady;

    private void Awake()
    {
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

        RefreshInputDevices();
        RefreshOutputDevices();

        if (autoStartListening)
        {
            StartMic();
            InitVoskModel();
            StartWorkerIfNeeded();
            RequestRebuild();
        }

        SetStatus(BuildStatusLine());
    }

    private void OnDisable()
    {
        StopListening();
        StopWorker();
        DisposeVoskModel();
        StopMic();
    }

    private void Update()
    {
        UpdateMicMeterThrottled();
        CaptureMicToWorker();
        DrainVoskPackets();
        DrainPlayQueue();
        DrainHeardQueue();
    }

    private void SetStatus(string msg)
    {
        if (statusText != null)
            statusText.text = msg;

        if (logStatusToConsole)
            Debug.Log(msg);
    }

    private void LogMaybe(string msg)
    {
        if (logFinalText || logRejected)
            Debug.Log(msg);
    }

    private string BuildStatusLine()
    {
        int words = wordMap.Count;
        string listen = IsListening() ? "Listening" : "Stopped";
        string model = voskModel != null ? "Model OK" : "Model Missing";
        string mic = micClip != null ? (micFrequency.ToString(CultureInfo.InvariantCulture) + "Hz " + micChannels.ToString(CultureInfo.InvariantCulture) + "ch") : "Mic Off";
        string ck = chunkFrames > 0 ? ("Chunk " + chunkFrames.ToString(CultureInfo.InvariantCulture)) : "Chunk ?";
        string g = (speechMode == SpeechMode.Keyword && useGrammarInKeywordMode) ? "Grammar ON" : "Grammar OFF";
        string p = enablePartial ? ("Partial ON x" + partialStabilityFrames.ToString(CultureInfo.InvariantCulture)) : "Partial OFF";
        string wd = requestWordDetails ? "Words ON" : "Words OFF";
        return "Mode: " + speechMode.ToString() + " | " + listen + " | Active Words: " + words + " | " + model + " | " + mic + " | " + ck + " | " + g + " | " + p + " | " + wd;
    }

    private bool IsListening()
    {
        return autoStartListening &&
               micClip != null &&
               !string.IsNullOrWhiteSpace(selectedMicDevice) &&
               Microphone.IsRecording(selectedMicDevice) &&
               workerRecognizerReady;
    }

    public void RefreshInputDevices()
    {
        if (inputDeviceDropdown == null)
            return;

        inputDeviceDropdown.ClearOptions();

        var devices = Microphone.devices ?? Array.Empty<string>();
        var options = new List<string>(Mathf.Max(1, devices.Length));

        if (devices.Length == 0)
        {
            options.Add("No Microphone");
            selectedMicDevice = null;
            inputDeviceDropdown.AddOptions(options);
            inputDeviceDropdown.value = 0;
            return;
        }

        for (int i = 0; i < devices.Length; i++)
            options.Add(devices[i]);

        inputDeviceDropdown.AddOptions(options);
        selectedMicDevice = devices[0];
        inputDeviceDropdown.value = 0;
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
        var devices = Microphone.devices ?? Array.Empty<string>();
        if (devices.Length == 0)
            return;

        index = Mathf.Clamp(index, 0, devices.Length - 1);
        selectedMicDevice = devices[index];

        StopMic();
        StartMic();

        micReadPosFrames = 0;
        lastPartialWord = null;
        partialStableCount = 0;

        RequestRebuild();
        SetStatus(BuildStatusLine() + " | Mic: " + selectedMicDevice);
    }

    private void OnOutputDeviceChanged(int index)
    {
        SetStatus(BuildStatusLine() + " | Output-Device Umschalten braucht ein Audio-Routing Plugin.");
    }

    private void StartMic()
    {
        if (string.IsNullOrWhiteSpace(selectedMicDevice))
            return;

        micClip = Microphone.Start(selectedMicDevice, true, Mathf.Clamp(micRingSeconds, 1, 60), Mathf.Clamp(requestedMicHz, 8000, 48000));
        micReadPosFrames = 0;

        if (micClip == null)
            return;

        micChannels = Mathf.Max(1, micClip.channels);
        micFrequency = Mathf.Max(8000, micClip.frequency);

        chunkFrames = Mathf.Clamp(targetChunkFrames, 128, 8192);
        chunkSeconds = chunkFrames / (float)micFrequency;

        int interleavedLen = chunkFrames * micChannels;
        if (interleaved == null || interleaved.Length != interleavedLen)
            interleaved = new float[interleavedLen];

        pcmBytesPerChunk = chunkFrames * 2;
        lock (pcmPoolLock)
        {
            pcmPool.Clear();
        }

        meterBuffer = null;
        meterNextUpdate = 0f;
    }

    private void StopMic()
    {
        if (!string.IsNullOrWhiteSpace(selectedMicDevice) && Microphone.IsRecording(selectedMicDevice))
            Microphone.End(selectedMicDevice);

        micClip = null;
    }

    private void UpdateMicMeterThrottled()
    {
        if (inputVolumeSlider == null)
            return;

        if (micClip == null || string.IsNullOrWhiteSpace(selectedMicDevice) || !Microphone.IsRecording(selectedMicDevice))
        {
            inputVolumeSlider.value = Mathf.MoveTowards(inputVolumeSlider.value, 0f, Time.unscaledDeltaTime * 200f);
            return;
        }

        float now = Time.unscaledTime;
        if (now < meterNextUpdate)
            return;

        meterNextUpdate = now + Mathf.Max(0.02f, meterUpdateInterval);

        int pos = Microphone.GetPosition(selectedMicDevice);
        if (pos <= 0)
            return;

        int start = pos - meterSampleWindow;
        if (start < 0)
            start += micClip.samples;

        int ch = Mathf.Max(1, micClip.channels);
        int need = meterSampleWindow * ch;
        if (meterBuffer == null || meterBuffer.Length != need)
            meterBuffer = new float[need];

        micClip.GetData(meterBuffer, start);

        double sum = 0.0;
        int frames = meterBuffer.Length / ch;

        for (int f = 0; f < frames; f++)
        {
            double m = 0.0;
            int baseIdx = f * ch;
            for (int c = 0; c < ch; c++)
                m += meterBuffer[baseIdx + c];
            m /= ch;
            sum += m * m;
        }

        float rms = Mathf.Sqrt((float)(sum / Mathf.Max(1, frames)));
        float value = Mathf.Clamp01(rms * meterGain);
        inputVolumeSlider.value = value * 100f;
    }

    private void CaptureMicToWorker()
    {
        if (!autoStartListening)
            return;

        if (voskModel == null)
            return;

        if (micClip == null || string.IsNullOrWhiteSpace(selectedMicDevice))
            return;

        if (!Microphone.IsRecording(selectedMicDevice))
        {
            StartMic();
            return;
        }

        if (chunkFrames <= 0 || interleaved == null || interleaved.Length == 0)
            return;

        int posFrames = Microphone.GetPosition(selectedMicDevice);
        if (posFrames < 0)
            return;

        int availableFrames = posFrames - micReadPosFrames;
        if (availableFrames < 0)
            availableFrames += micClip.samples;

        int chunksAvailable = availableFrames / chunkFrames;
        if (chunksAvailable <= 0)
            return;

        // int maxBufferedChunks = Mathf.Clamp((int)(1.0f / Mathf.Max(0.01f, chunkSeconds)), 6, 160);
        float maxBufferSeconds = 0.25f;
        int maxBufferedChunks = Mathf.Clamp((int)(maxBufferSeconds / Mathf.Max(0.01f, chunkSeconds)), 2, 64);

        int queued = pcmQueuedChunks;

        if (queued > maxBufferedChunks)
        {
            int drop = queued - maxBufferedChunks;
            int dropFrames = drop * chunkFrames;
            micReadPosFrames += dropFrames;
            while (micReadPosFrames >= micClip.samples)
                micReadPosFrames -= micClip.samples;
            return;
        }

        int processed = 0;
        int cap = Mathf.Max(1, maxChunksPerUpdate);

        while (availableFrames >= chunkFrames && processed < cap)
        {
            if (pcmQueuedChunks > maxBufferedChunks)
                break;

            micClip.GetData(interleaved, micReadPosFrames);

            byte[] pcm = RentPcmBuffer();
            InterleavedToPcm16LE(interleaved, pcm, micChannels, chunkFrames);

            pcmQueue.Enqueue(new PcmChunk { Data = pcm, Length = pcmBytesPerChunk });
            Interlocked.Increment(ref pcmQueuedChunks);
            SignalWorker();

            micReadPosFrames += chunkFrames;
            if (micReadPosFrames >= micClip.samples)
                micReadPosFrames = 0;

            availableFrames -= chunkFrames;
            processed++;
        }
    }

    private void DrainVoskPackets()
    {
        while (voskOutQueue.TryDequeue(out var pkt))
        {
            if (pkt.Json == null)
                continue;

            if (pkt.Finalized)
                HandleFinalJson(pkt.Json);
            else
                HandlePartialJson(pkt.Json);
        }
    }

    private void HandlePartialJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        string p = ExtractJsonString(json, "partial");
        if (string.IsNullOrWhiteSpace(p))
            return;

        string picked = PickKeywordFromTextOnly_NoAlloc(p);
        if (string.IsNullOrWhiteSpace(picked))
        {
            lastPartialWord = null;
            partialStableCount = 0;
            return;
        }

        if (!string.Equals(lastPartialWord, picked, StringComparison.OrdinalIgnoreCase))
        {
            lastPartialWord = picked;
            partialStableCount = 1;
            return;
        }

        partialStableCount++;

        if (partialStableCount < Mathf.Max(1, partialStabilityFrames))
            return;

        if (TryTriggerWord(picked, 0.99f, true))
        {
            lastAnyTriggerTime = Time.unscaledTime;
            RequestWorkerReset();
        }

        lastPartialWord = null;
        partialStableCount = 0;
    }

    private void HandleFinalJson(string json)
    {
        float now = Time.unscaledTime;

        if (now - lastAnyTriggerTime < globalCooldownSeconds)
            return;

        if (string.IsNullOrWhiteSpace(json))
            return;

        if (logFinalText)
            LogMaybe("FinalRaw: " + json);

        bool any;

        if (speechMode == SpeechMode.Keyword)
            any = HandleKeywordFinal(json);
        else
            any = HandleDictationFinal(json);

        if (any)
            lastAnyTriggerTime = now;
    }

    private bool HandleKeywordFinal(string json)
    {
        string bestWord = null;
        float bestConf = 0f;

        bool foundAny = false;

        if (requestWordDetails)
            foundAny = TryPickBestKeywordFromWordEntries(json, ref bestWord, ref bestConf);

        if (!foundAny)
        {
            string text = ExtractJsonString(json, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (logFinalText)
                    LogMaybe("FinalText: " + text);

                if (triggerOnlyBestMatch)
                {
                    string picked = PickKeywordFromTextOnly_NoAlloc(text);
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        bestWord = picked;
                        bestConf = 0.99f;
                        foundAny = true;
                    }
                }
                else
                {
                    bool any = false;
                    ForEachTokenNormalized(text, token =>
                    {
                        if (token.Length == 0)
                            return;
                        if (string.Equals(token, UnknownToken, StringComparison.OrdinalIgnoreCase))
                            return;
                        if (wordMap.ContainsKey(token))
                        {
                            if (TryTriggerWord(token, 0.99f, false))
                                any = true;
                        }
                    });
                    return any;
                }
            }
        }

        if (!foundAny)
        {
            if (logRejected)
                LogMaybe("Rejected: no keyword");
            return false;
        }

        if (string.Equals(bestWord, UnknownToken, StringComparison.OrdinalIgnoreCase))
        {
            if (logRejected)
                LogMaybe("Rejected: [unk]");
            return false;
        }

        return TryTriggerWord(bestWord, bestConf, false);
    }

    private bool HandleDictationFinal(string json)
    {
        string text = ExtractJsonString(json, "text");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        bool any = false;

        ForEachTokenNormalized(text, token =>
        {
            if (token.Length == 0)
                return;
            if (wordMap.ContainsKey(token))
            {
                if (TryTriggerWord(token, 0.99f, false))
                    any = true;
            }
        });

        return any;
    }

    private bool TryPickBestKeywordFromWordEntries(string json, ref string bestWord, ref float bestConf)
    {
        int r = json.IndexOf("\"result\"", StringComparison.OrdinalIgnoreCase);
        if (r < 0)
            return false;

        int arrStart = json.IndexOf('[', r);
        if (arrStart < 0)
            return false;

        int arrEnd = json.IndexOf(']', arrStart);
        if (arrEnd < 0)
            return false;

        int i = arrStart;
        bool any = false;

        while (i < arrEnd)
        {
            int objStart = json.IndexOf('{', i);
            if (objStart < 0 || objStart > arrEnd)
                break;

            int objEnd = json.IndexOf('}', objStart);
            if (objEnd < 0 || objEnd > arrEnd)
                break;

            string obj = json.Substring(objStart, objEnd - objStart + 1);

            string wRaw = ExtractJsonString(obj, "word");
            float conf = ExtractJsonFloat(obj, "conf", 0f);

            if (!string.IsNullOrWhiteSpace(wRaw))
            {
                string w = NormalizeToken(wRaw);
                if (w.Length != 0 && wordMap.ContainsKey(w))
                {
                    if (conf >= minWordConfidence)
                    {
                        if (!triggerOnlyBestMatch)
                        {
                            if (TryTriggerWord(w, conf, false))
                                any = true;
                        }
                        else
                        {
                            if (conf > bestConf)
                            {
                                bestConf = conf;
                                bestWord = w;
                                any = true;
                            }
                        }
                    }
                    else
                    {
                        if (logRejected)
                            LogMaybe("Rejected conf: " + w + " conf=" + conf.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                }
            }

            i = objEnd + 1;
        }

        return any;
    }

    private string PickKeywordFromTextOnly_NoAlloc(string text)
    {
        string found = null;
        ForEachTokenNormalized(text, token =>
        {
            if (found != null)
                return;

            if (token.Length == 0)
                return;

            if (string.Equals(token, UnknownToken, StringComparison.OrdinalIgnoreCase))
                return;

            if (wordMap.ContainsKey(token))
                found = token;
        });
        return found;
    }

    private bool TryTriggerWord(string word, float conf, bool partial)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        if (!wordMap.TryGetValue(word, out var item))
            return false;

        if (item == null || item.Clip == null)
            return false;

        float now = Time.unscaledTime;

        if (lastPlayedByWord.TryGetValue(word, out float last))
        {
            if (now - last < wordCooldownSeconds)
                return false;
        }

        lastPlayedByWord[word] = now;

        lock (playLock)
            playQueue.Enqueue(item.Clip);

        EnqueueHeard("Keyword: " + word + (partial ? " (partial)" : " (final)") + " conf=" + conf.ToString("0.00", CultureInfo.InvariantCulture));
        return true;
    }

    private void InitVoskModel()
    {
        if (voskModel != null)
            return;

        string modelPath = Path.Combine(Application.streamingAssetsPath, voskModelFolder);
        if (!Directory.Exists(modelPath))
        {
            SetStatus(BuildStatusLine() + " | Vosk model folder missing: " + modelPath);
            return;
        }

        Vosk.Vosk.SetLogLevel(0);
        voskModel = new Model(modelPath);
    }

    private void DisposeVoskModel()
    {
        if (voskModel != null)
        {
            voskModel.Dispose();
            voskModel = null;
        }
    }

    public void StartListening()
    {
        autoStartListening = true;

        if (micClip == null)
            StartMic();

        InitVoskModel();
        StartWorkerIfNeeded();
        RequestRebuild();
    }

    public void StopListening()
    {
        autoStartListening = false;
        RequestWorkerReset();
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

        playbackSource.PlayOneShot(item.Clip);
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

    private void RequestRebuild()
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
        RequestWorkerRebuild();
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

            w = NormalizeToken(w.Trim());
            if (w.Length == 0) continue;
            if (w.IndexOf(' ') >= 0) continue;

            if (wordMap.ContainsKey(w)) continue;
            wordMap[w] = it;
        }
    }

    private void DrainPlayQueue()
    {
        if (playbackSource == null)
            return;

        AudioClip clip = null;

        lock (playLock)
        {
            if (playQueue.Count > 0)
                clip = playQueue.Dequeue();
        }

        if (clip != null)
            playbackSource.PlayOneShot(clip);
    }

    private void EnqueueHeard(string heard)
    {
        lock (heardLock)
            heardQueue.Enqueue(heard);
    }

    private void DrainHeardQueue()
    {
        string last = null;

        lock (heardLock)
        {
            while (heardQueue.Count > 0)
                last = heardQueue.Dequeue();
        }

        if (last != null)
            SetStatus(BuildStatusLine() + " | Heard: " + last);
    }

    private static void InterleavedToPcm16LE(float[] srcInterleaved, byte[] dst, int channels, int frames)
    {
        int si = 0;
        int di = 0;

        if (channels <= 1)
        {
            for (int f = 0; f < frames; f++)
            {
                float x = srcInterleaved[si++];
                if (x > 1f) x = 1f;
                if (x < -1f) x = -1f;
                short s = (short)Mathf.RoundToInt(x * 32767f);
                dst[di++] = (byte)(s & 255);
                dst[di++] = (byte)((s >> 8) & 255);
            }
            return;
        }

        float inv = 1f / channels;

        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += srcInterleaved[si++];

            float x = sum * inv;
            if (x > 1f) x = 1f;
            if (x < -1f) x = -1f;
            short s = (short)Mathf.RoundToInt(x * 32767f);
            dst[di++] = (byte)(s & 255);
            dst[di++] = (byte)((s >> 8) & 255);
        }
    }

    private static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            return null;

        string needle = "\"" + key + "\"";
        int k = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (k < 0)
            return null;

        int colon = json.IndexOf(':', k + needle.Length);
        if (colon < 0)
            return null;

        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0)
            return null;

        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0)
            return null;

        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static float ExtractJsonFloat(string json, string key, float fallback)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            return fallback;

        string needle = "\"" + key + "\"";
        int k = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (k < 0)
            return fallback;

        int colon = json.IndexOf(':', k + needle.Length);
        if (colon < 0)
            return fallback;

        int i = colon + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
            i++;

        int j = i;
        while (j < json.Length)
        {
            char ch = json[j];
            if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '+' || ch == 'e' || ch == 'E')
            {
                j++;
                continue;
            }
            break;
        }

        if (j <= i)
            return fallback;

        string num = json.Substring(i, j - i);
        if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            return f;

        return fallback;
    }

    private static string BuildGrammar(IEnumerable<string> keywords, bool addUnknown)
    {
        List<string> list = new List<string>();

        foreach (var k in keywords)
        {
            if (string.IsNullOrWhiteSpace(k))
                continue;

            string w = NormalizeToken(k);
            if (w.Length == 0)
                continue;

            if (w.IndexOf(' ') >= 0)
                continue;

            bool exists = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], w, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                list.Add(w);
        }

        if (addUnknown)
            list.Add(UnknownToken);

        if (list.Count == 0)
            return null;

        list.Sort(StringComparer.Ordinal);

        string[] quoted = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
            quoted[i] = "\"" + EscapeJsonString(list[i]) + "\"";

        return "[" + string.Join(",", quoted) + "]";
    }

    private static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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

    private static void ForEachTokenNormalized(string input, Action<string> onToken)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        int len = input.Length;
        char[] token = new char[len];
        int tn = 0;

        for (int i = 0; i <= len; i++)
        {
            char c = i == len ? ' ' : input[i];
            bool ok = char.IsLetterOrDigit(c);

            if (ok)
            {
                token[tn++] = char.ToLowerInvariant(c);
                continue;
            }

            if (tn > 0)
            {
                string t = new string(token, 0, tn);
                onToken(t);
                tn = 0;
            }
        }
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

    private void StartWorkerIfNeeded()
    {
        if (workerThread != null)
            return;

        workerEvent = new AutoResetEvent(false);
        workerRunning = true;
        workerRecognizerReady = false;

        workerThread = new Thread(WorkerLoop);
        workerThread.IsBackground = true;
        workerThread.Start();
    }

    private void StopWorker()
    {
        if (workerThread == null)
            return;

        cmdQueue.Enqueue(new WorkerCommand { Type = 2 });
        SignalWorker();

        workerRunning = false;

        try
        {
            workerThread.Join(500);
        }
        catch
        {
        }

        workerThread = null;

        if (workerEvent != null)
        {
            workerEvent.Dispose();
            workerEvent = null;
        }

        workerRecognizerReady = false;

        DrainAndReturnAllPcm();
    }

    private void SignalWorker()
    {
        if (workerEvent != null)
            workerEvent.Set();
    }

    private void RequestWorkerRebuild()
    {
        if (voskModel == null)
            InitVoskModel();

        if (voskModel == null)
            return;

        if (micClip == null)
            StartMic();

        if (micClip == null)
            return;

        string grammar = null;

        if (speechMode == SpeechMode.Keyword && useGrammarInKeywordMode)
            grammar = BuildGrammar(wordMap.Keys, includeUnknownToken);

        int stride = 1;
        if (chunkSeconds > 0.001f)
            stride = Mathf.Clamp((int)Mathf.RoundToInt(partialSendInterval / chunkSeconds), 1, 12);

        cmdQueue.Enqueue(new WorkerCommand
        {
            Type = 1,
            Frequency = micFrequency,
            Grammar = grammar,
            EnablePartial = enablePartial,
            RequestWordDetails = requestWordDetails,
            PartialStrideChunks = stride
        });

        SignalWorker();
    }

    private void RequestWorkerReset()
    {
        cmdQueue.Enqueue(new WorkerCommand { Type = 3 });
        SignalWorker();
    }

    private void WorkerLoop()
    {
        VoskRecognizer rec = null;
        int partialStride = 1;
        int partialCounter = 0;
        bool doPartial = false;

        while (workerRunning)
        {
            bool didSomething = false;

            while (cmdQueue.TryDequeue(out var cmd))
            {
                didSomething = true;

                if (cmd.Type == 2)
                {
                    workerRunning = false;
                    break;
                }

                if (cmd.Type == 3)
                {
                    if (rec != null)
                        rec.Reset();
                    continue;
                }

                if (cmd.Type == 1)
                {
                    workerRecognizerReady = false;

                    DrainAndReturnAllPcm();

                    if (rec != null)
                    {
                        rec.Dispose();
                        rec = null;
                    }

                    if (voskModel != null && cmd.Frequency > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(cmd.Grammar))
                            rec = new VoskRecognizer(voskModel, cmd.Frequency, cmd.Grammar);
                        else
                            rec = new VoskRecognizer(voskModel, cmd.Frequency);

                        rec.SetWords(cmd.RequestWordDetails);
                        rec.SetMaxAlternatives(0);

                        doPartial = cmd.EnablePartial;
                        partialStride = Mathf.Max(1, cmd.PartialStrideChunks);
                        partialCounter = 0;

                        workerRecognizerReady = true;
                    }

                    continue;
                }
            }

            if (!workerRunning)
                break;

            if (rec == null)
            {
                if (!didSomething)
                    workerEvent.WaitOne(10);
                continue;
            }

            int processed = 0;

            while (processed < 64 && pcmQueue.TryDequeue(out var chunk))
            {
                didSomething = true;
                Interlocked.Decrement(ref pcmQueuedChunks);

                bool finalized = rec.AcceptWaveform(chunk.Data, chunk.Length);

                if (doPartial && !finalized)
                {
                    partialCounter++;
                    if (partialCounter >= partialStride)
                    {
                        partialCounter = 0;
                        string pj = rec.PartialResult();
                        if (!string.IsNullOrWhiteSpace(pj))
                            voskOutQueue.Enqueue(new VoskPacket { Finalized = false, Json = pj });
                    }
                }

                if (finalized)
                {
                    string rj = rec.Result();
                    if (!string.IsNullOrWhiteSpace(rj))
                        voskOutQueue.Enqueue(new VoskPacket { Finalized = true, Json = rj });
                }

                ReturnPcmBuffer(chunk.Data);
                processed++;
            }

            if (!didSomething)
                workerEvent.WaitOne(5);
        }

        if (rec != null)
        {
            rec.Dispose();
            rec = null;
        }

        workerRecognizerReady = false;

        DrainAndReturnAllPcm();
    }

    private void DrainAndReturnAllPcm()
    {
        while (pcmQueue.TryDequeue(out var chunk))
        {
            Interlocked.Decrement(ref pcmQueuedChunks);
            ReturnPcmBuffer(chunk.Data);
        }
    }

    private byte[] RentPcmBuffer()
    {
        lock (pcmPoolLock)
        {
            if (pcmPool.Count > 0)
                return pcmPool.Pop();
        }
        return new byte[Mathf.Max(2, pcmBytesPerChunk)];
    }

    private void ReturnPcmBuffer(byte[] buf)
    {
        if (buf == null)
            return;

        if (pcmBytesPerChunk > 0 && buf.Length != pcmBytesPerChunk)
            return;

        lock (pcmPoolLock)
        {
            if (pcmPool.Count < 256)
                pcmPool.Push(buf);
        }
    }
}
*/