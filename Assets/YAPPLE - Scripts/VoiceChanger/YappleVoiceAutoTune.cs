using UnityEngine;
using UnityEngine.UI;

public sealed class YappleVoiceAutoTune : MonoBehaviour
{
    public enum ScaleMode
    {
        Chromatic,
        Major,
        Minor
    }

    public enum NoteRoot
    {
        C = 0,
        Cs = 1,
        D = 2,
        Ds = 3,
        E = 4,
        F = 5,
        Fs = 6,
        G = 7,
        Gs = 8,
        A = 9,
        As = 10,
        B = 11
    }

    [Header("UI")]
    [SerializeField] Slider amountSlider;
    [SerializeField] float amountSliderMax = 100f;

    [Header("Key")]
    [SerializeField] NoteRoot keyRoot = NoteRoot.C;
    [SerializeField] ScaleMode scaleMode = ScaleMode.Major;

    [Header("T-Pain Style")]
    [SerializeField] bool hardTune = true;
    [SerializeField, Range(0f, 1f)] float wetAtHundred = 1f;
    [SerializeField, Range(0.2f, 4f)] float wetCurve = 1.6f;
    [SerializeField, Range(2f, 120f)] float retuneMsAtZero = 90f;
    [SerializeField, Range(2f, 120f)] float retuneMsAtHundred = 6f;
    [SerializeField, Range(0f, 1f)] float humanize = 0f;
    [SerializeField] bool holdLastNoteWhenUnvoiced = true;

    [Header("Pitch Detect")]
    [SerializeField, Range(60f, 300f)] float minPitchHz = 80f;
    [SerializeField, Range(200f, 2000f)] float maxPitchHz = 900f;
    [SerializeField, Range(0.05f, 0.35f)] float yinThreshold = 0.18f;
    [SerializeField, Range(2f, 80f)] float analysisIntervalMs = 10f;
    [SerializeField, Range(12f, 80f)] float analysisWindowMs = 24f;

    [Header("Pitch Shift")]
    [SerializeField, Range(2f, 40f)] float windowMs = 8f;
    [SerializeField, Range(-25f, 25f)] float detuneCents = 0f;
    [SerializeField, Range(0.1f, 2f)] float outputGain = 1f;
    [SerializeField] bool softClipLimiter = true;

    static readonly int[] MajorDegrees = { 0, 2, 4, 5, 7, 9, 11 };
    static readonly int[] MinorDegrees = { 0, 2, 3, 5, 7, 8, 10 };

    volatile float amount01Volatile;
    volatile int windowSamplesVolatile;
    volatile int analysisSamplesVolatile;
    volatile int analysisHopSamplesVolatile;
    volatile int keyRootPcVolatile;
    volatile int scaleModeVolatile;

    volatile float wetAtHundredVolatile;
    volatile float wetCurveVolatile;
    volatile float retuneMsAtZeroVolatile;
    volatile float retuneMsAtHundredVolatile;
    volatile float humanizeVolatile;
    volatile float detuneCentsVolatile;
    volatile int hardTuneVolatile;
    volatile int holdLastVolatile;

    int sampleRate;

    float[][] ring;
    int ringChannels;
    int ringLen;
    int ringMask;
    int writeIndex;

    float phase;

    float[] analysisBuf;
    float[] yinDiff;
    float[] yinCmnd;

    int analysisCountdown;
    float ratioSmoothed = 1f;
    float ratioTarget = 1f;

    void Awake()
    {
        sampleRate = AudioSettings.outputSampleRate;
        UpdateAllVolatiles();
    }

    void Update()
    {
        UpdateAllVolatiles();
    }

