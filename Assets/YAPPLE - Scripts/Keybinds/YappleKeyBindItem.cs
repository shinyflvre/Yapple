using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class YappleKeyBindItem : MonoBehaviour, IPointerClickHandler
{
    [Header("UI")]
    [SerializeField] private Button deleteButton;
    [SerializeField] private Button runButton;
    [SerializeField] private TMP_InputField wordInput;

    [Header("Keys")]
    [SerializeField] private GameObject key1;
    [SerializeField] private TMP_Text key1Text;
    [SerializeField] private GameObject key2;
    [SerializeField] private TMP_Text key2Text;
    [SerializeField] private GameObject key3;
    [SerializeField] private TMP_Text key3Text;

    [Header("Info")]
    [SerializeField] private TMP_Text infoText;

    private YappleKeyBinds owner;

    private readonly List<KeyCode> keys = new List<KeyCode>(3);
    private bool captureArmed;
    private bool captureDone;
    private float allReleasedSince;
    private float lastInputAt;

    public void Bind(YappleKeyBinds owner)
    {
        this.owner = owner;

        if (deleteButton != null) deleteButton.onClick.AddListener(OnDeleteClicked);
        if (runButton != null) runButton.onClick.AddListener(OnRunClicked);
        if (wordInput != null) wordInput.onValueChanged.AddListener(OnWordChanged);

        SetKeysUI();
        SetInfoIdle();
    }

    private void OnDestroy()
    {
        if (deleteButton != null) deleteButton.onClick.RemoveListener(OnDeleteClicked);
        if (runButton != null) runButton.onClick.RemoveListener(OnRunClicked);
        if (wordInput != null) wordInput.onValueChanged.RemoveListener(OnWordChanged);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (captureDone) return;
        if (keys.Count > 0) return;

        ArmCapture();
    }

    private void Update()
    {
        if (!captureArmed) return;
        if (owner == null) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            captureArmed = false;
            SetInfoIdle();
            return;
        }

        bool safe = owner.SafeKeybindsEnabled();

        var allowed = owner.GetCaptureKeyList();
        for (int i = 0; i < allowed.Length; i++)
        {
            var k = allowed[i];
            if (keys.Count >= 3) break;
            if (keys.Contains(k)) continue;

            if (Input.GetKeyDown(k))
            {
                keys.Add(k);
                lastInputAt = Time.unscaledTime;
                SetKeysUI();
                SetInfoCapturing();

                if (keys.Count >= 3)
                {
                    FinalizeCapture();
                    return;
                }
            }
        }

        if (keys.Count == 0)
            return;

        if (safe)
        {
            float timeout = owner.SafeKeybindsTimeoutSeconds();
            if (lastInputAt > 0f && Time.unscaledTime - lastInputAt >= timeout)
                FinalizeCapture();
            return;
        }

        bool anyHeld = false;
        for (int i = 0; i < keys.Count; i++)
        {
            if (Input.GetKey(keys[i]))
            {
                anyHeld = true;
                break;
            }
        }

        if (anyHeld)
        {
            allReleasedSince = 0f;
            return;
        }

        if (allReleasedSince <= 0f)
            allReleasedSince = Time.unscaledTime;

        if (Time.unscaledTime - allReleasedSince >= 0.12f)
            FinalizeCapture();
    }

    public bool HasKeys()
    {
        return keys.Count > 0;
    }

    public KeyCode[] GetKeys()
    {
        return keys.ToArray();
    }

    public string GetNormalizedWord()
    {
        if (wordInput == null) return string.Empty;
        return NormalizeToken(wordInput.text);
    }

    private void ArmCapture()
    {
        captureArmed = true;
        captureDone = false;
        keys.Clear();
        allReleasedSince = 0f;
        lastInputAt = 0f;
        SetKeysUI();
        SetInfoCapturing();
    }

    private void FinalizeCapture()
    {
        captureArmed = false;
        captureDone = true;
        SetKeysUI();
        SetInfoIdle();
        if (owner != null) owner.NotifyItemChanged();
    }

    private void OnRunClicked()
    {
        if (owner == null) return;
        owner.RunItem(this);
    }

    private void OnDeleteClicked()
    {
        if (owner == null) return;
        owner.DeleteItem(this);
    }

    private void OnWordChanged(string _)
    {
        if (owner != null) owner.NotifyItemChanged();
    }

    private void SetKeysUI()
    {
        if (key1 != null) key1.SetActive(keys.Count >= 1);
        if (key2 != null) key2.SetActive(keys.Count >= 2);
        if (key3 != null) key3.SetActive(keys.Count >= 3);

        if (key1Text != null) key1Text.text = keys.Count >= 1 ? owner.KeyToDisplayName(keys[0]) : string.Empty;
        if (key2Text != null) key2Text.text = keys.Count >= 2 ? owner.KeyToDisplayName(keys[1]) : string.Empty;
        if (key3Text != null) key3Text.text = keys.Count >= 3 ? owner.KeyToDisplayName(keys[2]) : string.Empty;
    }

    private void SetInfoCapturing()
    {
        if (infoText == null) return;

        bool safe = owner != null && owner.SafeKeybindsEnabled();

        if (keys.Count == 0)
        {
            infoText.text = safe ? "Press up to 3 keys one by one (ESC cancel)" : "Press up to 3 keys (ESC cancel)";
            return;
        }

        infoText.text = safe ? "Wait to confirm (ESC cancel)" : "Release all keys to confirm (ESC cancel)";
    }

    private void SetInfoIdle()
    {
        if (infoText == null) return;
        if (keys.Count == 0) infoText.text = "Click entry to set up to 3 keys";
        else infoText.text = string.Empty;
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
}
