using UnityEngine;
using UnityEngine.UI;

public sealed class YappleVoiceChord : MonoBehaviour
{
    public enum ChordMode
    {
        Major,
        Minor
    }

    [Header("UI")]
    [SerializeField] Slider chordSlider;
    [SerializeField] float chordSliderMax = 100f;

    [SerializeField] Slider dryWetSlider;
    [SerializeField] float dryWetSliderMax = 100f;

    [Header("Chord")]
    [SerializeField] ChordMode chordMode = ChordMode.Major;
    [SerializeField, Range(-24, 24)] int rootSemitoneOffset = 0;

    [Header("Mix")]
    [SerializeField, Range(0f, 1f)] float dryAtZero = 1f;
    [SerializeField, Range(0f, 1f)] float dryAtHundred = 0.6f;
    [SerializeField, Range(0f, 2f)] float harmonyGainAtHundred = 1f;
    [SerializeField, Range(0.1f, 2f)] float outputGain = 1f;

    [Header("DSP")]
    [SerializeField, Range(2f, 40f)] float windowMs = 10f;
    [SerializeField, Range(0f, 25f)] float detuneCents = 6f;
    [SerializeField] bool softClipLimiter = true;

    volatile float amount01;
    volatile float dryWet01;
    volatile int windowSamplesVolatile;
    volatile float r0;
    volatile float r1;
    volatile float r2;
    volatile float r3;

    int sampleRate;
    float[][] ring;
    int ringChannels;
    int ringLen;
    int ringMask;
    int writeIndex;

    float p0;
    float p1;
    float p2;
    float p3;

    void Awake()
    {
        sampleRate = AudioSettings.outputSampleRate;
        RecomputeRatios();
        UpdateWindowSamples();
    }

    void Update()
    {
        float a = 0f;
        if (chordSlider != null)
        {
            float max = chordSliderMax <= 0.0001f ? 100f : chordSliderMax;
            a = Mathf.Clamp01(chordSlider.value / max);
        }
        amount01 = a;

        float w = 0f;
        if (dryWetSlider != null)
        {
            float max = dryWetSliderMax <= 0.0001f ? 100f : dryWetSliderMax;
            w = Mathf.Clamp01(dryWetSlider.value / max);
        }
        dryWet01 = w;

        UpdateWindowSamples();
        RecomputeRatios();
    }

    void UpdateWindowSamples()
    {
        int ws = Mathf.RoundToInt(sampleRate * (Mathf.Clamp(windowMs, 2f, 40f) * 0.001f));
        ws = Mathf.Clamp(ws, 64, 4096);
        windowSamplesVolatile = ws;
    }

    void RecomputeRatios()
    {
        int third = chordMode == ChordMode.Major ? 4 : 3;

        int i0 = rootSemitoneOffset + third;
        int i1 = rootSemitoneOffset + 7;
        int i2 = rootSemitoneOffset + 12;
        int i3 = rootSemitoneOffset + 12 + third;

        float d = detuneCents;

        r0 = SemitoneRatio(i0) * CentsRatio(-d * 0.60f);
        r1 = SemitoneRatio(i1) * CentsRatio(+d * 0.80f);
        r2 = SemitoneRatio(i2) * CentsRatio(-d * 1.00f);
        r3 = SemitoneRatio(i3) * CentsRatio(+d * 1.20f);
    }

    static float SemitoneRatio(int semi)
    {
        return Mathf.Pow(2f, semi / 12f);
    }

    static float CentsRatio(float cents)
    {
        return Mathf.Pow(2f, cents / 1200f);
    }

    void EnsureRing(int channels, int windowSamples)
    {
        int desired = NextPow2(windowSamples * 8);
        desired = Mathf.Max(desired, 2048);

        if (ring != null && ringChannels == channels && ringLen == desired) return;

        ringChannels = channels;
        ringLen = desired;
        ringMask = ringLen - 1;
        writeIndex = 0;

        ring = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            ring[c] = new float[ringLen];
        }

        p0 = 0f;
        p1 = 0.25f;
        p2 = 0.5f;
        p3 = 0.75f;
    }

    static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        int ws = windowSamplesVolatile;
        EnsureRing(channels, ws);

        float a = amount01;
        float w = dryWet01;

        float baseDryMix = Mathf.Lerp(dryAtZero, dryAtHundred, a) * outputGain;
        float dryMix = baseDryMix * (1f - w);

        float harmonyTotal = a * harmonyGainAtHundred * outputGain;
        float harmonyEach = harmonyTotal * 0.25f;

        float rr0 = r0;
        float rr1 = r1;
        float rr2 = r2;
        float rr3 = r3;

        float s0 = (1f - rr0) / ws;
        float s1 = (1f - rr1) / ws;
        float s2 = (1f - rr2) / ws;
        float s3 = (1f - rr3) / ws;

        int frames = data.Length / channels;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * channels;

            for (int c = 0; c < channels; c++)
            {
                ring[c][writeIndex] = data[baseIdx + c];
            }

            if (a > 0.0001f)
            {
                p0 = Wrap01(p0 + s0);
                p1 = Wrap01(p1 + s1);
                p2 = Wrap01(p2 + s2);
                p3 = Wrap01(p3 + s3);
            }

            for (int c = 0; c < channels; c++)
            {
                float dry = data[baseIdx + c];
                float outSample = dry * dryMix;

                if (a > 0.0001f)
                {
                    float h =
                        VoiceSample(ring[c], writeIndex, ringMask, ws, p0) +
                        VoiceSample(ring[c], writeIndex, ringMask, ws, p1) +
                        VoiceSample(ring[c], writeIndex, ringMask, ws, p2) +
                        VoiceSample(ring[c], writeIndex, ringMask, ws, p3);

                    outSample += h * harmonyEach;
                }

                if (softClipLimiter)
                {
                    outSample = SoftClip(outSample);
                }

                data[baseIdx + c] = outSample;
            }

            writeIndex = (writeIndex + 1) & ringMask;
        }
    }

    static float VoiceSample(float[] buf, int writeIdx, int mask, int ws, float phaseA)
    {
        float phaseB = phaseA + 0.5f;
        if (phaseB >= 1f) phaseB -= 1f;

        float wA = Tri(phaseA);
        float wB = Tri(phaseB);

        float dA = phaseA * ws;
        float dB = phaseB * ws;

        float sA = ReadDelay(buf, writeIdx, mask, dA);
        float sB = ReadDelay(buf, writeIdx, mask, dB);

        return sA * wA + sB * wB;
    }

    static float Tri(float phase)
    {
        float x = 2f * phase - 1f;
        float a = Mathf.Abs(x);
        float v = 1f - a;
        return v < 0f ? 0f : v;
    }

    static float ReadDelay(float[] buf, int writeIdx, int mask, float delay)
    {
        float rp = writeIdx - delay;
        int i0 = (int)rp;
        float frac = rp - i0;

        int idx0 = i0 & mask;
        int idx1 = (idx0 + 1) & mask;

        float a = buf[idx0];
        float b = buf[idx1];
        return a + (b - a) * frac;
    }

    static float Wrap01(float v)
    {
        if (v >= 1f) v -= 1f;
        if (v < 0f) v += 1f;
        return v;
    }

    static float SoftClip(float x)
    {
        float ax = Mathf.Abs(x);
        if (ax <= 1f) return x * (1f - 0.33333334f * x * x);
        float s = Mathf.Sign(x);
        return s;
    }
}
