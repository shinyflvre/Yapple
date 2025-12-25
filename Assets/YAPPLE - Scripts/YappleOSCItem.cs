using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class YappleOSCItem : MonoBehaviour
{
    [SerializeField] private Button deleteButton;
    [SerializeField] private Button runButton;
    [SerializeField] public TMP_InputField oscCommandInput;
    [SerializeField] public TMP_InputField wordInput;

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
    }

    private void OnDisable()
    {
        if (deleteButton != null)
            deleteButton.onClick.RemoveListener(DeleteClicked);

        if (runButton != null)
            runButton.onClick.RemoveListener(RunClicked);

        if (oscCommandInput != null)
            oscCommandInput.onValueChanged.RemoveListener(Changed);

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
