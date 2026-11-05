#if UNITY_STANDALONE || UNITY_EDITOR
using SFB;
#endif

using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UISettingsModel : MonoBehaviour
{
    static UmaViewerBuilder Builder => UmaViewerBuilder.Instance;

    [SerializeField] private Toggle _lockCharacter;
    [SerializeField] private Toggle _openWithTPose;
    [SerializeField] private Toggle _enablePhysics;
    [SerializeField] private Toggle _lookAtCamera;
    [SerializeField] private Toggle _faceOverride;
    [SerializeField] private Slider _outlineWidthSlider;

    public ScrollRect MaterialsList;

    private bool
        _isHeadFix,
        _isTPose,
        _dynamicBoneEnable = true,
        _enableEyeTracking = true,
        _enableFaceOverride = true;

    private float _outlineWidth;

    public bool IsHeadFix
    {
        get { return _isHeadFix; }
        set { _lockCharacter.SetIsOnWithoutNotify(value); SetHeadFix(value); }
    }

    public bool IsTPose
    {
        get { return _isTPose; }
        set { _openWithTPose.SetIsOnWithoutNotify(value); SetTPose(value); }
    }

    public bool DynamicBoneEnable
    {
        get { return _dynamicBoneEnable; }
        set { _enablePhysics.SetIsOnWithoutNotify(value); SetDynamicBoneEnable(value); }
    }

    public bool EnableEyeTracking
    {
        get { return _enableEyeTracking; }
        set { _lookAtCamera.SetIsOnWithoutNotify(value); SetEyeTrackingEnable(value); }
    }

    public bool EnableFaceOverride
    {
        get { return _enableFaceOverride; }
        set { _faceOverride.SetIsOnWithoutNotify(value); SetFaceOverrideEnable(value); }
    }

    public float OutlineWidth
    {
        get { return _outlineWidth; }
        set { _outlineWidthSlider.value = value; }
    }

    public void SetHeadFix(bool value)
    {
        _isHeadFix = value;
    }

    public void SetTPose(bool value)
    {
        _isTPose = value;
    }

    public void SetDynamicBoneEnable(bool isOn)
    {
        _dynamicBoneEnable = isOn;
        Builder.CurrentUMAContainer?.SetDynamicBoneEnable(isOn);
    }

    public void SetEyeTrackingEnable(bool isOn)
    {
        _enableEyeTracking = isOn;
        Builder.CurrentUMAContainer?.SetEyeTracking(isOn);
    }

    public void SetFaceOverrideEnable(bool isOn)
    {
        _enableFaceOverride = isOn;
        Builder.CurrentUMAContainer?.SetFaceOverrideData(isOn);
    }

    public void ChangeOutlineWidth(float val)
    {
        _outlineWidth = val;
        Shader.SetGlobalFloat("_GlobalOutlineWidth", val);
    }

    public void ExportModel()
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        var container = Builder.CurrentUMAContainer;
        if (container)
        {
            var entry = container.CharaEntry;
            var path = StandaloneFileBrowser.SaveFilePanel("Save PMX File", Config.Instance.MainPath, $"{entry.Id}_{entry.GetName()}", "pmx");
            if (!string.IsNullOrEmpty(path))
            {
                ModelExporter.ExportModel(container, path);
            }
        }

        var prop_container = Builder.CurrentOtherContainer;
        if (prop_container)
        {
            var path = StandaloneFileBrowser.SaveFilePanel("Save PMX File", Config.Instance.MainPath, $"{prop_container}", "pmx");
            if (!string.IsNullOrEmpty(path))
            {
                ModelExporter.ExportModel(prop_container, path);
            }
        }

#else
        UmaViewerUI.Instance.ShowMessage("Not supported on this platform", UIMessageType.Warning);
#endif
    }

    #region MMD bone names toggle

    private void Start()
    {
        CreateMmdBoneNamesToggle();
    }

    public void SetMmdBoneNames(bool isOn)
    {
        if (Config.Instance == null || Config.Instance.PmxMmdBoneNames == isOn) return;
        Config.Instance.PmxMmdBoneNames = isOn;
        Config.Instance.UpdateConfig(false);
    }

    /// <summary>
    /// Clones the "Face Override" row into a new "MMD Bone Names" row right below it,
    /// and moves the rows under it down to make room (the panel has no layout group).
    /// </summary>
    private void CreateMmdBoneNamesToggle()
    {
        if (_faceOverride == null) return;
        var srcRow = _faceOverride.transform.parent as RectTransform;
        if (srcRow == null || srcRow.parent == null) return;
        var panel = srcRow.parent;
        float shift = srcRow.sizeDelta.y;

        var row = (RectTransform)Instantiate(srcRow.gameObject, panel).transform;
        row.name = "MmdBoneNames";
        row.SetSiblingIndex(srcRow.GetSiblingIndex() + 1);
        row.anchoredPosition = srcRow.anchoredPosition - new Vector2(0, shift);

        // Move everything below the new row down; shrink the materials list to keep the panel height
        for (int i = row.GetSiblingIndex() + 1; i < panel.childCount; i++)
        {
            var sib = (RectTransform)panel.GetChild(i);
            sib.anchoredPosition -= new Vector2(0, shift);
            if (MaterialsList && MaterialsList.transform.IsChildOf(sib) && sib.sizeDelta.y > shift * 2)
                sib.sizeDelta -= new Vector2(0, shift);
        }

        var toggle = row.GetComponentInChildren<Toggle>(true);
        toggle.onValueChanged = new Toggle.ToggleEvent(); // drop the cloned persistent SetFaceOverrideEnable call
        toggle.SetIsOnWithoutNotify(Config.Instance?.PmxMmdBoneNames ?? true);
        toggle.onValueChanged.AddListener(SetMmdBoneNames);

        var label = row.GetComponentInChildren<TMP_Text>(true);
        if (label)
        {
            label.text = "MMD Bone Names";
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = label.fontSize;
        }
    }

    #endregion
}
