using UnityEngine;
using UnityEngine.UI;

public sealed class YappleNoiseGate : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] Toggle enableToggle;

    [Header("Gate")]
    [SerializeField, Range(-90f, 0f)] float closeDb = -55f;
    [SerializeField, Range(-90f, 0f)] float openDb = -50f;
    [SerializeField, Range(0f, 500f)] float holdMs = 80f;
    [SerializeField, Range(0.1f, 50f)] float attackMs = 4f;
    [SerializeField, Range(5f, 800f)] float releaseMs = 160f;

    [Header("Meter")]
    [SerializeField, Range(-90f, 0f)] float meterFloorDb = -70f;

    [Header("Output")]
    [SerializeField, Range(0.1f, 2f)] float outputGain = 1f;
    [SerializeField] bool softClipLimiter = true;

    [SerializeField, HideInInspector] float meterDb;
    [SerializeField, HideInInspector] float gateGainDebug;

    volatile float enabledVolatile;

    int sampleRate = 48000;

    float meterEnv;
    float gateGain;
    float holdSamplesLeft;

    void Awake()
    {
        sampleRate = AudioSettings.outputSampleRate;
        if (sampleRate <= 0) sampleRate = 48000;

        enabledVolatile = enableToggle != null && enableToggle.isOn ? 1f : 0f;
        meterEnv = 0f;
        gateGain = 1f;
        holdSamplesLeft = 0f;
    }

    void OnEnable()
    {
        if (enableToggle != null) enableToggle.onValueChanged.AddListener(OnToggleChanged);
        enabledVolatile = enableToggle != null && enableToggle.isOn ? 1f : 0f;
    }

    void OnDisable()
    {
        if (enableToggle != null) enableToggle.onValueChanged.RemoveListener(OnToggleChanged);
    }

    void OnToggleChanged(bool on)
    {
        enabledVolatile = on ? 1f : 0f;
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (enabledVolatile <= 0.5f) return;
        if (channels <= 0) return;

        float cDb = Mathf.Min(closeDb, openDb);
        float oDb = Mathf.Max(openDb, closeDb);

        float holdSamp = Mathf.Clamp(holdMs, 0f, 500f) * 0.001f * sampleRate;

        float a = CoeffMs(Mathf.Max(attackMs, 0.1f), sampleRate);
        float r = CoeffMs(Mathf.Max(releaseMs, 5f), sampleRate);

        float meterAttack = 1f - Mathf.Exp(-1f / (sampleRate * 0.010f));
        float meterRelease = 1f - Mathf.Exp(-1f / (sampleRate * 0.200f));

        int frames = data.Length / channels;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * channels;

            float peak = 0f;
            for (int c = 0; c < channels; c++)
            {
                float av = Abs(data[baseIdx + c]);
                if (av > peak) peak = av;
            }

            if (peak > meterEnv) meterEnv += (peak - meterEnv) * meterAttack;
            else meterEnv += (peak - meterEnv) * meterRelease;

            float mDb = LinToDb(meterEnv);
            if (!IsFinite(mDb)) mDb = meterFloorDb;
            meterDb = Mathf.Max(meterFloorDb, Mathf.Min(0f, mDb));

            float target = 1f;

            if (meterDb >= oDb)
            {
                holdSamplesLeft = holdSamp;
                target = 1f;
            }
            else if (meterDb <= cDb)
            {
                if (holdSamplesLeft > 0f)
                {
                    holdSamplesLeft -= 1f;
                    target = 1f;
                }
                else
                {
                    target = 0f;
                }
            }
            else
            {
                target = gateGain > 0.5f ? 1f : 0f;
            }

            if (target < gateGain) gateGain += (target - gateGain) * a;
            else gateGain += (target - gateGain) * r;

            gateGainDebug = gateGain;

            float g = gateGain * outputGain;

            for (int c = 0; c < channels; c++)
            {
                float y = data[baseIdx + c] * g;

                if (!IsFinite(y)) y = 0f;
                if (softClipLimiter) y = SoftClip(y);

                data[baseIdx + c] = y;
            }
        }
    }

    static float CoeffMs(float ms, int sr)
    {
        float t = ms * 0.001f;
        if (t <= 0f) return 1f;
        return 1f - Mathf.Exp(-1f / (sr * t));
    }

    static float LinToDb(float lin)
    {
        return 20f * Mathf.Log10(Mathf.Max(lin, 1e-9f));
    }

    static float Abs(float x)
    {
        return x >= 0f ? x : -x;
    }

    static bool IsFinite(float x)
    {
        return !(float.IsNaN(x) || float.IsInfinity(x));
    }

    static float SoftClip(float x)
    {
        float ax = x >= 0f ? x : -x;
        if (ax <= 1f) return x * (1f - 0.33333334f * x * x);
        return x >= 0f ? 1f : -1f;
    }
}
