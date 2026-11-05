using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a "Record All" button beside "Record VMD" in the animation tab. It records every motion
/// in the animation list shown now (the animation set picked in the menu, e.g. "Generic" or a
/// character), on the loaded character, one VMD per motion.
/// In Generic, a search typed in the list's search box narrows it the same way.
/// Files land in VMDRecords/Batch_&lt;set&gt;_&lt;date&gt;/&lt;motion&gt;.vmd.
/// If the newest batch folder of the set is unfinished, the button resumes it instead
/// (motions that already have a VMD there are skipped, and so are motions an earlier run of
/// that folder could not record: they are listed in its skipped.txt).
///
/// Every motion is recorded from its first frame (a loop from the start of a cycle, with no
/// blend from the previous motion) to its end, every frame at 30 fps: length x 30 + 1 frames,
/// so a loop's last frame is its first. The first frame is the motion's own (not the T-pose
/// the recorder measures the rest in), and eye tracking is off while it records, so the head
/// isn't turned toward the camera.
/// </summary>
public class BatchVMDRecorder : MonoBehaviour
{
    const float SettleTimeout = 10f;
    const string IdleLabel = "Record All";

    Button vmdButton;
    Button batchButton;
    TMP_Text batchLabel;

    bool running;
    bool cancel;
    string progressLabel;

    /// <summary>Clones <paramref name="recordVmdButton"/> and splits its slot: [Record VMD][Record All].</summary>
    public void Attach(Button recordVmdButton)
    {
        if (batchButton || !recordVmdButton) return;
        vmdButton = recordVmdButton;

        batchButton = Instantiate(vmdButton, vmdButton.transform.parent);
        batchButton.name = "RecordAllVMD";
        batchButton.transform.SetSiblingIndex(vmdButton.transform.GetSiblingIndex() + 1);
        batchButton.onClick = new Button.ButtonClickedEvent(); // drop the cloned RecordVMD call
        batchButton.onClick.AddListener(OnClick);

        var src = (RectTransform)vmdButton.transform;
        var dst = (RectTransform)batchButton.transform;
        const float gap = 4f;
        float half = (src.sizeDelta.x - gap) / 2f;
        Vector2 pos = src.anchoredPosition;
        src.sizeDelta = dst.sizeDelta = new Vector2(half, src.sizeDelta.y);
        src.anchoredPosition = new Vector2(pos.x - (half + gap) / 2f, pos.y);
        dst.anchoredPosition = new Vector2(pos.x + (half + gap) / 2f, pos.y);

        foreach (var label in new[] { vmdButton.GetComponentInChildren<TMP_Text>(true), batchButton.GetComponentInChildren<TMP_Text>(true) })
        {
            if (label == null) continue;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = label.fontSize;
        }

        batchLabel = batchButton.GetComponentInChildren<TMP_Text>(true);
        if (batchLabel) batchLabel.text = IdleLabel;
    }

    static string SetName(UmaViewerUI ui) =>
        ui.ListedAnimationSet == -1 ? "Generic" :
        ui.ListedAnimationSet == -2 ? "Tail" :
        ui.ListedAnimationSet == -3 ? "Ear" :
        $"chr{ui.ListedAnimationSet}";

    /// <summary>The body motions of the list shown now, or null with the reason in <paramref name="why"/>.</summary>
    static List<UmaDatabaseEntry> ListedMotions(out string why)
    {
        why = null;
        var ui = UmaViewerUI.Instance;
        if (ui == null || ui.ListedAnimations == null || ui.ListedAnimations.Count == 0)
        {
            why = "Pick an animation set in the animation menu first";
            return null;
        }
        if (ui.ListedAnimationsMini)
        {
            why = "That's a mini set; the batch recorder needs a normal character";
            return null;
        }
        if (ui.ListedAnimationSet == -2 || ui.ListedAnimationSet == -3)
        {
            why = "Tail and Ear sets don't move the body; pick another set";
            return null;
        }

        IEnumerable<UmaDatabaseEntry> motions = ui.ListedAnimations;
        var search = ui.AnimationPageCtrl && ui.AnimationPageCtrl.SearchText ? ui.AnimationPageCtrl.SearchText.text : "";
        if (ui.ListedAnimationSet == -1 && !string.IsNullOrEmpty(search))
        {
            motions = motions.Where(m => Path.GetFileName(m.Name).Contains(search));
        }
        // Only body clips play as a motion; the others go to their own layers.
        return motions
            .Where(m => !m.Name.EndsWith("_pos") && !m.Name.EndsWith("_face") && !m.Name.EndsWith("_cam")
                        && !m.Name.Contains("_ear") && !m.Name.Contains("tail"))
            .ToList();
    }