    void UpdateAllVolatiles()
    {
        float a = 0f;
        if (amountSlider != null)
        {
            float max = amountSliderMax <= 0.0001f ? 100f : amountSliderMax;
            a = Mathf.Clamp01(amountSlider.value / max);
        }
        amount01Volatile = a;

        int ws = Mathf.RoundToInt(sampleRate * (Mathf.Clamp(windowMs, 2f, 40f) * 0.001f));
        ws = Mathf.Clamp(ws, 64, 4096);
        windowSamplesVolatile = ws;

        int an = Mathf.RoundToInt(sampleRate * (Mathf.Clamp(analysisWindowMs, 12f, 80f) * 0.001f));
        an = Mathf.Clamp(an, 512, 8192);
        analysisSamplesVolatile = an;

        int hop = Mathf.RoundToInt(sampleRate * (Mathf.Clamp(analysisIntervalMs, 2f, 80f) * 0.001f));
        hop = Mathf.Clamp(hop, 64, 16384);
        analysisHopSamplesVolatile = hop;

        keyRootPcVolatile = (int)keyRoot;
        scaleModeVolatile = (int)scaleMode;

        wetAtHundredVolatile = Mathf.Clamp01(wetAtHundred);
        wetCurveVolatile = Mathf.Clamp(wetCurve, 0.2f, 4f);
        retuneMsAtZeroVolatile = Mathf.Clamp(retuneMsAtZero, 2f, 120f);
        retuneMsAtHundredVolatile = Mathf.Clamp(retuneMsAtHundred, 2f, 120f);
        humanizeVolatile = Mathf.Clamp01(humanize);
        detuneCentsVolatile = Mathf.Clamp(detuneCents, -25f, 25f);
        hardTuneVolatile = hardTune ? 1 : 0;
        holdLastVolatile = holdLastNoteWhenUnvoiced ? 1 : 0;
    }

    void EnsureRing(int channels, int minLen)
    {
        int desired = NextPow2(minLen * 8);
        desired = Mathf.Max(desired, 4096);

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

        phase = 0f;

        analysisCountdown = 0;
        ratioSmoothed = 1f;
        ratioTarget = 1f;
    }

