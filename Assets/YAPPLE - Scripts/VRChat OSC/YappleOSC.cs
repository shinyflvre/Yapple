using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly Dictionary<string, List<OscCommand>> wordToOsc = new Dictionary<string, List<OscCommand>>(StringComparer.OrdinalIgnoreCase);
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

    private enum ValueKind
    {
        Bool,
        Int,
        Float
    }

    private struct OscCommand
    {
        public string Parameter;
        public CommandMode Mode;
        public ValueKind Kind;

        public bool BoolValue;
        public int IntValue;
        public float FloatValue;

        public bool HasRandomRange;
        public float RandomMin;
        public float RandomMax;
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
                list = new List<OscCommand>(2);
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

    private bool ExecuteOscCommand(OscCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Parameter))
            return false;

        if (cmd.Kind == ValueKind.Bool)
        {
            if (cmd.Mode == CommandMode.Toggle)
                return ExecuteToggle(cmd.Parameter, cmd.BoolValue);

            if (!SendVrchatBool(cmd.Parameter, cmd.BoolValue))
                return false;

            if (cmd.Mode == CommandMode.Trigger)
                StartTriggerPulse(cmd.Parameter, cmd.BoolValue);

            return true;
        }

        if (cmd.Kind == ValueKind.Int)
        {
            int v = cmd.IntValue;
            if (cmd.HasRandomRange)
                v = PickRandomInt(cmd.RandomMin, cmd.RandomMax);

            return SendVrchatInt(cmd.Parameter, v);
        }

        if (cmd.Kind == ValueKind.Float)
        {
            float v = cmd.FloatValue;
            if (cmd.HasRandomRange)
                v = PickRandomFloat(cmd.RandomMin, cmd.RandomMax);

            return SendVrchatFloat(cmd.Parameter, v);
        }

        return false;
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

    private bool SendVrchatInt(string parameterName, int value)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return false;

        SetupOscTransport();
        if (udp == null || targetEndPoint == null)
            return false;

        string address = "/avatar/parameters/" + parameterName.Trim();
        byte[] pkt = BuildOscIntPacket(address, value);

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

    private bool SendVrchatFloat(string parameterName, float value)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return false;

        SetupOscTransport();
        if (udp == null || targetEndPoint == null)
            return false;

        string address = "/avatar/parameters/" + parameterName.Trim();
        byte[] pkt = BuildOscFloatPacket(address, value);

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

    private static int PickRandomInt(float min, float max)
    {
        int a = Mathf.FloorToInt(min);
        int b = Mathf.FloorToInt(max);
        if (a > b)
        {
            int t = a;
            a = b;
            b = t;
        }

        if (a == b)
            return a;

        if (b < int.MaxValue)
            return UnityEngine.Random.Range(a, b + 1);

        return UnityEngine.Random.Range(a, b);
    }

    private static float PickRandomFloat(float min, float max)
    {
        if (min > max)
        {
            float t = min;
            min = max;
            max = t;
        }

        return min + (max - min) * UnityEngine.Random.value;
    }

    private static bool TryParseOscCommand(string text, out OscCommand cmd)
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

        bool hasRandom = false;
        float ranMin = 0f;
        float ranMax = 0f;

        if (TryConsumeRandomPrefix(ref s, out ranMin, out ranMax))
            hasRandom = true;

        string[] parts = s.Split(':');
        if (parts.Length == 2)
        {
            if (hasRandom)
                return false;

            string param = parts[0].Trim();
            string val = parts[1].Trim();

            if (!IsValidParamName(param))
                return false;

            if (!TryParseBool(val, out bool b))
                return false;

            cmd = new OscCommand
            {
                Parameter = param,
                Kind = ValueKind.Bool,
                BoolValue = b,
                Mode = mode
            };
            return true;
        }

        if (parts.Length == 3)
        {
            if (mode != CommandMode.Normal)
                return false;

            string param = parts[0].Trim();
            string type = parts[1].Trim();
            string val = parts[2].Trim();

            if (!IsValidParamName(param))
                return false;

            if (string.Equals(type, "int", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseIntLenient(val, out int iv))
                    return false;

                cmd = new OscCommand
                {
                    Parameter = param,
                    Kind = ValueKind.Int,
                    IntValue = iv,
                    Mode = CommandMode.Normal,
                    HasRandomRange = hasRandom,
                    RandomMin = ranMin,
                    RandomMax = ranMax
                };
                return true;
            }

            if (string.Equals(type, "float", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseFloatFlexible(val, out float fv))
                    return false;

                cmd = new OscCommand
                {
                    Parameter = param,
                    Kind = ValueKind.Float,
                    FloatValue = fv,
                    Mode = CommandMode.Normal,
                    HasRandomRange = hasRandom,
                    RandomMin = ranMin,
                    RandomMax = ranMax
                };
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryConsumeRandomPrefix(ref string s, out float min, out float max)
    {
        min = 0f;
        max = 0f;

        if (string.IsNullOrWhiteSpace(s))
            return false;

        string t = s.TrimStart();
        if (!t.StartsWith("Ran(", StringComparison.OrdinalIgnoreCase))
            return false;

        int close = t.IndexOf(')');
        if (close < 0)
            return false;

        string inside = t.Substring(4, close - 4);
        int dash = inside.IndexOf('-');
        if (dash <= 0 || dash >= inside.Length - 1)
            return false;

        string a = inside.Substring(0, dash).Trim();
        string b = inside.Substring(dash + 1).Trim();

        if (!TryParseFloatFlexible(a, out min))
            return false;

        if (!TryParseFloatFlexible(b, out max))
            return false;

        int after = close + 1;
        if (after >= t.Length || t[after] != ':')
            return false;

        s = t.Substring(after + 1).Trim();
        return true;
    }

    private static bool TryParseBool(string val, out bool b)
    {
        if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
        {
            b = true;
            return true;
        }

        if (string.Equals(val, "false", StringComparison.OrdinalIgnoreCase))
        {
            b = false;
            return true;
        }

        if (string.Equals(val, "1", StringComparison.OrdinalIgnoreCase))
        {
            b = true;
            return true;
        }

        if (string.Equals(val, "0", StringComparison.OrdinalIgnoreCase))
        {
            b = false;
            return true;
        }

        b = false;
        return false;
    }

    private static bool TryParseIntLenient(string val, out int iv)
    {
        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
            return true;

        if (TryParseFloatFlexible(val, out float f))
        {
            iv = Mathf.FloorToInt(f);
            return true;
        }

        iv = 0;
        return false;
    }

    private static bool TryParseFloatFlexible(string val, out float fv)
    {
        if (val == null)
        {
            fv = 0f;
            return false;
        }

        string s = val.Trim();

        if (s.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - 1).Trim();

        s = s.Replace(',', '.');

        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out fv);
    }

    private static bool IsValidParamName(string param)
    {
        if (string.IsNullOrWhiteSpace(param))
            return false;

        for (int i = 0; i < param.Length; i++)
        {
            char c = param[i];
            bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/';
            if (!ok)
                return false;
        }

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

    private static byte[] BuildOscIntPacket(string address, int value)
    {
        var bytes = new List<byte>(128);
        WritePaddedOscString(bytes, address);
        WritePaddedOscString(bytes, ",i");
        WriteOscInt32(bytes, value);
        return bytes.ToArray();
    }

    private static byte[] BuildOscFloatPacket(string address, float value)
    {
        var bytes = new List<byte>(128);
        WritePaddedOscString(bytes, address);
        WritePaddedOscString(bytes, ",f");
        WriteOscFloat32(bytes, value);
        return bytes.ToArray();
    }

    private static void WriteOscInt32(List<byte> dst, int value)
    {
        byte[] raw = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(raw);
        dst.AddRange(raw);
    }

    private static void WriteOscFloat32(List<byte> dst, float value)
    {
        byte[] raw = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(raw);
        dst.AddRange(raw);
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
