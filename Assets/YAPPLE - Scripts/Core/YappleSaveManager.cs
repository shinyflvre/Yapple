using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using JsonFormatting = Newtonsoft.Json.Formatting;

public sealed class YappleSaveManager : MonoBehaviour
{
    [Serializable]
    private sealed class Entry
    {
        public string type;
        public string word;
        public string filePath;
        public string command;
        public string oscCommand;
        public float volume = 100f;
        public int[] keys;
    }

    [Serializable]
    private sealed class Data
    {
        public int version = 4;
        public List<Entry> entries = new List<Entry>();
    }

    [Serializable]
    private sealed class LegacySoundEntry { public string word; public string filePath; }

    [Serializable]
    private sealed class LegacyCommandEntry { public string word; public string command; }

    [Serializable]
    private sealed class LegacyOscEntry { public string word; public string oscCommand; }

    [Header("Refs")]
    [SerializeField] private MonoBehaviour yappleCore;
    [SerializeField] private YappleCommands commandHandler;
    [SerializeField] private YappleOSC oscHandler;
    [SerializeField] private YappleKeyBinds keybindHandler;

    [Header("Content Root (same for all)")]
    [SerializeField] private Transform content;

    [Header("Behavior")]
    [SerializeField] private bool loadOnStart = true;
    [SerializeField] private bool clearUiBeforeLoad = true;

    [SerializeField] private Transform pinnedTopRow;

    private MethodInfo miCreateItemRow;
    private MethodInfo miLoadAudioIntoItem;
    private MethodInfo miStopListening;
    private MethodInfo miStartListening;

    private MethodInfo miAddCommandClicked;
    private MethodInfo miCommandsRebuildMap;
    private MethodInfo miCommandsRequestRebuild;

    private MethodInfo miAddOscClicked;
    private MethodInfo miOscRebuildMap;

    private FieldInfo fiOscCore;
    private MethodInfo miCoreRequestRebuildFromOsc;

    private MethodInfo miAddKeybindClicked;

    private FieldInfo fiKeybindWordInput;
    private FieldInfo fiKeybindKeys;
    private MethodInfo miKeybindSetKeysUI;
    private MethodInfo miKeybindSetInfoIdle;

    private bool isLoading;
    private int lastChildCount = -1;
    private int lastOrderHash = 0;
    private int lastKeybindKeysHash = 0;
    private readonly HashSet<int> boundInputs = new HashSet<int>();

    private const string TypeSound = "sound";
    private const string TypeCmd = "cmd";
    private const string TypeOsc = "osc";
    private const string TypeKeybind = "kb";

    private void Awake()
    {
        CacheMethods();
    }

    private void Start()
    {
        if (loadOnStart)
            StartCoroutine(LoadRoutine());
    }

    private void Update()
    {
        if (isLoading || content == null)
            return;

        int cc = content.childCount;
        int oh = CalcOrderHash(content);
        int kh = CalcKeybindKeysHash(content);

        bool changed = cc != lastChildCount || oh != lastOrderHash || kh != lastKeybindKeysHash;

        if (changed)
        {
            lastChildCount = cc;
            lastOrderHash = oh;
            lastKeybindKeysHash = kh;
            BindEndEdit();
            SaveNow();
        }
    }

    private void OnApplicationQuit()
    {
        SaveNow();
    }

