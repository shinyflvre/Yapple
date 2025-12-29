using System.Collections.Generic;
using UnityEngine;

public sealed class YappleMoveHandle : MonoBehaviour
{
    [Header("Hover Area")]
    [SerializeField] private RectTransform hoverArea;
    [SerializeField] private Canvas canvasInput;
    [SerializeField] private bool requireFocus = true;

    [Header("Targets")]
    [SerializeField] private List<GameObject> targets = new List<GameObject>();
    [SerializeField] private bool disableTargetsOnStart = true;

    private bool _armedDrag;
    private bool _lastActive;

    private void Awake()
    {
        if (hoverArea == null)
        {
            hoverArea = transform as RectTransform;
        }
    }

    private void Start()
    {
        if (disableTargetsOnStart)
        {
            SetTargets(false);
            _lastActive = false;
        }
        else
        {
            _lastActive = GetAnyTargetActive();
        }
    }

    private void Update()
    {
        if (requireFocus && !Application.isFocused)
        {
            _armedDrag = false;
            Apply(false);
            return;
        }

        bool inside = IsMouseInsideHoverArea();

        bool mouseHeld = Input.GetMouseButton(0);
        if (!_armedDrag && mouseHeld && inside)
        {
            _armedDrag = true;
        }
        else if (_armedDrag && !mouseHeld)
        {
            _armedDrag = false;
        }

        bool active = inside || _armedDrag;
        Apply(active);
    }

    private void OnDisable()
    {
        _armedDrag = false;
        Apply(false);
    }

    private void Apply(bool active)
    {
        if (active == _lastActive)
        {
            return;
        }

        _lastActive = active;
        SetTargets(active);
    }

    private bool IsMouseInsideHoverArea()
    {
        if (hoverArea == null)
        {
            return false;
        }

        Canvas c = canvasInput != null ? canvasInput : hoverArea.GetComponentInParent<Canvas>();
        Camera cam = c != null ? c.worldCamera : null;

        return RectTransformUtility.RectangleContainsScreenPoint(hoverArea, Input.mousePosition, cam);
    }

    private void SetTargets(bool state)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            GameObject go = targets[i];
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

    private bool GetAnyTargetActive()
    {
        for (int i = 0; i < targets.Count; i++)
        {
            GameObject go = targets[i];
            if (go != null && go.activeSelf)
            {
                return true;
            }
        }
        return false;
    }
}
