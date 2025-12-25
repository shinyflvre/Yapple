using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleCommandItem : MonoBehaviour
{
    [SerializeField] private Button deleteButton;
    [SerializeField] private Button runButton;
    [SerializeField] public TMP_InputField commandInput;
    [SerializeField] public TMP_InputField wordInput;

    public Action<YappleCommandItem> OnDeleteRequested;
    public Action<YappleCommandItem> OnRunRequested;
    public Action<YappleCommandItem> OnChanged;

    public string Command => commandInput != null ? commandInput.text : string.Empty;
    public string Word => wordInput != null ? wordInput.text : string.Empty;

    private void OnEnable()
    {
        if (deleteButton != null)
            deleteButton.onClick.AddListener(DeleteClicked);

        if (runButton != null)
            runButton.onClick.AddListener(RunClicked);

        if (commandInput != null)
            commandInput.onValueChanged.AddListener(Changed);

        if (wordInput != null)
            wordInput.onValueChanged.AddListener(Changed);
    }

    private void OnDisable()
    {
        if (deleteButton != null)
            deleteButton.onClick.RemoveListener(DeleteClicked);

        if (runButton != null)
            runButton.onClick.RemoveListener(RunClicked);

        if (commandInput != null)
            commandInput.onValueChanged.RemoveListener(Changed);

        if (wordInput != null)
            wordInput.onValueChanged.RemoveListener(Changed);
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
        OnChanged?.Invoke(this);
    }
}