    static string RecordsDir => Path.GetFullPath(Application.dataPath + UnityHumanoidVMDRecorder.FileSavePath);

    /// <summary>The newest Batch_&lt;set&gt;_&lt;date&gt; folder, or null.</summary>
    static string NewestBatchDir(string setName)
    {
        if (!Directory.Exists(RecordsDir)) return null;
        // The date in the name sorts oldest to newest.
        return Directory.GetDirectories(RecordsDir, $"Batch_{setName}_*").OrderBy(d => d).LastOrDefault();
    }

    const string SkippedFileName = "skipped.txt";

    /// <summary>The file names (no .vmd) of the motions a run in <paramref name="dir"/> could not record.</summary>
    static HashSet<string> ReadSkipped(string dir)
    {
        var names = new HashSet<string>();
        var file = Path.Combine(dir, SkippedFileName);
        if (!File.Exists(file)) return names;
        foreach (var line in File.ReadAllLines(file))
        {
            var name = line.Split('\t')[0].Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    /// <summary>Notes in <paramref name="dir"/> that a motion could not be recorded, so a resume
    /// doesn't try it again (it would fail the same way, and the batch would never finish).</summary>
    static void WriteSkipped(string dir, string fileName, string reason)
    {
        try
        {
            File.AppendAllText(Path.Combine(dir, SkippedFileName), $"{fileName}\t{reason}\n");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Batch VMD: couldn't write {SkippedFileName}: {e.Message}");
        }
    }

    /// <summary>The newest batch folder of the set if some motions have neither a VMD nor a
    /// skipped.txt line there, or null. <paramref name="done"/> counts both.</summary>
    static string UnfinishedBatchDir(string setName, int motionCount, out int done)
    {
        done = 0;
        var dir = NewestBatchDir(setName);
        if (dir == null) return null;
        var handled = new HashSet<string>(Directory.GetFiles(dir, "*.vmd").Select(Path.GetFileNameWithoutExtension));
        handled.UnionWith(ReadSkipped(dir));
        done = Math.Min(handled.Count, motionCount);
        return done < motionCount ? dir : null;
    }

    // The idle label is refreshed every second rather than every frame (it scans a folder).
    float nextLabelRefresh;

    void Update()
    {
        if (!batchLabel) return;
        if (running)
        {
            batchLabel.text = progressLabel;
            return;
        }
        if (Time.unscaledTime < nextLabelRefresh) return;
        nextLabelRefresh = Time.unscaledTime + 1f;

        var motions = ListedMotions(out _);
        if (motions == null || motions.Count == 0)
        {
            batchLabel.text = IdleLabel;
            return;
        }
        var resumeDir = UnfinishedBatchDir(SetName(UmaViewerUI.Instance), motions.Count, out int done);
        batchLabel.text = resumeDir != null && done > 0
            ? $"Resume All ({done}/{motions.Count})"
            : $"{IdleLabel} ({motions.Count})";
    }

    void OnClick()
    {
        var ui = UmaViewerUI.Instance;
        if (running)
        {
            cancel = true;
            ui.ShowMessage("Batch VMD will stop after the current motion", UIMessageType.Warning);
            return;
        }

        var motions = ListedMotions(out var why);
        if (motions == null || motions.Count == 0)
        {
            ui.ShowMessage(why ?? "No body motions in this list", UIMessageType.Warning);
            return;
        }
        var resumeDir = UnfinishedBatchDir(SetName(ui), motions.Count, out int done);
        if (done == 0) resumeDir = null; // an empty folder: start a fresh one
        StartCoroutine(Guarded(Run(motions, resumeDir)));
    }

    /// <summary>Runs <paramref name="inner"/>, and says so on screen if it throws (Unity would only stop it).</summary>
    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object current;
            try
            {
                if (!inner.MoveNext()) yield break;
                current = inner.Current;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                UmaViewerUI.Instance.ShowMessage($"Batch VMD stopped by an error: {e.Message} (see the Console)", UIMessageType.Error);
                EndRun();
                yield break;
            }
            yield return current;
        }
    }