    public void SaveNow()
    {
        if (isLoading || content == null)
            return;

        try
        {
            var data = new Data();

            for (int i = 0; i < content.childCount; i++)
            {
                var row = content.GetChild(i);
                if (row == null)
                    continue;

                var sound = row.GetComponentInChildren<YappleSoundItem>(true);
                if (sound != null && !string.IsNullOrWhiteSpace(sound.FilePath))
                {
                    data.entries.Add(new Entry
                    {
                        type = TypeSound,
                        word = sound.Word ?? string.Empty,
                        filePath = sound.FilePath,
                        volume = sound.VolumePercent
                    });
                    continue;
                }

                var cmd = row.GetComponentInChildren<YappleCommandItem>(true);
                if (cmd != null)
                {
                    string w = cmd.Word;
                    if (string.IsNullOrWhiteSpace(w) && cmd.wordInput != null)
                        w = cmd.wordInput.text;

                    string c = cmd.Command;
                    if (string.IsNullOrWhiteSpace(c) && cmd.commandInput != null)
                        c = cmd.commandInput.text;

                    if (!string.IsNullOrWhiteSpace(c))
                    {
                        data.entries.Add(new Entry
                        {
                            type = TypeCmd,
                            word = w ?? string.Empty,
                            command = c ?? string.Empty
                        });
                    }
                    continue;
                }

                var osc = row.GetComponentInChildren<YappleOSCItem>(true);
                if (osc != null)
                {
                    string w = osc.wordInput != null ? osc.wordInput.text : string.Empty;
                    string o = osc.oscCommandInput != null ? osc.oscCommandInput.text : string.Empty;

                    if (!string.IsNullOrWhiteSpace(o))
                    {
                        data.entries.Add(new Entry
                        {
                            type = TypeOsc,
                            word = w ?? string.Empty,
                            oscCommand = o ?? string.Empty
                        });
                    }
                    continue;
                }

                var kb = row.GetComponentInChildren<YappleKeyBindItem>(true);
                if (kb != null && kb.HasKeys())
                {
                    string w = GetKeybindWord(kb);
                    var k = kb.GetKeys();
                    if (k != null && k.Length > 0)
                    {
                        var arr = new int[Mathf.Min(3, k.Length)];
                        int n = 0;
                        for (int j = 0; j < k.Length && n < 3; j++)
                            arr[n++] = (int)k[j];

                        data.entries.Add(new Entry
                        {
                            type = TypeKeybind,
                            word = w ?? string.Empty,
                            keys = arr
                        });
                    }
                }
            }

            string json = JsonConvert.SerializeObject(data, JsonFormatting.Indented);
            File.WriteAllText(GetSavePath(), json);
        }
        catch (Exception e)
        {
            Debug.LogError("Save failed: " + e);
        }
    }

    public void LoadNow()
    {
        StartCoroutine(LoadRoutine());
    }

    private IEnumerator LoadRoutine()
    {
        isLoading = true;

        yield return null;

        var data = ReadData();

        InvokeNoArgs(miStopListening);

        if (pinnedTopRow != null)
            pinnedTopRow.SetAsFirstSibling();

        int pinnedCount = pinnedTopRow != null ? 1 : 0;

        if (clearUiBeforeLoad && content != null)
            ClearChildren(content);

        boundInputs.Clear();

        if (content == null)
        {
            isLoading = false;
            yield break;
        }

        for (int i = 0; i < data.entries.Count; i++)
        {
            var e = data.entries[i];
            if (e == null)
                continue;

            string t = (e.type ?? string.Empty).Trim().ToLowerInvariant();

            if (t == TypeSound)
            {
                if (string.IsNullOrWhiteSpace(e.filePath))
                    continue;

                var item = CreateSoundItemRow(e.filePath);
                if (item == null)
                    continue;

                SetSoundWord(item, e.word);
                item.SetVolumePercent(e.volume);

                var row = GetDirectChild(item.transform, content);
                if (row != null)
                    row.SetSiblingIndex(Mathf.Clamp(pinnedCount + i, 0, content.childCount - 1));

                yield return StartCoroutine(LoadAudio(e.filePath, item));
                continue;
            }

            if (t == TypeCmd)
            {
                if (string.IsNullOrWhiteSpace(e.command))
                    continue;

                var item = CreateCommandItem();
                if (item == null)
                    continue;

                SetCommand(item, e.word, e.command);

                var row = GetDirectChild(item.transform, content);
                if (row != null)
                    row.SetSiblingIndex(Mathf.Clamp(pinnedCount + i, 0, content.childCount - 1));

                continue;
            }

            if (t == TypeOsc)
            {
                if (string.IsNullOrWhiteSpace(e.oscCommand))
                    continue;

                var item = CreateOscItem();
                if (item == null)
                    continue;

                SetOsc(item, e.word, e.oscCommand);

                var row = GetDirectChild(item.transform, content);
                if (row != null)
                    row.SetSiblingIndex(Mathf.Clamp(pinnedCount + i, 0, content.childCount - 1));

                continue;
            }

            if (t == TypeKeybind)
            {
                if (e.keys == null || e.keys.Length == 0)
                    continue;

                var item = CreateKeybindItem();
                if (item == null)
                    continue;

                SetKeybind(item, e.word, e.keys);

                var row = GetDirectChild(item.transform, content);
                if (row != null)
                    row.SetSiblingIndex(Mathf.Clamp(pinnedCount + i, 0, content.childCount - 1));

                continue;
            }
        }

        BindEndEdit();
        lastChildCount = content.childCount;
        lastOrderHash = CalcOrderHash(content);
        lastKeybindKeysHash = CalcKeybindKeysHash(content);

        isLoading = false;

        InvokeNoArgs(miCommandsRebuildMap);
        InvokeNoArgs(miCommandsRequestRebuild);

        InvokeNoArgs(miOscRebuildMap);
        InvokeCoreRequestRebuildFromOsc();

        if (keybindHandler != null)
            keybindHandler.NotifyItemChanged();

        InvokeNoArgs(miStartListening);
    }

