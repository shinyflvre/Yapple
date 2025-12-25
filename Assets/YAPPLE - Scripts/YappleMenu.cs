using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public sealed class YappleMenu : MonoBehaviour
{
    [Serializable]
    public sealed class MenuElement
    {
        public string name;
        public List<Button> activeButtons = new List<Button>();
        public List<GameObject> active = new List<GameObject>();
        public List<GameObject> unactive = new List<GameObject>();
        public List<Button> backButtons = new List<Button>();
    }

    [Header("Menus")]
    [SerializeField] private List<MenuElement> menus = new List<MenuElement>();

    [Header("Start")]
    [SerializeField] private int openMenuOnStart = -1;
    [SerializeField] private bool closeAllOnStart = true;

    private int _openIndex = -1;

    private readonly Dictionary<Button, UnityAction> _openBindings = new Dictionary<Button, UnityAction>();
    private readonly Dictionary<Button, UnityAction> _backBindings = new Dictionary<Button, UnityAction>();

    public int OpenIndex => _openIndex;
    public IReadOnlyList<MenuElement> Menus => menus;

    private void Awake()
    {
        RebindButtons();
    }

    private void OnEnable()
    {
        RebindButtons();
    }

    private void OnDisable()
    {
        UnbindAll();
    }

    private void Start()
    {
        if (closeAllOnStart)
        {
            CloseAllImmediate();
        }

        if (openMenuOnStart >= 0)
        {
            OpenMenu(openMenuOnStart);
        }
        else
        {
            _openIndex = -1;
        }
    }

    public void RebindButtons()
    {
        UnbindAll();

        for (int i = 0; i < menus.Count; i++)
        {
            var m = menus[i];
            if (m == null)
            {
                continue;
            }

            BindActiveButtons(i, m.activeButtons);
            BindBackButtons(m.backButtons);
        }
    }

    public void OpenMenu(int index)
    {
        if (index < 0 || index >= menus.Count)
        {
            return;
        }

        if (_openIndex == index)
        {
            return;
        }

        if (_openIndex >= 0)
        {
            ApplyClosedState(_openIndex);
        }

        _openIndex = index;
        ApplyOpenState(_openIndex);
    }

    public void CloseMenu()
    {
        if (_openIndex < 0)
        {
            return;
        }

        ApplyClosedState(_openIndex);
        _openIndex = -1;
    }

    public void CloseAllImmediate()
    {
        for (int i = 0; i < menus.Count; i++)
        {
            ApplyClosedState(i);
        }
        _openIndex = -1;
    }

    public void OpenMenu(string menuName)
    {
        if (string.IsNullOrEmpty(menuName))
        {
            return;
        }

        for (int i = 0; i < menus.Count; i++)
        {
            var m = menus[i];
            if (m != null && string.Equals(m.name, menuName, StringComparison.OrdinalIgnoreCase))
            {
                OpenMenu(i);
                return;
            }
        }
    }

    private void BindActiveButtons(int index, List<Button> buttons)
    {
        if (buttons == null)
        {
            return;
        }

        for (int b = 0; b < buttons.Count; b++)
        {
            var btn = buttons[b];
            if (btn == null)
            {
                continue;
            }

            int captured = index;
            UnityAction action = () =>
            {
                if (_openIndex == captured)
                {
                    CloseMenu();
                }
                else
                {
                    OpenMenu(captured);
                }
            };

            btn.onClick.AddListener(action);
            _openBindings[btn] = action;
        }
    }

    private void BindBackButtons(List<Button> buttons)
    {
        if (buttons == null)
        {
            return;
        }

        for (int b = 0; b < buttons.Count; b++)
        {
            var btn = buttons[b];
            if (btn == null)
            {
                continue;
            }

            UnityAction action = CloseMenu;
            btn.onClick.AddListener(action);
            _backBindings[btn] = action;
        }
    }

    private void UnbindAll()
    {
        foreach (var kv in _openBindings)
        {
            if (kv.Key != null)
            {
                kv.Key.onClick.RemoveListener(kv.Value);
            }
        }
        _openBindings.Clear();

        foreach (var kv in _backBindings)
        {
            if (kv.Key != null)
            {
                kv.Key.onClick.RemoveListener(kv.Value);
            }
        }
        _backBindings.Clear();
    }

    private void ApplyOpenState(int index)
    {
        var m = menus[index];
        if (m == null)
        {
            return;
        }

        SetListActive(m.active, true);
        SetListActive(m.unactive, false);
    }

    private void ApplyClosedState(int index)
    {
        var m = menus[index];
        if (m == null)
        {
            return;
        }

        SetListActive(m.active, false);
        SetListActive(m.unactive, true);
    }

    private static void SetListActive(List<GameObject> list, bool state)
    {
        if (list == null)
        {
            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            var go = list[i];
            if (go == null)
            {
                continue;
            }

            if (go.activeSelf != state)
            {
                go.SetActive(state);
            }
        }
    }
}
