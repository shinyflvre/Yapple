using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleVoiceChanger : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Dropdown inputDeviceDropdown;
    [SerializeField] private Slider inputVolumeSlider;

    [Header("Audio")]
    [SerializeField] private AudioSource micMonitorSource;

    [Header("Monitor")]
    [SerializeField] private bool monitorEnabled = true;
    [SerializeField, Range(0f, 1f)] private float monitorVolume = 1f;
    [SerializeField, Range(50f, 500f)] private float monitorLatencyMs = 180f;
    [SerializeField, Range(5f, 250f)] private float resyncThresholdMs = 60f;

    [Header("Meter")]
    [SerializeField, Range(256, 8192)] private int meterSampleWindow = 1024;
    [SerializeField, Range(0.1f, 10f)] private float meterGain = 1f;
    [SerializeField, Range(0.02f, 0.25f)] private float meterUpdateInterval = 0.06f;

    [Header("Config")]
    [SerializeField] private bool autoStart = true;
    [SerializeField, Range(2, 30)] private int micClipLengthSeconds = 10;

    private string currentDevice;
    private AudioClip micClip;
    private float[] meterBuffer;
    private float meterValue01;
    private float nextMeterTime;

    private void Awake()
    {
        Application.runInBackground = true;
        AudioListener.pause = false;

        if (micMonitorSource == null)
            micMonitorSource = GetComponent<AudioSource>();

        if (micMonitorSource != null)
        {
            micMonitorSource.playOnAwake = false;
            micMonitorSource.loop = true;
            micMonitorSource.spatialBlend = 0f;
            micMonitorSource.volume = monitorVolume;
        }

        if (inputVolumeSlider != null)
        {
            inputVolumeSlider.minValue = 0f;
            inputVolumeSlider.maxValue = 100f;
            inputVolumeSlider.value = 0f;
            inputVolumeSlider.interactable = false;
        }

        if (inputDeviceDropdown != null)
            inputDeviceDropdown.onValueChanged.AddListener(OnInputDeviceChanged);
    }

    private void Start()
    {
        RefreshInputDevices();

        if (autoStart)
            StartSelectedDevice();
    }

    private void Update()
    {
        UpdateMeter();
        UpdateMonitorResync();

        if (inputVolumeSlider != null)
            inputVolumeSlider.value = Mathf.Clamp01(meterValue01) * 100f;

        if (micMonitorSource != null)
            micMonitorSource.volume = monitorEnabled ? monitorVolume : 0f;
    }

    private void OnDisable()
    {
        StopMic();
    }

    public void RefreshInputDevices()
    {
        if (inputDeviceDropdown == null)
            return;

        inputDeviceDropdown.ClearOptions();

        string[] devices = Microphone.devices ?? Array.Empty<string>();
        var options = new List<string>(Mathf.Max(1, devices.Length));

        if (devices.Length == 0)
        {
            options.Add("No Microphone");
            inputDeviceDropdown.AddOptions(options);
            inputDeviceDropdown.value = 0;
            currentDevice = null;
            return;
        }

        for (int i = 0; i < devices.Length; i++)
            options.Add(devices[i]);

        inputDeviceDropdown.AddOptions(options);
        inputDeviceDropdown.value = 0;
        currentDevice = devices[0];
    }

    private void StartSelectedDevice()
    {
        string[] devices = Microphone.devices ?? Array.Empty<string>();
        if (devices.Length == 0)
            return;

        int idx = 0;

        if (inputDeviceDropdown != null)
            idx = Mathf.Clamp(inputDeviceDropdown.value, 0, devices.Length - 1);

        currentDevice = devices[idx];
        StartMic(currentDevice);
    }

    private void OnInputDeviceChanged(int index)
    {
        string[] devices = Microphone.devices ?? Array.Empty<string>();
        if (devices.Length == 0)
            return;

        index = Mathf.Clamp(index, 0, devices.Length - 1);
        currentDevice = devices[index];
        StartMic(currentDevice);
    }

    private void StartMic(string device)
    {
        StopMic();

        if (string.IsNullOrWhiteSpace(device))
            return;

        int hz = PickSampleRate(device);

        micClip = Microphone.Start(device, true, Mathf.Clamp(micClipLengthSeconds, 2, 30), hz);

        if (micClip == null)
            return;

        meterBuffer = null;
        meterValue01 = 0f;
        nextMeterTime = 0f;

        if (micMonitorSource != null && monitorEnabled)
            StartCoroutine(BeginMonitorWhenReady(device));
    }

    private IEnumerator BeginMonitorWhenReady(string device)
    {
        float timeout = Time.realtimeSinceStartup + 2f;
        while (Microphone.GetPosition(device) <= 0 && Time.realtimeSinceStartup < timeout)
            yield return null;

        if (micClip == null || micMonitorSource == null)
            yield break;

        micMonitorSource.clip = micClip;
        micMonitorSource.loop = true;

        int micPos = Microphone.GetPosition(device);
        int latencySamples = MsToSamples(monitorLatencyMs, micClip.frequency);
        int startPos = WrapSamples(micPos - latencySamples, micClip.samples);

        micMonitorSource.timeSamples = startPos;
        micMonitorSource.Play();
    }

    private void UpdateMonitorResync()
    {
        if (!monitorEnabled)
            return;

        if (micClip == null || micMonitorSource == null)
            return;

        if (string.IsNullOrWhiteSpace(currentDevice))
            return;

        if (!Microphone.IsRecording(currentDevice))
            return;

        if (!micMonitorSource.isPlaying)
            return;

        int micPos = Microphone.GetPosition(currentDevice);
        if (micPos <= 0)
            return;

        int latencySamples = MsToSamples(monitorLatencyMs, micClip.frequency);
        int expected = WrapSamples(micPos - latencySamples, micClip.samples);

        int current = micMonitorSource.timeSamples;
        int diff = CircularDistance(current, expected, micClip.samples);

        int threshold = MsToSamples(resyncThresholdMs, micClip.frequency);
        if (diff > threshold)
            micMonitorSource.timeSamples = expected;
    }

    private void UpdateMeter()
    {
        if (micClip == null)
        {
            meterValue01 = 0f;
            return;
        }

        float now = Time.unscaledTime;
        if (now < nextMeterTime)
            return;

        nextMeterTime = now + meterUpdateInterval;

        if (string.IsNullOrWhiteSpace(currentDevice))
        {
            meterValue01 = 0f;
            return;
        }

        if (!Microphone.IsRecording(currentDevice))
        {
            meterValue01 = 0f;
            return;
        }

        int micPos = Microphone.GetPosition(currentDevice);
        if (micPos <= 0)
        {
            meterValue01 = 0f;
            return;
        }

        int channels = Mathf.Max(1, micClip.channels);
        int frames = Mathf.Clamp(meterSampleWindow, 256, 8192);
        int samplesNeeded = frames * channels;

        if (meterBuffer == null || meterBuffer.Length != samplesNeeded)
            meterBuffer = new float[samplesNeeded];

        int startFrame = micPos - frames;
        if (startFrame < 0)
            startFrame += micClip.samples;

        bool ok = micClip.GetData(meterBuffer, startFrame);
        if (!ok)
        {
            meterValue01 = 0f;
            return;
        }

        double sumSq = 0.0;
        int n = meterBuffer.Length;

        for (int i = 0; i < n; i++)
        {
            float s = meterBuffer[i] * meterGain;
            sumSq += (double)s * (double)s;
        }

        float rms = 0f;
        if (n > 0)
            rms = (float)Math.Sqrt(sumSq / n);

        float v = Mathf.Clamp01(rms * 2.5f);

        meterValue01 = Mathf.Lerp(meterValue01, v, 0.45f);
    }

    private void StopMic()
    {
        if (micMonitorSource != null)
        {
            micMonitorSource.Stop();
            micMonitorSource.clip = null;
        }

        if (!string.IsNullOrWhiteSpace(currentDevice) && Microphone.IsRecording(currentDevice))
            Microphone.End(currentDevice);

        micClip = null;
        meterBuffer = null;
        meterValue01 = 0f;
    }

    private static int PickSampleRate(string device)
    {
        int outHz = AudioSettings.outputSampleRate;
        if (outHz <= 0) outHz = 48000;

        try
        {
            Microphone.GetDeviceCaps(device, out int minHz, out int maxHz);
            if (minHz == 0 && maxHz == 0)
                return outHz;

            if (maxHz > 0)
            {
                int lo = minHz > 0 ? minHz : 1;
                return Mathf.Clamp(outHz, lo, maxHz);
            }

            return Mathf.Max(1, minHz);
        }
        catch
        {
            return outHz;
        }
    }

    private static int MsToSamples(float ms, int hz)
    {
        float s = ms * 0.001f;
        int v = Mathf.RoundToInt(s * hz);
        return Mathf.Max(0, v);
    }

    private static int WrapSamples(int pos, int length)
    {
        if (length <= 0) return 0;
        int r = pos % length;
        if (r < 0) r += length;
        return r;
    }

    private static int CircularDistance(int a, int b, int length)
    {
        int d = Mathf.Abs(a - b);
        if (length <= 0) return d;
        int alt = length - d;
        return Mathf.Min(d, alt);
    }
}