    private static int CalcOrderHash(Transform t)
    {
        unchecked
        {
            int h = 17;
            if (t == null)
                return h;

            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                h = h * 31 + (c != null ? c.GetInstanceID() : 0);
            }
            return h;
        }
    }

    private static int CalcKeybindKeysHash(Transform t)
    {
        unchecked
        {
            int h = 17;
            if (t == null)
                return h;

            for (int i = 0; i < t.childCount; i++)
            {
                var row = t.GetChild(i);
                if (row == null)
                    continue;

                var kb = row.GetComponentInChildren<YappleKeyBindItem>(true);
                if (kb == null)
                    continue;

                var keys = kb.GetKeys();
                if (keys == null)
                {
                    h = h * 31;
                    continue;
                }

                h = h * 31 + keys.Length;
                for (int k = 0; k < keys.Length; k++)
                    h = h * 31 + (int)keys[k];
            }

            return h;
        }
    }

    private void BindEndEdit()
    {
        if (content == null)
            return;

        var inputs = content.GetComponentsInChildren<TMP_InputField>(true);
        for (int i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i];
            if (input == null)
                continue;

            int id = input.GetInstanceID();
            if (boundInputs.Contains(id))
                continue;

            input.onEndEdit.AddListener(_ =>
            {
                if (!isLoading)
                    SaveNow();
            });

            boundInputs.Add(id);
        }

        var sliders = content.GetComponentsInChildren<Slider>(true);
        for (int i = 0; i < sliders.Length; i++)
        {
            var s = sliders[i];
            if (s == null)
                continue;

            int id = s.GetInstanceID();
            if (boundInputs.Contains(id))
                continue;

            s.onValueChanged.AddListener(_ =>
            {
                if (!isLoading)
                    SaveNow();
            });

            boundInputs.Add(id);
        }
    }

    private void ClearChildren(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var c = t.GetChild(i);
            if (c == null)
                continue;

            if (pinnedTopRow != null && c == pinnedTopRow)
                continue;

            Destroy(c.gameObject);
        }
    }

    private static Transform GetDirectChild(Transform from, Transform parent)
    {
        if (from == null || parent == null)
            return null;

        Transform cur = from;
        while (cur != null && cur.parent != parent)
            cur = cur.parent;

        return cur != null && cur.parent == parent ? cur : null;
    }

    private string GetSavePath()
    {
        string dir = GetLocalLowYappleDir();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static string GetLocalLowYappleDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Path.Combine(Application.persistentDataPath, "Yapple");

        return Path.GetFullPath(Path.Combine(local, "..", "LocalLow", "Yapple"));
    }

    private Data ReadData()
    {
        string path = GetSavePath();
        if (!File.Exists(path))
            return new Data();

        string json;
        try { json = File.ReadAllText(path); }
        catch { return new Data(); }

        if (string.IsNullOrWhiteSpace(json))
            return new Data();

        try
        {
            var root = JsonConvert.DeserializeObject<JObject>(json);
            if (root == null)
                return new Data();

            var entriesTok = root["entries"];
            if (entriesTok != null && entriesTok.Type == JTokenType.Array)
            {
                var d = new Data();
                var list = entriesTok.ToObject<List<Entry>>();
                if (list != null)
                    d.entries = list;
                return d;
            }

            var d2 = new Data();
            var s = root["sounds"] != null ? root["sounds"].ToObject<List<LegacySoundEntry>>() : null;
            var c = root["commands"] != null ? root["commands"].ToObject<List<LegacyCommandEntry>>() : null;
            var o = root["oscs"] != null ? root["oscs"].ToObject<List<LegacyOscEntry>>() : null;

            if (s != null)
            {
                for (int i = 0; i < s.Count; i++)
                {
                    var e = s[i];
                    if (e != null && !string.IsNullOrWhiteSpace(e.filePath))
                        d2.entries.Add(new Entry { type = TypeSound, word = e.word ?? string.Empty, filePath = e.filePath });
                }
            }

            if (c != null)
            {
                for (int i = 0; i < c.Count; i++)
                {
                    var e = c[i];
                    if (e != null && !string.IsNullOrWhiteSpace(e.command))
                        d2.entries.Add(new Entry { type = TypeCmd, word = e.word ?? string.Empty, command = e.command });
                }
            }

            if (o != null)
            {
                for (int i = 0; i < o.Count; i++)
                {
                    var e = o[i];
                    if (e != null && !string.IsNullOrWhiteSpace(e.oscCommand))
                        d2.entries.Add(new Entry { type = TypeOsc, word = e.word ?? string.Empty, oscCommand = e.oscCommand });
                }
            }

            return d2;
        }
        catch
        {
            try
            {
                var d = JsonConvert.DeserializeObject<Data>(json);
                return d ?? new Data();
            }
            catch
            {
                return new Data();
            }
        }
    }

    private void CacheMethods()
    {
        if (yappleCore != null)
        {
            var t = yappleCore.GetType();
            miCreateItemRow = t.GetMethod("CreateItemRow", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miLoadAudioIntoItem = t.GetMethod("LoadAudioIntoItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miStopListening = t.GetMethod("StopListening", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miStartListening = t.GetMethod("StartListening", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (commandHandler != null)
        {
            var t = commandHandler.GetType();
            miAddCommandClicked = t.GetMethod("AddCommandClicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miCommandsRebuildMap = t.GetMethod("RebuildMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miCommandsRequestRebuild = t.GetMethod("RequestRebuild", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (oscHandler != null)
        {
            var t = oscHandler.GetType();
            miAddOscClicked = t.GetMethod("AddOscClicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            miOscRebuildMap = t.GetMethod("RebuildMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            fiOscCore = t.GetField("core", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (keybindHandler != null)
        {
            var t = keybindHandler.GetType();
            miAddKeybindClicked = t.GetMethod("AddKeybind", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        var kbType = typeof(YappleKeyBindItem);
        fiKeybindWordInput = kbType.GetField("wordInput", BindingFlags.Instance | BindingFlags.NonPublic);
        fiKeybindKeys = kbType.GetField("keys", BindingFlags.Instance | BindingFlags.NonPublic);
        miKeybindSetKeysUI = kbType.GetMethod("SetKeysUI", BindingFlags.Instance | BindingFlags.NonPublic);
        miKeybindSetInfoIdle = kbType.GetMethod("SetInfoIdle", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private void InvokeNoArgs(MethodInfo mi)
    {
        if (mi == null)
            return;

        object target = null;

        if (mi == miAddCommandClicked || mi == miCommandsRebuildMap || mi == miCommandsRequestRebuild)
            target = commandHandler;
        else if (mi == miAddOscClicked || mi == miOscRebuildMap)
            target = oscHandler;
        else
            target = yappleCore;

        if (target == null)
            return;

        var pars = mi.GetParameters();
        if (pars != null && pars.Length != 0)
            return;

        try { mi.Invoke(target, null); } catch { }
    }

    private void InvokeCoreRequestRebuildFromOsc()
    {
        if (oscHandler == null || fiOscCore == null)
            return;

        object coreObj;
        try { coreObj = fiOscCore.GetValue(oscHandler); } catch { return; }
        if (coreObj == null)
            return;

        if (miCoreRequestRebuildFromOsc == null)
            miCoreRequestRebuildFromOsc = coreObj.GetType().GetMethod("RequestRebuild", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (miCoreRequestRebuildFromOsc == null)
            return;

        var pars = miCoreRequestRebuildFromOsc.GetParameters();
        if (pars != null && pars.Length != 0)
            return;

        try { miCoreRequestRebuildFromOsc.Invoke(coreObj, null); } catch { }
    }

    private YappleSoundItem CreateSoundItemRow(string filePath)
    {
        if (yappleCore == null || miCreateItemRow == null)
            return null;

        try
        {
            object obj = miCreateItemRow.Invoke(yappleCore, new object[] { filePath });
            return obj as YappleSoundItem;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerator LoadAudio(string filePath, YappleSoundItem item)
    {
        if (yappleCore == null || miLoadAudioIntoItem == null)
            yield break;

        object obj;
        try { obj = miLoadAudioIntoItem.Invoke(yappleCore, new object[] { filePath, item }); }
        catch { yield break; }

        var en = obj as IEnumerator;
        if (en != null)
            yield return StartCoroutine(en);
        else
            yield return null;
    }

    private YappleCommandItem CreateCommandItem()
    {
        if (commandHandler == null || miAddCommandClicked == null || content == null)
            return null;

        int before = content.childCount;
        try { miAddCommandClicked.Invoke(commandHandler, null); } catch { return null; }

        for (int i = content.childCount - 1; i >= 0 && i >= before - 1; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleCommandItem>(true);
            if (item != null)
                return item;
        }

        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleCommandItem>(true);
            if (item != null)
                return item;
        }

        return null;
    }

    private YappleOSCItem CreateOscItem()
    {
        if (oscHandler == null || miAddOscClicked == null || content == null)
            return null;

        int before = content.childCount;
        try { miAddOscClicked.Invoke(oscHandler, null); } catch { return null; }

        for (int i = content.childCount - 1; i >= 0 && i >= before - 1; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleOSCItem>(true);
            if (item != null)
                return item;
        }

        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleOSCItem>(true);
            if (item != null)
                return item;
        }

        return null;
    }

    private YappleKeyBindItem CreateKeybindItem()
    {
        if (keybindHandler == null || miAddKeybindClicked == null || content == null)
            return null;

        int before = content.childCount;
        try { miAddKeybindClicked.Invoke(keybindHandler, null); } catch { return null; }

        for (int i = content.childCount - 1; i >= 0 && i >= before - 1; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleKeyBindItem>(true);
            if (item != null)
                return item;
        }

        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var row = content.GetChild(i);
            if (row == null)
                continue;

            var item = row.GetComponentInChildren<YappleKeyBindItem>(true);
            if (item != null)
                return item;
        }

        return null;
    }

    private static void SetSoundWord(YappleSoundItem item, string word)
    {
        if (item == null)
            return;

        var input = item.GetComponentInChildren<TMP_InputField>(true);
        if (input == null)
            return;

        input.SetTextWithoutNotify(word ?? string.Empty);
        input.ForceLabelUpdate();
    }

    private static void SetCommand(YappleCommandItem item, string word, string command)
    {
        if (item == null)
            return;

        if (item.wordInput != null)
        {
            item.wordInput.SetTextWithoutNotify(word ?? string.Empty);
            item.wordInput.ForceLabelUpdate();
        }

        if (item.commandInput != null)
        {
            item.commandInput.SetTextWithoutNotify(command ?? string.Empty);
            item.commandInput.ForceLabelUpdate();
        }
    }

    private static void SetOsc(YappleOSCItem item, string word, string oscCommand)
    {
        if (item == null)
            return;

        if (item.wordInput != null)
        {
            item.wordInput.SetTextWithoutNotify(word ?? string.Empty);
            item.wordInput.ForceLabelUpdate();
        }

        if (item.oscCommandInput != null)
        {
            item.oscCommandInput.SetTextWithoutNotify(oscCommand ?? string.Empty);
            item.oscCommandInput.ForceLabelUpdate();
        }
    }

    private string GetKeybindWord(YappleKeyBindItem item)
    {
        if (item == null)
            return string.Empty;

        if (fiKeybindWordInput != null)
        {
            try
            {
                var input = fiKeybindWordInput.GetValue(item) as TMP_InputField;
                if (input != null)
                    return input.text ?? string.Empty;
            }
            catch
            {
            }
        }

        return item.GetNormalizedWord();
    }

    private void SetKeybind(YappleKeyBindItem item, string word, int[] keys)
    {
        if (item == null)
            return;

        if (fiKeybindWordInput != null)
        {
            try
            {
                var input = fiKeybindWordInput.GetValue(item) as TMP_InputField;
                if (input != null)
                {
                    input.SetTextWithoutNotify(word ?? string.Empty);
                    input.ForceLabelUpdate();
                }
            }
            catch
            {
            }
        }

        if (fiKeybindKeys != null && keys != null)
        {
            try
            {
                var list = fiKeybindKeys.GetValue(item) as List<KeyCode>;
                if (list != null)
                {
                    list.Clear();
                    for (int i = 0; i < keys.Length && list.Count < 3; i++)
                    {
                        var kc = (KeyCode)keys[i];
                        if (keybindHandler != null && !keybindHandler.IsKeyAllowedForCapture(kc))
                            continue;

                        if (!list.Contains(kc))
                            list.Add(kc);
                    }
                }
            }
            catch
            {
            }
        }

        if (miKeybindSetKeysUI != null)
        {
            try { miKeybindSetKeysUI.Invoke(item, null); } catch { }
        }

        if (miKeybindSetInfoIdle != null)
        {
            try { miKeybindSetInfoIdle.Invoke(item, null); } catch { }
        }
    }
}