    void EndRun()
    {
        running = false;
        if (vmdButton) vmdButton.interactable = true;
        nextLabelRefresh = 0; // show the new count right away
    }

    /// <summary>
    /// Sets the face animator up for one motion: its own face and ear clips (which
    /// LoadAnimation put in clip_2 / clip_2_ear), or the controller's empty placeholder for a
    /// part the motion has none of, so the previous motion's clip doesn't carry over.
    /// Stopped (speed 0) and with no pending triggers: the caller poses it every frame.
    /// </summary>
    static void PrepareFace(UmaContainerCharacter container, UmaDatabaseEntry entry, Animator faceAnimator,
        AnimatorOverrideController faceOverride, out float faceLength, out float earLength)
    {
        // The same names LoadAnimation looks the clips up by.
        var abList = UmaViewerMain.Instance.AbList;
        string facialName = entry.Name.Replace("/body", "/facial");
        bool hasFace = abList.ContainsKey(facialName + "_face");
        bool hasEar = abList.ContainsKey(facialName + "_ear");

        // null puts back the controller's own clip: no face animation, as on a fresh load.
        if (!hasFace) faceOverride["clip_1"] = faceOverride["clip_2"] = null;
        if (!hasEar) faceOverride["clip_1_ear"] = faceOverride["clip_2_ear"] = null;

        // LoadAnimation may have queued a motion_1 -> motion_2 transition (a loop's start):
        // with the animator posed directly, a pending trigger would blend in the previous clip.
        foreach (var p in faceAnimator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Trigger) faceAnimator.ResetTrigger(p.nameHash);
        }
        faceAnimator.speed = 0;

        if (!hasFace || !hasEar)
        {
            // Back to the rest locator, and the morphs with it.
            faceAnimator.Play("motion_2", 0, 0);
            faceAnimator.Play("motion_2", 1, 0);
            container.FaceDrivenKeyTarget?.ResetLocator();
        }

