using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using Vosk;

public sealed class YappleVosk : MonoBehaviour
{
    public enum SpeechMode
    {
        Keyword,
        Dictation
    }

    public event Action<string, float, bool> OnKeyword;
    public event Action<string> OnFinalText;

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

    [Header("Partial")]
    [SerializeField] private bool enablePartial = true;
    [SerializeField, Range(1, 6)] private int partialStabilityFrames = 2;

    [Header("Debug")]
    [SerializeField] private bool logStatusToConsole = false;
    [SerializeField] private bool logFinalText = false;
    [SerializeField] private bool logRejected = false;

    private SpeechMode speechMode = SpeechMode.Keyword;

    private string selectedMicDevice;
    private AudioClip micClip;

    private Model voskModel;

    private int micReadPosFrames;
    private int micChannels;
    private int micFrequency;
    private int chunkFrames;
    private float chunkSeconds;

    private float[] interleaved;

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

    private bool listeningRequested;

    private readonly HashSet<string> activeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private string lastPartialWord;
    private int partialStableCount;

    private int meterSampleWindow = 1024;
    private float meterGain = 1.0f;
    private float meterUpdateInterval = 0.06f;
    private float[] meterBuffer;
    private float meterNextUpdate;
    private float meterValue01;

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

    public bool IsListening => listeningRequested && micClip != null && !string.IsNullOrWhiteSpace(selectedMicDevice) && Microphone.IsRecording(selectedMicDevice) && workerRecognizerReady;
    public bool ModelOk => voskModel != null;
    public string SelectedMicDevice => selectedMicDevice;
    public int MicHz => micFrequency;
    public int MicChannels => micChannels;
    public int ChunkFrames => chunkFrames;
    public bool GrammarOn => speechMode == SpeechMode.Keyword && useGrammarInKeywordMode;

    private void OnDisable()
    {
        StopListening();
        StopWorker();
        DisposeVoskModel();
        StopMicInternal();
    }

    private void Update()
    {
        UpdateMicMeterThrottled();
        CaptureMicToWorker();
        DrainVoskPackets();
    }

    public void SetSpeechMode(SpeechMode mode)
    {
        speechMode = mode;
        if (listeningRequested)
            RequestWorkerRebuild();
    }

    public void SetMeterConfig(int sampleWindow, float gain, float updateInterval)
    {
        meterSampleWindow = Mathf.Clamp(sampleWindow, 256, 8192);
        meterGain = Mathf.Clamp(gain, 0.1f, 10f);
        meterUpdateInterval = Mathf.Clamp(updateInterval, 0.02f, 0.25f);
        meterBuffer = null;
        meterNextUpdate = 0f;
    }

    public float GetMeterLevel01()
    {
        return meterValue01;
    }

    public string[] GetInputDevices()
    {
        return Microphone.devices ?? Array.Empty<string>();
    }

