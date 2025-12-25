using System;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleSoundItem : MonoBehaviour
{
    [Header("Prefab UI")]
    [SerializeField] private TMP_InputField wordInput;
    [SerializeField] private TMP_Text audioNameText;
    [SerializeField] private TMP_Text lengthText;
    [SerializeField] private Button playButton;
    [SerializeField] private Button deleteButton;
    [SerializeField] private Slider volumeSlider;

    public Action<YappleSoundItem> OnPlayRequested;
    public Action<YappleSoundItem> OnDeleteRequested;
    public Action<YappleSoundItem> OnWordChanged;
    public Action<YappleSoundItem> OnVolumeChanged;

    public string FilePath { get; private set; }
    public AudioClip Clip { get; private set; }

    private float volumePercent = 100f;

    public string Word => wordInput == null ? null : wordInput.text;

    public float VolumePercent => volumePercent;
    public float Volume01 => Mathf.Clamp01(volumePercent / 100f);

    private void Awake()
    {
        if (playButton != null)
            playButton.onClick.AddListener(() => OnPlayRequested?.Invoke(this));

        if (deleteButton != null)
            deleteButton.onClick.AddListener(() => OnDeleteRequested?.Invoke(this));

        if (wordInput != null)
        {
            wordInput.onValueChanged.AddListener(_ => OnWordChanged?.Invoke(this));
            wordInput.onEndEdit.AddListener(_ => OnWordChanged?.Invoke(this));
        }

        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 100f;
            SetVolumePercent(100f);
            volumeSlider.onValueChanged.AddListener(OnVolumeSliderChanged);
        }

        UpdatePlayInteractable();
    }

    private void OnVolumeSliderChanged(float v)
    {
        volumePercent = Mathf.Clamp(v, 0f, 100f);
        OnVolumeChanged?.Invoke(this);
    }

    public void SetVolumePercent(float percent)
    {
        float p = Mathf.Clamp(percent, 0f, 100f);
        volumePercent = p;

        if (volumeSlider != null)
            volumeSlider.SetValueWithoutNotify(p);
    }

    public void SetFile(string filePath)
    {
        FilePath = filePath;
        Clip = null;

        if (audioNameText != null)
            audioNameText.text = string.IsNullOrWhiteSpace(filePath) ? "Unknown" : Path.GetFileName(filePath);

        SetLoading();
        UpdatePlayInteractable();
    }

    public void SetLoading()
    {
        if (lengthText != null)
            lengthText.text = "LOADING...";
    }

    public void SetLoadFailed(string message)
    {
        Clip = null;

        if (lengthText != null)
            lengthText.text = "LOAD FAILED";

        UpdatePlayInteractable();
        Debug.LogError(message);
    }

    public void SetClip(AudioClip clip)
    {
        Clip = clip;

        if (lengthText != null)
            lengthText.text = clip == null ? "LENGTH: 00:00" : "LENGTH: " + FormatTime(clip.length);

        UpdatePlayInteractable();
    }

    private void UpdatePlayInteractable()
    {
        if (playButton != null)
            playButton.interactable = Clip != null;
    }

    private static string FormatTime(float seconds)
    {
        if (seconds < 0f) seconds = 0f;

        int total = Mathf.RoundToInt(seconds);
        int m = total / 60;
        int s = total % 60;

        return m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
    }
}