    void EnsureAnalysisBuffers(int n)
    {
        if (analysisBuf == null || analysisBuf.Length != n) analysisBuf = new float[n];
        if (yinDiff == null || yinDiff.Length < n) yinDiff = new float[n];
        if (yinCmnd == null || yinCmnd.Length < n) yinCmnd = new float[n];
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
        int an = analysisSamplesVolatile;

        EnsureRing(channels, ws > an ? ws : an);
        EnsureAnalysisBuffers(an);

        float amount = amount01Volatile;

        float wetMax = wetAtHundredVolatile;
        float wetPow = wetCurveVolatile;
        float wet = wetMax * Mathf.Pow(amount, wetPow);
        wet = Mathf.Clamp01(wet);
        if (amount >= 0.999f) wet = 1f;

        float rt0 = retuneMsAtZeroVolatile;
        float rt1 = retuneMsAtHundredVolatile;
        float retuneMs = Mathf.Lerp(rt0, rt1, amount);
        float tau = Mathf.Max(0.0005f, retuneMs * 0.001f);
        float alphaPerSample = 1f - Mathf.Exp(-1f / (sampleRate * tau));

        float minHz = minPitchHz;
        float maxHz = maxPitchHz;
        float threshold = yinThreshold;

        int keyPc = keyRootPcVolatile;
        int scale = scaleModeVolatile;

        int hopSamples = analysisHopSamplesVolatile;

        float human = humanizeVolatile;
        float detune = detuneCentsVolatile;
        bool hard = hardTuneVolatile != 0;
        bool holdLast = holdLastVolatile != 0;

        int frames = data.Length / channels;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * channels;

            for (int c = 0; c < channels; c++)
            {
                ring[c][writeIndex] = data[baseIdx + c];
            }

            analysisCountdown--;
            if (analysisCountdown <= 0)
            {
                analysisCountdown = hopSamples;

                float detectedHz = DetectPitchHzFromRing(an, minHz, maxHz, threshold);
                if (detectedHz > 1f)
                {
                    float qHz = QuantizeToScaleHz(detectedHz, keyPc, scale);
                    float targetHz = qHz;

                    if (!hard)
                    {
                        float midi = HzToMidi(detectedHz);
                        float qmidi = HzToMidi(qHz);
                        float mixedMidi = Mathf.Lerp(midi, qmidi, amount);
                        targetHz = MidiToHz(mixedMidi);
                    }

                    if (human > 0f)
                    {
                        float midi = HzToMidi(detectedHz);
                        float qmidi = HzToMidi(targetHz);
                        float mixedMidi = Mathf.Lerp(qmidi, midi, human);
                        targetHz = MidiToHz(mixedMidi);
                    }

                    float r = targetHz / detectedHz;
                    if (detune != 0f) r *= Mathf.Pow(2f, detune / 1200f);
                    ratioTarget = Mathf.Clamp(r, 0.5f, 2.0f);
                }
                else
                {
                    if (!holdLast) ratioTarget = 1f;
                }
            }

            ratioSmoothed += (ratioTarget - ratioSmoothed) * alphaPerSample;

            if (amount <= 0.0001f)
            {
                for (int c = 0; c < channels; c++)
                {
                    float v = data[baseIdx + c] * outputGain;
                    data[baseIdx + c] = softClipLimiter ? SoftClip(v) : v;
                }

                writeIndex = (writeIndex + 1) & ringMask;
                continue;
            }

            float step = (1f - ratioSmoothed) / ws;
            phase = Wrap01(phase + step);

            for (int c = 0; c < channels; c++)
            {
                float dry = data[baseIdx + c];
                float tuned = PitchShiftSample(ring[c], writeIndex, ringMask, ws, phase);

                float outSample = (wet >= 0.999f ? tuned : Mathf.Lerp(dry, tuned, wet)) * outputGain;
                data[baseIdx + c] = softClipLimiter ? SoftClip(outSample) : outSample;
            }

            writeIndex = (writeIndex + 1) & ringMask;
        }
    }

    float DetectPitchHzFromRing(int n, float minHz, float maxHz, float threshold)
    {
        int start = (writeIndex - n + 1) & ringMask;

        float mean = 0f;
        for (int i = 0; i < n; i++)
        {
            int idx = (start + i) & ringMask;
            float v = ring[0][idx];
            analysisBuf[i] = v;
            mean += v;
        }
        mean /= n;

        for (int i = 0; i < n; i++) analysisBuf[i] -= mean;

        int tauMin = Mathf.Clamp(Mathf.FloorToInt(sampleRate / Mathf.Max(maxHz, 1f)), 2, n - 2);
        int tauMax = Mathf.Clamp(Mathf.CeilToInt(sampleRate / Mathf.Max(minHz, 1f)), tauMin + 1, n - 2);

        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            float sum = 0f;
            int limit = n - tau;
            for (int i = 0; i < limit; i++)
            {
                float d = analysisBuf[i] - analysisBuf[i + tau];
                sum += d * d;
            }
            yinDiff[tau] = sum;
        }

        float running = 0f;
        yinCmnd[tauMin] = 1f;

        for (int tau = tauMin + 1; tau <= tauMax; tau++)
        {
            running += yinDiff[tau];
            float denom = running <= 0f ? 1e-9f : running;
            yinCmnd[tau] = yinDiff[tau] * tau / denom;
        }

        int bestTau = -1;
        for (int tau = tauMin + 1; tau <= tauMax - 1; tau++)
        {
            if (yinCmnd[tau] < threshold && yinCmnd[tau] < yinCmnd[tau - 1] && yinCmnd[tau] <= yinCmnd[tau + 1])
            {
                bestTau = tau;
                break;
            }
        }

        if (bestTau < 0) return 0f;

        float t = bestTau;
        float y0 = yinCmnd[bestTau - 1];
        float y1 = yinCmnd[bestTau];
        float y2 = yinCmnd[bestTau + 1];
        float denomP = (y0 - 2f * y1 + y2);
        if (Mathf.Abs(denomP) > 1e-9f)
        {
            float shift = 0.5f * (y0 - y2) / denomP;
            t = bestTau + Mathf.Clamp(shift, -0.5f, 0.5f);
        }

        if (t <= 0.0001f) return 0f;

        float hz = sampleRate / t;
        if (hz < minHz || hz > maxHz) return 0f;
        return hz;
    }

    static float QuantizeToScaleHz(float hz, int keyPc, int scaleMode)
    {
        float midi = HzToMidi(hz);

        int baseMidi = Mathf.RoundToInt(midi);
        int best = baseMidi;
        float bestDist = 9999f;

        for (int d = -12; d <= 12; d++)
        {
            int cand = baseMidi + d;
            int pc = Mod(cand - keyPc, 12);

            bool ok;
            if (scaleMode == (int)ScaleMode.Chromatic) ok = true;
            else if (scaleMode == (int)ScaleMode.Major) ok = InDegrees(pc, MajorDegrees);
            else ok = InDegrees(pc, MinorDegrees);

            if (!ok) continue;

            float dist = Mathf.Abs(cand - midi);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = cand;
            }
        }

        return MidiToHz(best);
    }

    static bool InDegrees(int pc, int[] degrees)
    {
        for (int i = 0; i < degrees.Length; i++)
        {
            if (degrees[i] == pc) return true;
        }
        return false;
    }

    static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    static float HzToMidi(float hz)
    {
        return 69f + 12f * Mathf.Log(hz / 440f, 2f);
    }

    static float MidiToHz(float midi)
    {
        return 440f * Mathf.Pow(2f, (midi - 69f) / 12f);
    }

    static float PitchShiftSample(float[] buf, int writeIdx, int mask, int ws, float phaseA)
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
        return Mathf.Sign(x);
    }
}