    public void SetMicDevice(string deviceName)
    {
        selectedMicDevice = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;

        lastPartialWord = null;
        partialStableCount = 0;

        StopMicInternal();
        if (listeningRequested)
        {
            StartMicInternal();
            RequestWorkerRebuild();
        }
    }
    public void SetKeywords(IEnumerable<string> keywords)
    {
        activeKeywords.Clear();
        if (keywords != null)
        {
            foreach (var k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k))
                    continue;

                string w = NormalizeToken(k);
                if (w.Length == 0)
                    continue;

                if (!string.Equals(w, UnknownToken, StringComparison.OrdinalIgnoreCase))
                    activeKeywords.Add(w);
            }
        }

        if (listeningRequested)
            RequestWorkerRebuild();
    }
    public void StartListening()
    {
        listeningRequested = true;

        if (string.IsNullOrWhiteSpace(selectedMicDevice))
        {
            var devices = GetInputDevices();
            if (devices.Length > 0)
                selectedMicDevice = devices[0];
        }

        EnsureModel();
        StartWorkerIfNeeded();
        StartMicInternal();
        RequestWorkerRebuild();
    }

    public void StopListening()
    {
        listeningRequested = false;
        RequestWorkerReset();
    }

    public void ResetRecognizer()
    {
        RequestWorkerReset();
    }

    private void EnsureModel()
    {
        if (voskModel != null)
            return;

        string modelPath = Path.Combine(Application.streamingAssetsPath, voskModelFolder);
        if (!Directory.Exists(modelPath))
        {
            if (logStatusToConsole)
                Debug.Log("Vosk model folder missing: " + modelPath);
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

    private void StartMicInternal()
    {
        if (!listeningRequested)
            return;

        if (string.IsNullOrWhiteSpace(selectedMicDevice))
            return;

        if (micClip != null && Microphone.IsRecording(selectedMicDevice))
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
            pcmPool.Clear();

        meterBuffer = null;
        meterNextUpdate = 0f;
        meterValue01 = 0f;
    }

    private void StopMicInternal()
    {
        if (!string.IsNullOrWhiteSpace(selectedMicDevice) && Microphone.IsRecording(selectedMicDevice))
            Microphone.End(selectedMicDevice);

        micClip = null;
        meterValue01 = 0f;
    }

    private void UpdateMicMeterThrottled()
    {
        if (micClip == null || string.IsNullOrWhiteSpace(selectedMicDevice) || !Microphone.IsRecording(selectedMicDevice))
        {
            meterValue01 = Mathf.MoveTowards(meterValue01, 0f, Time.unscaledDeltaTime * 10f);
            return;
        }

        float now = Time.unscaledTime;
        if (now < meterNextUpdate)
            return;

        meterNextUpdate = now + meterUpdateInterval;

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
        meterValue01 = Mathf.Clamp01(rms * meterGain);
    }

    private void CaptureMicToWorker()
    {
        if (!listeningRequested)
            return;

        if (voskModel == null)
            EnsureModel();

        if (voskModel == null)
            return;

        if (micClip == null || string.IsNullOrWhiteSpace(selectedMicDevice))
            return;

        if (!Microphone.IsRecording(selectedMicDevice))
        {
            StartMicInternal();
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
        if (!enablePartial)
            return;

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

        EmitKeyword(picked, 0.99f, true);

        lastPartialWord = null;
        partialStableCount = 0;
    }

    private void HandleFinalJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        if (logFinalText)
            Debug.Log("FinalRaw: " + json);

        if (speechMode == SpeechMode.Keyword)
            HandleKeywordFinal(json);
        else
            HandleDictationFinal(json);
    }
    private void HandleKeywordFinal(string json)
    {
        string text = ExtractJsonString(json, "text");
        if (!string.IsNullOrWhiteSpace(text))
            OnFinalText?.Invoke(text);

        if (!string.IsNullOrWhiteSpace(text) && !triggerOnlyBestMatch)
        {
            bool any = EmitAllKeywordMatchesFromText_NoAlloc(text, false);
            if (!any && logRejected)
                Debug.Log("Rejected: no keyword");
            return;
        }

        string bestWord = null;
        float bestConf = 0f;
        bool foundAny = false;

        if (requestWordDetails)
            foundAny = TryPickBestKeywordFromWordEntries(json, ref bestWord, ref bestConf);

        if (!string.IsNullOrWhiteSpace(text))
        {
            string picked = PickKeywordFromTextOnly_NoAlloc(text);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                if (!foundAny)
                {
                    bestWord = picked;
                    bestConf = 0.99f;
                    foundAny = true;
                }
                else
                {
                    int pickedTokens = CountKeywordTokens(picked);
                    int bestTokens = CountKeywordTokens(bestWord);
                    if (pickedTokens > bestTokens || (pickedTokens == bestTokens && picked.Length > bestWord.Length))
                    {
                        bestWord = picked;
                        bestConf = 0.99f;
                        foundAny = true;
                    }
                }
            }
        }

        if (!foundAny)
        {
            if (logRejected)
                Debug.Log("Rejected: no keyword");
            return;
        }

        EmitKeyword(bestWord, bestConf, false);
    }
    private void HandleDictationFinal(string json)
    {
        string text = ExtractJsonString(json, "text");
        if (string.IsNullOrWhiteSpace(text))
            return;

        OnFinalText?.Invoke(text);

        if (triggerOnlyBestMatch)
        {
            string picked = PickKeywordFromTextOnly_NoAlloc(text);
            if (!string.IsNullOrWhiteSpace(picked))
                EmitKeyword(picked, 0.99f, false);
            else if (logRejected)
                Debug.Log("Rejected: no keyword");
            return;
        }

        bool any = EmitAllKeywordMatchesFromText_NoAlloc(text, false);
        if (!any && logRejected)
            Debug.Log("Rejected: no keyword");
    }

    private void EmitKeyword(string word, float conf, bool partial)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        string w = NormalizeToken(word);
        if (w.Length == 0)
            return;

        if (string.Equals(w, UnknownToken, StringComparison.OrdinalIgnoreCase))
            return;

        if (!activeKeywords.Contains(w))
            return;

        OnKeyword?.Invoke(w, conf, partial);
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
                if (w.Length != 0 && activeKeywords.Contains(w))
                {
                    if (conf >= minWordConfidence)
                    {
                        if (!triggerOnlyBestMatch)
                        {
                            EmitKeyword(w, conf, false);
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
                            Debug.Log("Rejected conf: " + w + " conf=" + conf.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                }
            }

            i = objEnd + 1;
        }

        return any;
    }
    private string PickKeywordFromTextOnly_NoAlloc(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (activeKeywords == null || activeKeywords.Count == 0)
            return null;

        string normalizedText = NormalizeToken(text);
        if (normalizedText.Length == 0)
            return null;

        string best = null;
        int bestTokens = -1;
        int bestLen = -1;

        foreach (var kw in activeKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw))
                continue;

            if (!ContainsWholeKeyword(normalizedText, kw))
                continue;

            int tokens = CountKeywordTokens(kw);
            int len = kw.Length;

            if (tokens > bestTokens || (tokens == bestTokens && len > bestLen))
            {
                best = kw;
                bestTokens = tokens;
                bestLen = len;
            }
        }

        return best;
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
        if (!listeningRequested)
            return;

        if (voskModel == null)
            EnsureModel();

        if (voskModel == null)
            return;

        if (micClip == null)
            StartMicInternal();

        if (micClip == null)
            return;

        string grammar = null;

        if (speechMode == SpeechMode.Keyword && useGrammarInKeywordMode)
            grammar = BuildGrammar(activeKeywords, includeUnknownToken);

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
    private static int CountKeywordTokens(string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
            return 0;

        int tokens = 1;
        for (int i = 0; i < keyword.Length; i++)
        {
            if (keyword[i] == ' ')
                tokens++;
        }
        return tokens;
    }

    private static bool ContainsWholeKeyword(string normalizedText, string keyword)
    {
        if (string.IsNullOrEmpty(normalizedText) || string.IsNullOrEmpty(keyword))
            return false;

        int start = 0;
        int klen = keyword.Length;

        while (true)
        {
            int idx = normalizedText.IndexOf(keyword, start, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            bool leftOk = idx == 0 || normalizedText[idx - 1] == ' ';
            int after = idx + klen;
            bool rightOk = after == normalizedText.Length || normalizedText[after] == ' ';

            if (leftOk && rightOk)
                return true;

            start = idx + 1;
            if (start >= normalizedText.Length)
                return false;
        }
    }

    private bool EmitAllKeywordMatchesFromText_NoAlloc(string text, bool partial)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (activeKeywords == null || activeKeywords.Count == 0)
            return false;

        string normalizedText = NormalizeToken(text);
        if (normalizedText.Length == 0)
            return false;

        bool any = false;

        foreach (var kw in activeKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw))
                continue;

            if (ContainsWholeKeyword(normalizedText, kw))
            {
                EmitKeyword(kw, 0.99f, partial);
                any = true;
            }
        }

        return any;
    }

}