        faceLength = hasFace && faceOverride["clip_2"] ? faceOverride["clip_2"].length : 0;
        earLength = hasEar && faceOverride["clip_2_ear"] ? faceOverride["clip_2_ear"].length : 0;
    }

    /// <summary>The normalized time of a face or ear clip <paramref name="t"/> seconds into the motion.</summary>
    static float FaceTime(float t, float clipLength, AnimationClip clip)
    {
        if (clipLength <= 0) return 0;
        float n = t / clipLength;
        return clip && clip.isLooping ? n : Mathf.Min(1f, n);
    }

    /// <param name="resumeDir">A folder of an earlier batch to finish: motions with a VMD there
    /// are skipped. Null starts a new folder.</param>
    IEnumerator Run(List<UmaDatabaseEntry> motions, string resumeDir)
    {
        var ui = UmaViewerUI.Instance;
        var container = UmaViewerBuilder.Instance ? UmaViewerBuilder.Instance.CurrentUMAContainer : null;
        if (!container || container.IsMini || !container.UmaAnimator)
        {
            ui.ShowMessage("Load a normal (not mini) character first", UIMessageType.Warning);
            yield break;
        }

        var rootbone = container.transform.Find("Position");
        var leftover = rootbone.GetComponent<UnityHumanoidVMDRecorder>();
        if (leftover && leftover.IsRecording)
        {
            ui.ShowMessage("Stop the manual Record VMD first", UIMessageType.Warning);
            yield break;
        }
        if (leftover) Destroy(leftover); // an idle one would block the Record VMD button too
        // SaveVMD destroys its recorder, so each motion gets a new one (as the Record VMD button does).
        UnityHumanoidVMDRecorder recorder = null;

        var setName = SetName(ui);
        var outDir = resumeDir ?? Path.Combine(RecordsDir, $"Batch_{setName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
        Directory.CreateDirectory(outDir);

        // The animator steps with the recorder (one fixed step each), so every recorded frame
        // is exactly one frame of the motion, however fast the screen draws.
        var animator = container.UmaAnimator;
        var oldSpeed = animator.speed;
        var oldUpdateMode = animator.updateMode;
        animator.updateMode = AnimatorUpdateMode.AnimatePhysics;
        // Hair and skirt physics aren't recorded (only body bones are), and without
        // CySpringPlugin.dll they throw every frame. ModelExporter turns them off the same way.
        var oldPhysics = container.EnablePhysics;
        container.EnablePhysics = false;
        // Eye tracking is a look-at IK on the head and spine, aimed at the camera: it would be
        // recorded into the motion (the head turned ~20° on the first frames, then following
        // the camera). With it off, the character turns the IK off in its next FixedUpdate.
        var oldEyeTracking = container.EnableEyeTracking;
        container.EnableEyeTracking = false;
        // The face (layer 0) and ears (layer 1) play on their own animator. It's stepped and
        // posed like the body, frame by frame; left alone it ran in real time from when the
        // motion was loaded, and kept looping the last face clip it had into every later
        // motion without one (the recorder reads the face locator on every frame).
        var faceAnimator = container.UmaFaceAnimator;
        var faceOverride = container.FaceOverrideController;
        var oldFaceSpeed = faceAnimator ? faceAnimator.speed : 1f;
        var oldFaceUpdateMode = faceAnimator ? faceAnimator.updateMode : AnimatorUpdateMode.Normal;
        if (faceAnimator) faceAnimator.updateMode = AnimatorUpdateMode.AnimatePhysics;

        running = true;
        cancel = false;
        progressLabel = "Cancel";
        if (vmdButton) vmdButton.interactable = false; // a manual recording would clash with the batch
        ui.ShowMessage(resumeDir != null
            ? $"Resuming batch VMD of {motions.Count} motions in {Path.GetFileName(outDir)}"
            : $"Recording {motions.Count} motions of {setName} to {outDir}", UIMessageType.Default);

        var skipped = new List<string>();
        var usedNames = new HashSet<string>();
        var skippedBefore = ReadSkipped(outDir);
        int saved = 0, alreadyDone = 0, failedBefore = 0;
        try
        {
            for (int j = 0; j < motions.Count && !cancel; j++)
            {
                var entry = motions[j];
                var motionName = Path.GetFileName(entry.Name);
                progressLabel = $"Cancel ({j + 1}/{motions.Count})";

                // Two motions can share a file name; keep both. Named in list order, so a
                // resumed batch gives every motion the same name as the first run did.
                var fileName = motionName;
                for (int n = 2; !usedNames.Add(fileName); n++) fileName = $"{motionName}_{n}";
                var path = Path.Combine(outDir, fileName + ".vmd");
                if (File.Exists(path))
                {
                    alreadyDone++;
                    continue;
                }
                if (skippedBefore.Contains(fileName))
                {
                    failedBefore++;
                    continue;
                }
                void Skip(string reason)
                {
                    skipped.Add($"{motionName}: {reason}");
                    WriteSkipped(outDir, fileName, reason);
                }

                try
                {
                    container.LoadAnimation(entry);
                }
                catch (Exception e)
                {
                    // e.g. a bundle with no AnimationClip in it (a DrivenKeyAnimation only).
                    Debug.LogWarning($"Batch VMD: {entry.Name} failed to load: {e.Message}");
                    Skip($"failed to load ({e.Message})");
                    continue;
                }
                animator.speed = 1;

                // Let the animator take the new state, then wait for the motion itself
                // (a loop first plays the previous one's end and its own start).
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                float waitStart = Time.time;
                bool settled = false;
                while (!cancel && Time.time - waitStart < SettleTimeout)
                {
                    if (animator.GetCurrentAnimatorStateInfo(0).IsName("motion_2") && !animator.IsInTransition(0))
                    {
                        settled = true;
                        break;
                    }
                    yield return new WaitForFixedUpdate();
                }
                if (cancel) break;
                if (!settled)
                {
                    Skip("never settled into the motion");
                    continue;
                }

                // Every frame is sampled at its own time, with the animator stopped: frame i is
                // posed with Play(motion_2, i x dt / length), then one whole engine step runs
                // (WaitForFixedUpdate resumes after the animator's AnimatePhysics step) and the
                // bones are saved. So frame 0 is the motion's frame 0 (a loop's cycle start, with
                // no blend from the previous motion), and the last frame is the motion's end (a
                // loop's end is its start; a one-shot holds its last pose rather than leaving the
                // state). A bare animator.Update(0) after Initialize's T-pose isn't enough: it
                // re-posed the body, but the head and ears stayed 5-20° off until a full step ran.
                float length = animator.GetCurrentAnimatorStateInfo(0).length;
                recorder = rootbone.gameObject.AddComponent<UnityHumanoidVMDRecorder>();
                recorder.ManualCapture = true;
                recorder.Initialize(); // also sets Time.fixedDeltaTime to the VMD rate (30 fps)
                float dt = Time.fixedDeltaTime;
                // Frames 0..N at t = 0, dt, ..., length: a loop's last frame is its first.
                int frames = Mathf.RoundToInt(length / dt) + 1;
                animator.speed = 0;

                float faceLength = 0, earLength = 0;
                if (faceAnimator && faceOverride)
                {
                    PrepareFace(container, entry, faceAnimator, faceOverride, out faceLength, out earLength);
                }

                recorder.StartRecording();
                for (int i = 0; i < frames && !cancel; i++)
                {
                    float t = i * dt;
                    animator.Play("motion_2", 0, length > 0 ? Mathf.Min(1f, t / length) : 0f);
                    if (faceAnimator && faceOverride)
                    {
                        // The face and ear clips start with the body; one shorter than the body
                        // holds its last frame, a loop wraps as it does in the game.
                        faceAnimator.Play("motion_2", 0, FaceTime(t, faceLength, faceOverride["clip_2"]));
                        faceAnimator.Play("motion_2", 1, FaceTime(t, earLength, faceOverride["clip_2_ear"]));
                    }
                    yield return new WaitForFixedUpdate();
                    recorder.CaptureFrame();
                }
                recorder.StopRecording();
                if (cancel) break;

                try
                {
                    recorder.KeyReductionLevel = 1; // every frame, so a loop's last frame is its first
                    recorder.SaveVMD(container.name, path);
                    saved++;
                    Debug.Log($"Batch VMD: {fileName} <- {entry.Name}, {frames} frames ({length:0.###} s)");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Skip($"save failed ({e.Message})");
                }
            }
        }
        finally
        {
            // A recorder left by a cancel was never saved (saving destroys it); remove it.
            if (recorder)
            {
                if (recorder.IsRecording) recorder.StopRecording();
                Destroy(recorder);
            }
            if (animator)
            {
                animator.updateMode = oldUpdateMode;
                animator.speed = oldSpeed;
            }
            if (faceAnimator)
            {
                faceAnimator.updateMode = oldFaceUpdateMode;
                faceAnimator.speed = oldFaceSpeed;
            }
            if (container)
            {
                container.EnablePhysics = oldPhysics;
                container.EnableEyeTracking = oldEyeTracking;
            }
            EndRun();
        }

        if (skipped.Count > 0) Debug.LogWarning("Batch VMD skipped:\n" + string.Join("\n", skipped));
        var summary = (cancel ? "Batch VMD cancelled. " : "Batch VMD done. ") + $"Saved {saved}/{motions.Count} to {outDir}"
            + (alreadyDone > 0 ? $"; {alreadyDone} were already there" : "")
            + (failedBefore > 0 ? $"; {failedBefore} failed before (see {SkippedFileName})" : "")
            + (skipped.Count > 0 ? $"; skipped {skipped.Count} (see the Console)" : "");
        ui.ShowMessage(summary, cancel || skipped.Count > 0 ? UIMessageType.Warning : UIMessageType.Success);
        cancel = false;
    }
}
