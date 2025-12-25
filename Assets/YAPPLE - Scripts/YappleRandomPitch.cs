using UnityEngine;
using UnityEngine.UI;

public sealed class YappleRandomPitch : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private Slider slider;

    [Header("Pitch Range")]
    [SerializeField] private float minPitch = 0.85f;
    [SerializeField] private float maxPitch = 1.15f;

    [Header("LFO Speed (Hz)")]
    [SerializeField] private float minSpeed = 0.05f;
    [SerializeField] private float maxSpeed = 8.0f;

    [Header("Slider")]
    [SerializeField] private float sliderMax = 100f;

    [Header("Behavior")]
    [SerializeField] private bool useUnscaledTime = false;

    private float basePitch = 1f;
    private float phase;

    private void Awake()
    {
        if (audioSource != null) basePitch = audioSource.pitch;
    }

    private void OnEnable()
    {
        if (audioSource != null) basePitch = audioSource.pitch;
        phase = 0f;
    }

    private void Update()
    {
        if (audioSource == null || slider == null) return;

        float amount = Mathf.Clamp01(slider.value / Mathf.Max(0.0001f, sliderMax));

        if (amount <= 0f)
        {
            audioSource.pitch = basePitch;
            return;
        }

        float hz = Mathf.Lerp(minSpeed, maxSpeed, amount);
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        phase += dt * hz * Mathf.PI * 2f;
        if (phase > Mathf.PI * 2f) phase -= Mathf.Floor(phase / (Mathf.PI * 2f)) * (Mathf.PI * 2f);

        float t = (Mathf.Sin(phase) + 1f) * 0.5f;
        float lfoPitch = Mathf.Lerp(minPitch, maxPitch, t);

        audioSource.pitch = Mathf.Lerp(basePitch, lfoPitch, amount);
    }

    public void SetSliderMax(float value)
    {
        sliderMax = Mathf.Max(0.0001f, value);
    }

    public void SetPitchRange(float min, float max)
    {
        minPitch = min;
        maxPitch = max;
    }

    public void SetSpeedRange(float min, float max)
    {
        minSpeed = Mathf.Max(0f, min);
        maxSpeed = Mathf.Max(minSpeed, max);
    }
}
