using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public sealed class YappleKeyBinds : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button addKeybindButton;
    [SerializeField] private YappleKeyBindItem keybindPrefab;
    [SerializeField] private Transform content;

    [Header("Refs")]
    [SerializeField] private YappleCore core;
    [SerializeField] private YappleVosk vosk;

    [Header("Behavior")]
    [SerializeField, Range(0f, 5f)] private float perWordCooldownSeconds = 0.4f;
    [SerializeField] private bool triggerOnPartial = false;

    [Header("Capture")]
    [SerializeField] private bool activateSafeKeybinds = false;
    [SerializeField, Range(0.2f, 10f)] private float safeKeybindIdleSeconds = 2f;

    [Header("Runtime")]
    [SerializeField] private bool forceRunInBackground = true;
    [SerializeField] private bool debugLog = false;
    [SerializeField] private TMP_Text debugText;

    [Header("Key Sending")]
    [SerializeField] private bool preferInterceptionIfAvailable = true;
    [SerializeField, Range(0f, 0.2f)] private float perKeyDelaySeconds = 0.01f;
    [SerializeField, Range(0f, 0.2f)] private float holdSeconds = 0.06f;
    [SerializeField, Range(0f, 0.25f)] private float chordModifierLeadSeconds = 0.04f;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private List<AudioClip> clips = new List<AudioClip>();
    [SerializeField, Range(0.1f, 3f)] private float minPitch = 0.95f;
    [SerializeField, Range(0.1f, 3f)] private float maxPitch = 1.05f;

    private readonly List<YappleKeyBindItem> items = new List<YappleKeyBindItem>();
    private readonly Dictionary<string, List<YappleKeyBindItem>> wordToItems = new Dictionary<string, List<YappleKeyBindItem>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastByWord = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    private readonly object queueLock = new object();
    private readonly Queue<YappleKeyBindItem> runQueue = new Queue<YappleKeyBindItem>();

    private KeyboardBackend backend;

    private static readonly Dictionary<KeyCode, ushort> KeyCodeToVk = BuildKeyCodeToVk();
    private static readonly KeyCode[] CaptureKeys = BuildCaptureKeys();

    private string lastDebugLine;
    private float lastDebugAt;

    public bool SafeKeybindsEnabled()
    {
        return activateSafeKeybinds;
    }

    public float SafeKeybindsTimeoutSeconds()
    {
        return safeKeybindIdleSeconds;
    }

    private void OnEnable()
    {
        if (forceRunInBackground)
            Application.runInBackground = true;

        if (addKeybindButton != null) addKeybindButton.onClick.AddListener(AddKeybind);

        if (core != null) core.CollectKeywords += OnCollectKeywords;

        if (vosk != null)
        {
            vosk.OnKeyword += OnKeyword;
            vosk.OnFinalText += OnFinalText;
        }

        RebuildMap();
    }

    private void OnDisable()
    {
        if (addKeybindButton != null) addKeybindButton.onClick.RemoveListener(AddKeybind);

        if (core != null) core.CollectKeywords -= OnCollectKeywords;

        if (vosk != null)
        {
            vosk.OnKeyword -= OnKeyword;
            vosk.OnFinalText -= OnFinalText;
        }

        lock (queueLock)
            runQueue.Clear();

        if (backend != null)
        {
            backend.Dispose();
            backend = null;
        }
    }

    private void Update()
    {
        YappleKeyBindItem next = null;
        lock (queueLock)
        {
            if (runQueue.Count > 0)
                next = runQueue.Dequeue();
        }

        if (next != null)
            StartCoroutine(ExecuteItemRoutine(next));

        if (debugText != null)
        {
            if (Time.unscaledTime - lastDebugAt <= 2.0f)
                debugText.text = lastDebugLine;
            else
                debugText.text = string.Empty;
        }
    }

    public KeyCode[] GetCaptureKeyList()
    {
        return CaptureKeys;
    }

    public bool IsKeyAllowedForCapture(KeyCode key)
    {
        return KeyCodeToVk.ContainsKey(key);
    }

    public string KeyToDisplayName(KeyCode key)
    {
        if (key == KeyCode.LeftControl || key == KeyCode.RightControl) return "CTRL";
        if (key == KeyCode.LeftAlt || key == KeyCode.RightAlt) return "ALT";
        if (key == KeyCode.LeftShift || key == KeyCode.RightShift) return "SHIFT";
        if (key == KeyCode.LeftWindows || key == KeyCode.RightWindows) return "WIN";
        if (key == KeyCode.Return) return "ENTER";
        if (key == KeyCode.Escape) return "ESC";
        if (key == KeyCode.Backspace) return "BACK";
        if (key == KeyCode.Tab) return "TAB";
        if (key == KeyCode.Space) return "SPACE";
        if (key >= KeyCode.A && key <= KeyCode.Z) return key.ToString().ToUpperInvariant();
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return ((int)(key - KeyCode.Alpha0)).ToString();
        if (key >= KeyCode.F1 && key <= KeyCode.F15) return key.ToString().ToUpperInvariant();
        if (key == KeyCode.UpArrow) return "UP";
        if (key == KeyCode.DownArrow) return "DOWN";
        if (key == KeyCode.LeftArrow) return "LEFT";
        if (key == KeyCode.RightArrow) return "RIGHT";
        if (key == KeyCode.Insert) return "INS";
        if (key == KeyCode.Delete) return "DEL";
        if (key == KeyCode.Home) return "HOME";
        if (key == KeyCode.End) return "END";
        if (key == KeyCode.PageUp) return "PGUP";
        if (key == KeyCode.PageDown) return "PGDN";
        if (key == KeyCode.CapsLock) return "CAPS";
        if (key == KeyCode.Numlock) return "NUM";
        if (key == KeyCode.Print) return "PRTSC";
        if (key == KeyCode.Pause) return "PAUSE";
        return key.ToString().ToUpperInvariant();
    }

    public void NotifyItemChanged()
    {
        RebuildMap();
        if (core != null) core.RequestRebuild();
    }

    public void DeleteItem(YappleKeyBindItem item)
    {
        if (item == null) return;
        items.Remove(item);
        Destroy(item.gameObject);
        NotifyItemChanged();
    }

    public void RunItem(YappleKeyBindItem item)
    {
        if (item == null) return;
        if (!item.HasKeys()) return;
        EnqueueRun(item);
    }

    private void EnqueueRun(YappleKeyBindItem item)
    {
        lock (queueLock)
            runQueue.Enqueue(item);
    }

    private void AddKeybind()
    {
        if (keybindPrefab == null || content == null) return;

        var inst = Instantiate(keybindPrefab, content, false);
        inst.Bind(this);

        items.Add(inst);
        NotifyItemChanged();
    }

    private void OnCollectKeywords(HashSet<string> set)
    {
        if (set == null) return;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;

            string w = it.GetNormalizedWord();
            if (string.IsNullOrWhiteSpace(w)) continue;

            set.Add(w);
        }
    }

    private void OnKeyword(string word, float conf, bool partial)
    {
        if (!triggerOnPartial && partial) return;

        string w = NormalizeToken(word);
        if (string.IsNullOrWhiteSpace(w)) return;

        if (!wordToItems.TryGetValue(w, out var list) || list == null || list.Count == 0)
        {
            SetDebug($"KW '{w}' no match");
            return;
        }

        float now = Time.unscaledTime;
        if (lastByWord.TryGetValue(w, out float last))
        {
            if (now - last < perWordCooldownSeconds)
            {
                SetDebug($"KW '{w}' cooldown");
                return;
            }
        }
        lastByWord[w] = now;

        var item = PickFirstRunnable(list);
        if (item == null)
        {
            SetDebug($"KW '{w}' no runnable");
            return;
        }

        SetDebug($"KW '{w}' -> run");
        EnqueueRun(item);
    }

    private void OnFinalText(string text)
    {
    }

    private void SetDebug(string line)
    {
        lastDebugLine = line;
        lastDebugAt = Time.unscaledTime;
        if (debugLog) Debug.Log($"YappleKeyBinds: {line}");
    }

    private YappleKeyBindItem PickFirstRunnable(List<YappleKeyBindItem> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var it = list[i];
            if (it == null) continue;
            if (!it.HasKeys()) continue;
            return it;
        }
        return null;
    }

    private static bool IsModifier(KeyCode k)
    {
        return k == KeyCode.LeftShift || k == KeyCode.RightShift
            || k == KeyCode.LeftControl || k == KeyCode.RightControl
            || k == KeyCode.LeftAlt || k == KeyCode.RightAlt
            || k == KeyCode.LeftWindows || k == KeyCode.RightWindows;
    }

    private static int ModifierPriority(KeyCode k)
    {
        if (k == KeyCode.LeftWindows || k == KeyCode.RightWindows) return 0;
        if (k == KeyCode.LeftControl || k == KeyCode.RightControl) return 1;
        if (k == KeyCode.LeftShift || k == KeyCode.RightShift) return 2;
        if (k == KeyCode.LeftAlt || k == KeyCode.RightAlt) return 3;
        return 10;
    }

    private IEnumerator ExecuteItemRoutine(YappleKeyBindItem item)
    {
        EnsureBackend();

        var rawKeys = item.GetKeys();
        if (rawKeys == null || rawKeys.Length == 0) yield break;

        var mods = new List<KeyCode>(3);
        var normals = new List<KeyCode>(3);

        for (int i = 0; i < rawKeys.Length; i++)
        {
            var k = rawKeys[i];
            if (IsModifier(k)) mods.Add(k);
            else normals.Add(k);
        }

        mods.Sort((a, b) => ModifierPriority(a).CompareTo(ModifierPriority(b)));

        bool ok = true;
        string failDetail = null;

        for (int i = 0; i < mods.Count; i++)
        {
            bool sent = SendKey(mods[i], true, out ushort vk, out int err);
            if (!sent)
            {
                ok = false;
                if (failDetail == null) failDetail = $"down {mods[i]} vk 0x{vk:X2} err {err}";
            }
            if (perKeyDelaySeconds > 0f) yield return new WaitForSecondsRealtime(perKeyDelaySeconds);
        }

        if (mods.Count > 0 && chordModifierLeadSeconds > 0f)
            yield return new WaitForSecondsRealtime(chordModifierLeadSeconds);

        for (int i = 0; i < normals.Count; i++)
        {
            bool sent = SendKey(normals[i], true, out ushort vk, out int err);
            if (!sent)
            {
                ok = false;
                if (failDetail == null) failDetail = $"down {normals[i]} vk 0x{vk:X2} err {err}";
            }
            if (perKeyDelaySeconds > 0f) yield return new WaitForSecondsRealtime(perKeyDelaySeconds);
        }

        if (holdSeconds > 0f) yield return new WaitForSecondsRealtime(holdSeconds);

        for (int i = normals.Count - 1; i >= 0; i--)
        {
            bool sent = SendKey(normals[i], false, out ushort vk, out int err);
            if (!sent)
            {
                ok = false;
                if (failDetail == null) failDetail = $"up {normals[i]} vk 0x{vk:X2} err {err}";
            }
            if (perKeyDelaySeconds > 0f) yield return new WaitForSecondsRealtime(perKeyDelaySeconds);
        }

        for (int i = mods.Count - 1; i >= 0; i--)
        {
            bool sent = SendKey(mods[i], false, out ushort vk, out int err);
            if (!sent)
            {
                ok = false;
                if (failDetail == null) failDetail = $"up {mods[i]} vk 0x{vk:X2} err {err}";
            }
            if (perKeyDelaySeconds > 0f) yield return new WaitForSecondsRealtime(perKeyDelaySeconds);
        }

        SetDebug(ok ? "send ok" : $"send failed ({failDetail})");
        if (ok) PlayConfirm();
    }

    private void PlayConfirm()
    {
        if (audioSource == null) return;
        if (clips == null || clips.Count == 0) return;

        var clip = clips[UnityEngine.Random.Range(0, clips.Count)];
        if (clip == null) return;

        float a = Mathf.Min(minPitch, maxPitch);
        float b = Mathf.Max(minPitch, maxPitch);
        audioSource.pitch = UnityEngine.Random.Range(a, b);
        audioSource.PlayOneShot(clip, 1f);
    }

    private void EnsureBackend()
    {
        if (backend != null) return;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (preferInterceptionIfAvailable)
        {
            var i = new InterceptionBackend();
            if (i.IsAvailable)
            {
                backend = i;
                SetDebug("backend interception");
                return;
            }
            i.Dispose();
        }

        backend = new SendInputBackend();
        SetDebug("backend sendinput");
#else
        backend = new NullBackend();
        SetDebug("backend null");
#endif
    }

    private bool SendKey(KeyCode key, bool down, out ushort vk, out int win32Error)
    {
        win32Error = 0;
        vk = 0;

        if (!KeyCodeToVk.TryGetValue(key, out vk))
            return false;

        if (backend == null)
            return false;

        bool ok = backend.SendVk(vk, down);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (!ok && backend is SendInputBackend s)
            win32Error = s.LastWin32Error;
#endif

        return ok;
    }

    private void RebuildMap()
    {
        wordToItems.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;

            string w = it.GetNormalizedWord();
            if (string.IsNullOrWhiteSpace(w)) continue;

            if (!wordToItems.TryGetValue(w, out var list))
            {
                list = new List<YappleKeyBindItem>();
                wordToItems[w] = list;
            }

            if (!list.Contains(it))
                list.Add(it);
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

    private static Dictionary<KeyCode, ushort> BuildKeyCodeToVk()
    {
        var d = new Dictionary<KeyCode, ushort>();

        for (int i = 0; i < 26; i++)
            d[(KeyCode)((int)KeyCode.A + i)] = (ushort)(0x41 + i);

        for (int i = 0; i <= 9; i++)
            d[(KeyCode)((int)KeyCode.Alpha0 + i)] = (ushort)(0x30 + i);

        for (int i = 1; i <= 15; i++)
            d[(KeyCode)((int)KeyCode.F1 + (i - 1))] = (ushort)(0x70 + (i - 1));

        d[KeyCode.Return] = 0x0D;
        d[KeyCode.Escape] = 0x1B;
        d[KeyCode.Backspace] = 0x08;
        d[KeyCode.Tab] = 0x09;
        d[KeyCode.Space] = 0x20;

        d[KeyCode.LeftArrow] = 0x25;
        d[KeyCode.UpArrow] = 0x26;
        d[KeyCode.RightArrow] = 0x27;
        d[KeyCode.DownArrow] = 0x28;

        d[KeyCode.Insert] = 0x2D;
        d[KeyCode.Delete] = 0x2E;
        d[KeyCode.Home] = 0x24;
        d[KeyCode.End] = 0x23;
        d[KeyCode.PageUp] = 0x21;
        d[KeyCode.PageDown] = 0x22;

        d[KeyCode.LeftShift] = 0xA0;
        d[KeyCode.RightShift] = 0xA1;
        d[KeyCode.LeftControl] = 0xA2;
        d[KeyCode.RightControl] = 0xA3;
        d[KeyCode.LeftAlt] = 0xA4;
        d[KeyCode.RightAlt] = 0xA5;

        d[KeyCode.LeftWindows] = 0x5B;
        d[KeyCode.RightWindows] = 0x5C;

        d[KeyCode.CapsLock] = 0x14;
        d[KeyCode.Numlock] = 0x90;
        d[KeyCode.Print] = 0x2C;
        d[KeyCode.Pause] = 0x13;

        return d;
    }

    private static KeyCode[] BuildCaptureKeys()
    {
        var list = new List<KeyCode>(KeyCodeToVk.Count);
        foreach (var kv in KeyCodeToVk)
            list.Add(kv.Key);
        list.Sort((a, b) => ((int)a).CompareTo((int)b));
        return list.ToArray();
    }

    private abstract class KeyboardBackend : IDisposable
    {
        public abstract bool SendVk(ushort vk, bool down);
        public abstract void Dispose();

        protected static bool TryVkToScan(ushort vk, out ushort scan, out bool isExtended)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            scan = (ushort)MapVirtualKeyW(vk, 0u);
            isExtended = IsExtendedVk(vk);
            return scan != 0;
#else
            scan = 0;
            isExtended = false;
            return false;
#endif
        }

        protected static bool IsExtendedVk(ushort vk)
        {
            switch (vk)
            {
                case 0x5B:
                case 0x5C:
                case 0xA3:
                case 0xA5:
                case 0x21:
                case 0x22:
                case 0x23:
                case 0x24:
                case 0x25:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x2C:
                case 0x2D:
                case 0x2E:
                case 0x6F:
                case 0x90:
                    return true;
                default:
                    return false;
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
#endif
    }

    private sealed class NullBackend : KeyboardBackend
    {
        public override bool SendVk(ushort vk, bool down) { return false; }
        public override void Dispose() { }
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private sealed class SendInputBackend : KeyboardBackend
    {
        public int LastWin32Error { get; private set; }

        public override bool SendVk(ushort vk, bool down)
        {
            LastWin32Error = 0;

            if (!TryVkToScan(vk, out ushort scan, out bool isExtended))
                return false;

            INPUT input = default;
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = scan;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = UIntPtr.Zero;

            uint flags = KEYEVENTF_SCANCODE;
            if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
            if (!down) flags |= KEYEVENTF_KEYUP;

            input.U.ki.dwFlags = flags;

            uint sent = SendInput(1u, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 1u)
            {
                LastWin32Error = Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }

        public override void Dispose()
        {
        }

        private const uint INPUT_KEYBOARD = 1u;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001u;
        private const uint KEYEVENTF_KEYUP = 0x0002u;
        private const uint KEYEVENTF_SCANCODE = 0x0008u;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }
    }

    private sealed class InterceptionBackend : KeyboardBackend
    {
        public bool IsAvailable { get; private set; }

        private IntPtr context;
        private int keyboardDevice = -1;

        public InterceptionBackend()
        {
            try
            {
                context = interception_create_context();
                if (context == IntPtr.Zero)
                    return;

                for (int dev = 1; dev <= 20; dev++)
                {
                    if (interception_is_keyboard(dev) != 0)
                    {
                        keyboardDevice = dev;
                        break;
                    }
                }

                IsAvailable = keyboardDevice > 0;
            }
            catch
            {
                IsAvailable = false;
            }
        }

        public override bool SendVk(ushort vk, bool down)
        {
            if (!IsAvailable) return false;
            if (!TryVkToScan(vk, out ushort scan, out bool isExtended)) return false;

            ushort state = down ? (ushort)INTERCEPTION_KEY_DOWN : (ushort)INTERCEPTION_KEY_UP;
            if (isExtended) state = (ushort)(state | INTERCEPTION_KEY_E0);

            InterceptionStroke stroke = default;
            stroke.key.code = scan;
            stroke.key.state = state;
            stroke.key.information = 0;

            int res = interception_send(context, keyboardDevice, ref stroke, 1u);
            return res == 1;
        }

        public override void Dispose()
        {
            if (context != IntPtr.Zero)
            {
                interception_destroy_context(context);
                context = IntPtr.Zero;
            }
            IsAvailable = false;
        }

        private const ushort INTERCEPTION_KEY_DOWN = 0x00;
        private const ushort INTERCEPTION_KEY_UP = 0x01;
        private const ushort INTERCEPTION_KEY_E0 = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct InterceptionKeyStroke
        {
            public ushort code;
            public ushort state;
            public uint information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterceptionMouseStroke
        {
            public ushort state;
            public ushort flags;
            public short rolling;
            public int x;
            public int y;
            public uint information;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct InterceptionStroke
        {
            [FieldOffset(0)] public InterceptionKeyStroke key;
            [FieldOffset(0)] public InterceptionMouseStroke mouse;
        }

        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr interception_create_context();

        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void interception_destroy_context(IntPtr context);

        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int interception_send(IntPtr context, int device, ref InterceptionStroke stroke, uint nstroke);

        [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int interception_is_keyboard(int device);
    }
#endif
}
