#if UNITY_STANDALONE || UNITY_EDITOR
using SFB;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    #region Batch export

    const string BatchLabel = "Batch Export";
    const string BatchCancelLabel = "Cancel Batch";
    const float BatchLoadTimeout = 300f; // seconds per costume (downloads can be slow in standalone mode)

    private Coroutine _batchCoroutine;
    private bool _batchCancel;
    private TMP_Text _batchLabel;

    private void Start()
    {
        CreateBatchExportButton();
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

    /// <summary>
    /// Clones the existing "Export Model" button and places the clone beside it,
    /// so no scene edits are needed.
    /// </summary>
    private void CreateBatchExportButton()
    {
        Button exportButton = GetComponentsInChildren<Button>(true).FirstOrDefault(b =>
        {
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                if (b.onClick.GetPersistentMethodName(i) == nameof(ExportModel)) return true;
            return false;
        });
        if (exportButton == null)
        {
            Debug.LogWarning("[BatchExport] Export Model button not found, batch button not created.");
            return;
        }

        var batchButton = Instantiate(exportButton, exportButton.transform.parent);
        batchButton.name = "BatchExportCostumes";
        batchButton.transform.SetSiblingIndex(exportButton.transform.GetSiblingIndex() + 1);
        batchButton.onClick = new Button.ButtonClickedEvent(); // drop the cloned persistent ExportModel call
        batchButton.onClick.AddListener(ToggleBatchExport);

        // Split the original button's slot in half: [Export Model][Batch Export]
        var src = (RectTransform)exportButton.transform;
        var dst = (RectTransform)batchButton.transform;
        const float gap = 4f;
        float width = src.sizeDelta.x;
        float half = (width - gap) / 2f;
        Vector2 pos = src.anchoredPosition;
        src.sizeDelta = dst.sizeDelta = new Vector2(half, src.sizeDelta.y);
        src.anchoredPosition = new Vector2(pos.x - (half + gap) / 2f, pos.y);
        dst.anchoredPosition = new Vector2(pos.x + (half + gap) / 2f, pos.y);

        foreach (var label in new[] { exportButton.GetComponentInChildren<TMP_Text>(true), batchButton.GetComponentInChildren<TMP_Text>(true) })
        {
            if (label == null) continue;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = label.fontSize;
        }

        _batchLabel = batchButton.GetComponentInChildren<TMP_Text>(true);
        if (_batchLabel) _batchLabel.text = BatchLabel;
    }

    public void ToggleBatchExport()
    {
        if (_batchCoroutine != null)
        {
            _batchCancel = true;
            UmaViewerUI.Instance.ShowMessage("Batch export will stop after the current costume", UIMessageType.Warning);
            return;
        }
        BatchExportCostumes();
    }

    /// <summary>
    /// Loads and exports every costume listed for the currently selected character,
    /// each into its own subfolder (textures are written next to the .pmx).
    /// </summary>
    public void BatchExportCostumes()
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        var ui = UmaViewerUI.Instance;
        var chara = ui.CostumeListChara;
        var list = ui.CostumeListRect;
        if (chara == null || list == null)
        {
            ui.ShowMessage("Select a character first", UIMessageType.Warning);
            return;
        }
        if (ui.LiveSelectPannel && ui.LiveSelectPannel.activeInHierarchy)
        {
            ui.ShowMessage("Close the live character select panel before batch exporting", UIMessageType.Warning);
            return;
        }

        var costumes = list.content.Cast<Transform>()
            .Select(t => t.GetComponent<UmaUIContainer>())
            .Where(c => c != null && c.Button != null && !string.IsNullOrEmpty(c.Id))
            .ToList();
        if (costumes.Count == 0)
        {
            ui.ShowMessage("No costumes listed for this character", UIMessageType.Warning);
            return;
        }

        var folders = StandaloneFileBrowser.OpenFolderPanel($"Batch export {costumes.Count} costumes to...", Config.Instance.MainPath, false);
        if (folders == null || folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;

        string root = Path.Combine(folders[0], SafeName($"{chara.Id}_{chara.GetName()}"));
        _batchCancel = false;
        _batchCoroutine = StartCoroutine(BatchExportRoutine(chara, costumes, root));
#else
        UmaViewerUI.Instance.ShowMessage("Not supported on this platform", UIMessageType.Warning);
#endif
    }

    private IEnumerator BatchExportRoutine(CharaEntry chara, List<UmaUIContainer> costumes, string root)
    {
        var ui = UmaViewerUI.Instance;
        if (_batchLabel) _batchLabel.text = BatchCancelLabel;
        int ok = 0;
        var failed = new List<string>();

        try
        {
            for (int i = 0; i < costumes.Count && !_batchCancel; i++)
            {
                var item = costumes[i];
                if (item == null) { failed.Add($"#{i + 1} (list changed)"); continue; }

                string costumeId = item.Id;
                string costumeName = item.Name;
                ui.ShowMessage($"Batch {i + 1}/{costumes.Count}: loading {costumeName}", UIMessageType.Default);

                // Another load in progress would make the click a no-op (PreLoadAndRun ignores it)
                while (UmaAssetManager.LoadCoroutine != null) yield return null;

                var previous = Builder.CurrentUMAContainer;
                string expectedName = $"Chara_{chara.Id}_{costumeId}";
                item.Button.onClick.Invoke(); // same load path as clicking the costume by hand

                float start = Time.realtimeSinceStartup;
                while (UmaAssetManager.LoadCoroutine != null ||
                       Builder.CurrentUMAContainer == null ||
                       Builder.CurrentUMAContainer == previous ||
                       Builder.CurrentUMAContainer.name != expectedName)
                {
                    if (Time.realtimeSinceStartup - start > BatchLoadTimeout) break;
                    yield return null;
                }

                var container = Builder.CurrentUMAContainer;
                if (container == null || container == previous || container.name != expectedName)
                {
                    failed.Add($"{costumeName} (load timed out)");
                    continue;
                }

                // Let the model's components run Start() and settle before exporting
                for (int f = 0; f < 3; f++) yield return null;
                yield return new WaitForEndOfFrame();

                try
                {
                    string dir = Path.Combine(root, SafeName($"{costumeId}_{costumeName}"));
                    Directory.CreateDirectory(dir);
                    ModelExporter.ExportModel(container, Path.Combine(dir, SafeName($"{chara.Id}_{costumeId}") + ".pmx"));
                    ok++;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    failed.Add($"{costumeName} ({e.Message})");
                }
            }
        }
        finally
        {
            _batchCoroutine = null;
            if (_batchLabel) _batchLabel.text = BatchLabel;
        }

        string summary = $"Batch export {(_batchCancel ? "cancelled" : "done")}: {ok}/{costumes.Count} saved to {root}";
        if (failed.Count > 0)
        {
            summary += $"\nFailed: {string.Join(", ", failed)}";
            Debug.LogWarning("[BatchExport] " + summary);
        }
        ui.ShowMessage(summary, failed.Count > 0 || _batchCancel ? UIMessageType.Warning : UIMessageType.Success);
        _batchCancel = false;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "unnamed" : cleaned;
    }

    #endregion
}
