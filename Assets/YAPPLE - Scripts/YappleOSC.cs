using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleOSC : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button addOscButton;
    [SerializeField] private GameObject oscItemPrefab;
    [SerializeField] private Transform content;

    [Header("Refs")]
    [SerializeField] private YappleCore core;
    [SerializeField] private YappleVosk vosk;

    [Header("OSC")]
    [SerializeField] private string targetIp = "127.0.0.1";
    [SerializeField] private int targetPort = 9000;

    [Header("Behavior")]
    [SerializeField, Range(0f, 5f)] private float perWordCooldownSeconds = 2f;
    [SerializeField] private bool triggerOnPartial = false;
    [SerializeField] private bool allowMultiplePerWordRandom = true;
    [SerializeField, Range(0f, 2f)] private float triggerPulseSeconds = 0.75f;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private List<AudioClip> clips = new List<AudioClip>();
    [SerializeField] private float minPitch = 0.8f;
    [SerializeField] private float maxPitch = 1.2f;

    private readonly List<YappleOSCItem> items = new List<YappleOSCItem>();
    private readonly Dictionary<string, List<OscBoolCommand>> wordToOsc = new Dictionary<string, List<OscBoolCommand>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastRunByWord = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> toggleStateByParameter = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> triggerTokenByParameter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private UdpClient udp;
    private IPEndPoint targetEndPoint;

    private enum CommandMode
    {
        Normal,
        Trigger,
        Toggle
    }

    private struct OscBoolCommand
    {
        public string Parameter;
        public bool Value;
        public CommandMode Mode;
    }

    private void Awake()
    {
        if (core == null)
            core = GetComponent<YappleCore>();

        if (vosk == null)
            vosk = GetComponent<YappleVosk>();

        SetupOscTransport();
    }

    private void OnEnable()
    {
        if (addOscButton != null)
            addOscButton.onClick.AddListener(AddOscClicked);

        if (vosk != null)
            vosk.OnKeyword += OnKeyword;

        if (core != null)
            core.CollectKeywords += OnCollectKeywords;
    }

    private void OnDisable()
    {
        StopAllCoroutines();

        if (addOscButton != null)
            addOscButton.onClick.RemoveListener(AddOscClicked);

        if (vosk != null)
            vosk.OnKeyword -= OnKeyword;

        if (core != null)
            core.CollectKeywords -= OnCollectKeywords;

        DisposeOscTransport();
    }

    private void SetupOscTransport()
    {
        if (udp != null)
            return;

        if (!IPAddress.TryParse(targetIp, out var ip))
            ip = IPAddress.Loopback;

        targetEndPoint = new IPEndPoint(ip, Mathf.Clamp(targetPort, 1, 65535));
        udp = new UdpClient();
    }

    private void DisposeOscTransport()
    {
        if (udp != null)
        {
            udp.Close();
            udp.Dispose();
            udp = null;
        }
    }

    private void AddOscClicked()
    {
        if (oscItemPrefab == null || content == null)
            return;

        GameObject go = Instantiate(oscItemPrefab);
        go.transform.SetParent(content, false);

        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.localScale = Vector3.one;
            rt.anchoredPosition3D = Vector3.zero;
        }

        var item = go.GetComponent<YappleOSCItem>();
        if (item == null)
        {
            Destroy(go);
            return;
        }

        item.OnDeleteRequested = HandleDeleteRequested;
        item.OnRunRequested = HandleRunRequested;
        item.OnChanged = HandleChanged;

        items.Add(item);
        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }

    private void HandleDeleteRequested(YappleOSCItem item)
    {
        if (item == null)
            return;

        items.Remove(item);
        Destroy(item.gameObject);

        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }

    private void HandleRunRequested(YappleOSCItem item)
    {
        if (item == null)
            return;

        if (TryParseOscCommand(item.OSCCommand, out var cmd))
        {
            if (ExecuteOscCommand(cmd))
                PlayFeedback();
        }
    }

    private void HandleChanged(YappleOSCItem _)
    {
        RebuildMap();

        if (core != null)
            core.RequestRebuild();
    }

    private void RebuildMap()
    {
        wordToOsc.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null)
                continue;

            string w = NormalizeToken(it.Word);
            if (string.IsNullOrWhiteSpace(w))
                continue;

            if (!TryParseOscCommand(it.OSCCommand, out var cmd))
                continue;

            if (!wordToOsc.TryGetValue(w, out var list))
            {
                list = new List<OscBoolCommand>(2);
                wordToOsc[w] = list;
            }

            list.Add(cmd);
        }
    }

    private void OnCollectKeywords(HashSet<string> set)
    {
        if (set == null)
            return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null)
                continue;

            string w = NormalizeToken(it.Word);
            if (string.IsNullOrWhiteSpace(w))
                continue;

            set.Add(w);
        }
    }

    private void OnKeyword(string word, float conf, bool partial)
    {
        if (!triggerOnPartial && partial)
            return;

        string w = NormalizeToken(word);
        if (string.IsNullOrWhiteSpace(w))
            return;

        float now = Time.unscaledTime;

        if (lastRunByWord.TryGetValue(w, out float last))
        {
            if (now - last < perWordCooldownSeconds)
                return;
        }

        if (!wordToOsc.TryGetValue(w, out var cmds))
            return;

        if (cmds == null || cmds.Count == 0)
            return;

        var cmd = cmds[0];

        if (allowMultiplePerWordRandom && cmds.Count > 1)
            cmd = cmds[UnityEngine.Random.Range(0, cmds.Count)];

        if (ExecuteOscCommand(cmd))
        {
            lastRunByWord[w] = now;
            PlayFeedback();
        }
    }

    private bool ExecuteOscCommand(OscBoolCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Parameter))
            return false;

        if (cmd.Mode == CommandMode.Toggle)
            return ExecuteToggle(cmd.Parameter, cmd.Value);

        if (!SendVrchatBool(cmd.Parameter, cmd.Value))
            return false;

        if (cmd.Mode == CommandMode.Trigger)
            StartTriggerPulse(cmd.Parameter, cmd.Value);

        return true;
    }

    private bool ExecuteToggle(string parameter, bool firstValue)
    {
        bool next;
        if (toggleStateByParameter.TryGetValue(parameter, out var current))
            next = !current;
        else
            next = firstValue;

        if (!SendVrchatBool(parameter, next))
            return false;

        toggleStateByParameter[parameter] = next;
        return true;
    }

    private void StartTriggerPulse(string parameter, bool sentValue)
    {
        int token = 0;
        triggerTokenByParameter.TryGetValue(parameter, out token);
        token++;
        triggerTokenByParameter[parameter] = token;

        bool backValue = !sentValue;
        StartCoroutine(TriggerPulseCoroutine(parameter, backValue, Mathf.Max(0f, triggerPulseSeconds), token));
    }

    private IEnumerator TriggerPulseCoroutine(string parameter, bool backValue, float seconds, int token)
    {
        if (seconds > 0f)
            yield return new WaitForSecondsRealtime(seconds);

        if (!triggerTokenByParameter.TryGetValue(parameter, out var currentToken) || currentToken != token)
            yield break;

        SendVrchatBool(parameter, backValue);

        if (triggerTokenByParameter.TryGetValue(parameter, out currentToken) && currentToken == token)
            triggerTokenByParameter.Remove(parameter);
    }

    private void PlayFeedback()
    {
        if (audioSource == null || clips == null || clips.Count == 0)
            return;

        var clip = clips[UnityEngine.Random.Range(0, clips.Count)];
        if (clip == null)
            return;

        audioSource.pitch = UnityEngine.Random.Range(minPitch, maxPitch);
        audioSource.PlayOneShot(clip);
    }

    private bool SendVrchatBool(string parameterName, bool value)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return false;

        SetupOscTransport();
        if (udp == null || targetEndPoint == null)
            return false;

        string address = "/avatar/parameters/" + parameterName.Trim();
        byte[] pkt = BuildOscBoolPacket(address, value);

        try
        {
            udp.Send(pkt, pkt.Length, targetEndPoint);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseOscCommand(string text, out OscBoolCommand cmd)
    {
        cmd = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string s = text.Trim();
        CommandMode mode = CommandMode.Normal;

        if (StartsWithPrefix(s, "trigger:", out var rest))
        {
            mode = CommandMode.Trigger;
            s = rest.Trim();
        }
        else if (StartsWithPrefix(s, "toggle:", out rest))
        {
            mode = CommandMode.Toggle;
            s = rest.Trim();
        }

        int colon = s.IndexOf(':');
        if (colon <= 0 || colon >= s.Length - 1)
            return false;

        string param = s.Substring(0, colon).Trim();
        string val = s.Substring(colon + 1).Trim();

        if (param.Length == 0)
            return false;

        for (int i = 0; i < param.Length; i++)
        {
            char c = param[i];
            bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-';
            if (!ok)
                return false;
        }

        bool b;
        if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
            b = true;
        else if (string.Equals(val, "false", StringComparison.OrdinalIgnoreCase))
            b = false;
        else if (string.Equals(val, "1", StringComparison.OrdinalIgnoreCase))
            b = true;
        else if (string.Equals(val, "0", StringComparison.OrdinalIgnoreCase))
            b = false;
        else
            return false;

        cmd = new OscBoolCommand { Parameter = param, Value = b, Mode = mode };
        return true;
    }

    private static bool StartsWithPrefix(string s, string prefix, out string rest)
    {
        if (s != null && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = s.Substring(prefix.Length);
            return true;
        }

        rest = s;
        return false;
    }

    private static byte[] BuildOscBoolPacket(string address, bool value)
    {
        var bytes = new List<byte>(128);
        WritePaddedOscString(bytes, address);
        WritePaddedOscString(bytes, value ? ",T" : ",F");
        return bytes.ToArray();
    }

    private static void WritePaddedOscString(List<byte> dst, string s)
    {
        if (s == null)
            s = string.Empty;

        byte[] str = Encoding.UTF8.GetBytes(s);
        dst.AddRange(str);
        dst.Add(0);

        int pad = 4 - (dst.Count % 4);
        if (pad != 4)
        {
            for (int i = 0; i < pad; i++)
                dst.Add(0);
        }
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
}
