using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleOSCItem : MonoBehaviour
{
    [Header("Buttons")]
    [SerializeField] private Button deleteButton;
    [SerializeField] private Button runButton;

    [Header("Inputs")]
    [SerializeField] public TMP_InputField oscCommandInput;
    [SerializeField] public TMP_InputField wordInput;

    [Header("Indicators")]
    [SerializeField] private GameObject oscTypeObject;
    [SerializeField] private TMP_Text oscTypeText;
    [SerializeField] private GameObject oscModifierObject;
    [SerializeField] private TMP_Text oscModifierText;

    public Action<YappleOSCItem> OnDeleteRequested;
    public Action<YappleOSCItem> OnRunRequested;
    public Action<YappleOSCItem> OnChanged;

    public string OSCCommand => oscCommandInput != null ? oscCommandInput.text : string.Empty;
    public string Word => wordInput != null ? wordInput.text : string.Empty;

    private void OnEnable()
    {
        if (deleteButton != null)
            deleteButton.onClick.AddListener(DeleteClicked);

        if (runButton != null)
            runButton.onClick.AddListener(RunClicked);

        if (oscCommandInput != null)
            oscCommandInput.onValueChanged.AddListener(Changed);

        if (wordInput != null)
            wordInput.onValueChanged.AddListener(Changed);

        UpdateIndicators();
        StartCoroutine(DeferredIndicatorRefresh());
    }

    private void OnDisable()
    {
        StopAllCoroutines();

        if (deleteButton != null)
            deleteButton.onClick.RemoveListener(DeleteClicked);

        if (runButton != null)
            runButton.onClick.RemoveListener(RunClicked);

        if (oscCommandInput != null)
            oscCommandInput.onValueChanged.RemoveListener(Changed);

        if (wordInput != null)
            wordInput.onValueChanged.RemoveListener(Changed);
    }

    public void RefreshIndicators()
    {
        UpdateIndicators();
    }

    private IEnumerator DeferredIndicatorRefresh()
    {
        yield return null;
        UpdateIndicators();
        yield return null;
        UpdateIndicators();
    }

    private void DeleteClicked()
    {
        OnDeleteRequested?.Invoke(this);
    }

    private void RunClicked()
    {
        OnRunRequested?.Invoke(this);
    }

    private void Changed(string _)
    {
        UpdateIndicators();
        OnChanged?.Invoke(this);
    }

    private void UpdateIndicators()
    {
        string raw = OSCCommand;
        if (string.IsNullOrWhiteSpace(raw))
        {
            SetTypeVisible(false);
            SetModifierVisible(false);
            return;
        }

        string s = raw.Trim();

        bool hasModeModifier = false;
        string modeModifier = string.Empty;

        if (TryConsumePrefix(ref s, "trigger:"))
        {
            hasModeModifier = true;
            modeModifier = "Trigger";
        }
        else if (TryConsumePrefix(ref s, "toggle:"))
        {
            hasModeModifier = true;
            modeModifier = "Toggle";
        }

        bool hasRandomModifier = TryConsumeRandomPrefix(ref s);

        string type;
        if (!TryDetermineType(s, hasModeModifier, hasRandomModifier, out type))
        {
            SetTypeVisible(false);
            SetModifierVisible(false);
            return;
        }

        SetType(type);

        string modifier = string.Empty;
        if (hasModeModifier)
            modifier = modeModifier;
        else if (hasRandomModifier)
            modifier = "Random";

        if (string.IsNullOrWhiteSpace(modifier))
            SetModifierVisible(false);
        else
            SetModifier(modifier);
    }

    private static bool TryConsumePrefix(ref string s, string prefix)
    {
        if (s != null && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(prefix.Length).Trim();
            return true;
        }

        return false;
    }

    private static bool TryConsumeRandomPrefix(ref string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;

        string t = s.TrimStart();
        if (!t.StartsWith("Ran(", StringComparison.OrdinalIgnoreCase))
            return false;

        int close = t.IndexOf(')');
        if (close < 0)
            return false;

        int after = close + 1;
        if (after >= t.Length || t[after] != ':')
            return false;

        s = t.Substring(after + 1).Trim();
        return true;
    }

    private static bool TryDetermineType(string s, bool hasModeModifier, bool hasRandomModifier, out string type)
    {
        type = string.Empty;

        if (string.IsNullOrWhiteSpace(s))
            return false;

        string[] parts = s.Split(':');

        if (parts.Length == 2)
        {
            if (hasRandomModifier)
                return false;

            string param = parts[0].Trim();
            string val = parts[1].Trim();

            if (param.Length == 0)
                return false;

            if (!IsValidParamName(param))
                return false;

            if (!IsBoolValue(val))
                return false;

            type = "Bool";
            return true;
        }

        if (parts.Length == 3)
        {
            if (hasModeModifier)
                return false;

            string param = parts[0].Trim();
            string t = parts[1].Trim();
            string val = parts[2].Trim();

            if (param.Length == 0)
                return false;

            if (!IsValidParamName(param))
                return false;

            if (t.Equals("int", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsNumericValue(val))
                    return false;

                type = "Int";
                return true;
            }

            if (t.Equals("float", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsNumericValue(val))
                    return false;

                type = "Float";
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool IsValidParamName(string param)
    {
        for (int i = 0; i < param.Length; i++)
        {
            char c = param[i];
            bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/';
            if (!ok)
                return false;
        }

        return true;
    }

    private static bool IsBoolValue(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;

        if (v.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;

        if (v.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;

        if (v.Equals("1", StringComparison.OrdinalIgnoreCase))
            return true;

        if (v.Equals("0", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsNumericValue(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return false;

        string s = v.Trim();

        if (s.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - 1).Trim();

        s = s.Replace(',', '.');

        float _;
        return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private void SetType(string t)
    {
        if (oscTypeText != null)
            oscTypeText.text = t;

        SetTypeVisible(true);
    }

    private void SetModifier(string m)
    {
        if (oscModifierText != null)
            oscModifierText.text = m;

        SetModifierVisible(true);
    }

    private void SetTypeVisible(bool visible)
    {
        if (oscTypeObject != null)
            oscTypeObject.SetActive(visible);
    }

    private void SetModifierVisible(bool visible)
    {
        if (oscModifierObject != null)
            oscModifierObject.SetActive(visible);
    }
}
