using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QSnipAndClip
{
    internal static class CoreTests
    {
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            bool includeInteractiveInput = Array.IndexOf(args, "--include-interactive-input") >= 0;
            int exitCode = Run(includeInteractiveInput);
            Console.Out.Flush();
            Console.Error.Flush();

            // This process is the only one that ever holds tray GDI objects, a low-level keyboard
            // hook, WASAPI endpoints and a Media Foundation writer at once. Letting the CLR tear
            // all of that down together intermittently finalizes a COM proxy whose apartment has
            // already gone (RPC_NT_CALL_FAILED, surfacing as 0xC000041D) after every assertion has
            // already passed. The result is known at this point, so report it and leave rather
            // than racing shutdown. The app itself does not share this teardown and does not fault.
            // Environment.Exit still runs those finalizers, so end the process outright: every
            // test has already disposed its own resources and the OS reclaims the rest.
            TerminateProcess(GetCurrentProcess(), exitCode);
            return exitCode;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, int exitCode);

        private static int Run(bool includeInteractiveInput)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "QSNC-Tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                TestAssemblyMetadata();
                TestSettingsRoundTrip(root);
                TestLegacySettingsMigration(root);
                TestRecordingFrameRateSettings();
                TestRecordingQualitySettings(root);
                TestRecordingQualityBitrates();
                TestRecordingDiagnostics();
                TestRecordingPreferenceSaveRollback(root);
                TestFrameRowCopy();
                TestRecordingAudioSettings();
                TestTrayIcons();
                TestPcmAudioMixer();
                TestDeliveryReporting();
                TestRecordingResolution();
                TestAudioPeakTracker();
                TestShortcutCatalogOrder();
                TestCaptureActionMenuRow();
                TestPersistentMenu();
                TestShortcutSettings(root);
                TestShortcutConflictDetection();
                TestOutputShortcuts(root);
                TestFilenameDefaultDetection();
                TestRegionRecordingShade();
                TestCaptureCompletionBehavior(root);
                TestDefaultDestinationIsStandalone();
                TestRetiredQNameIsGone();
                TestRecordingQualityDefault(root);
                TestRegistrationFailureMessages();
                if (includeInteractiveInput)
                {
                    TestShortcutCaptureChordWindow();
                }
                else
                {
                    Console.WriteLine(
                        "SKIPPED: TestShortcutCaptureChordWindow requires an active Windows input desktop. "
                        + "Run windows\\test-interactive.ps1 deliberately before a release.");
                }
                TestCopyableDialog();
                TestStartupManager();
                TestUpdateChecker();
                TestDisplayRefreshRate();
                TestVideoBoundsNormalization();
                TestVideoScheduleSkipsCatchUpBursts();
                TestPngSave(root);
                TestFilenameTemplates(root);
                TestRecoveryFallback(root);
                TestClipRouting(root);
                TestNativeMp4Encoding(root);
                TestWasapiLoopbackInitialization();
                TestWasapiMicrophoneInitialization();
                TestMicrophoneDevices(root);
                TestRecordingPause();
                TestRecordingControlDefaults();
                TestRecordingControlConflicts();
                TestRecordingControlRepair();
                TestRecordingControlSettings(root);
                TestRecordingControlMigrationFromSchema13(root);
                TestDisplayLayoutIdentity();
                TestControllerLayoutStore();
                TestControllerPlacement();
                TestControllerLayoutGeometry();
                TestControllerIsWhiteAndUnpanelled();
                TestControllerButtonsSitUnderThePosts();
                TestResizeNotchLivesOnTheHAndHidesUntilHovered();
                Console.WriteLine("PASS: " + assertions + " assertions");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
            finally
            {
                if (Directory.Exists(root)
                    && root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("QSNC-Tests-", StringComparison.Ordinal))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestAssemblyMetadata()
        {
            Assembly assembly = typeof(CoreTests).Assembly;
            AssemblyProductAttribute product = (AssemblyProductAttribute)Attribute.GetCustomAttribute(
                assembly,
                typeof(AssemblyProductAttribute));
            AssemblyInformationalVersionAttribute informational =
                (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly,
                    typeof(AssemblyInformationalVersionAttribute));

            AssertEqual("Huck’s Snip ’n’ Clip", product.Product,
                "The Windows binary should carry the canonical public product name.");
            AssertEqual("0.1.5.0", assembly.GetName().Version.ToString(),
                "The Windows assembly version should match release 0.1.5.");
            AssertEqual("0.1.5", informational.InformationalVersion,
                "The Windows informational version should match the installer and macOS metadata.");
        }

        private static void TestRecordingPause()
        {
            using (ScreenClipRecorder recorder = new ScreenClipRecorder(15, RecordingAudioMode.ComputerAndMicrophone))
            {
                try
                {
                    recorder.Start(new Rectangle(0, 0, 160, 120));
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (recorder.DurationSeconds < 0.35 && !recorder.IsFinished && DateTime.UtcNow < deadline)
                        Thread.Sleep(20);
                    AssertTrue(recorder.Error == null && recorder.DurationSeconds >= 0.35,
                        "The hardware recorder must deliver frames before pausing.");
                    recorder.SetPaused(true);
                    deadline = DateTime.UtcNow.AddSeconds(3);
                    while (!recorder.IsPaused && !recorder.IsFinished && DateTime.UtcNow < deadline) Thread.Sleep(10);
                    AssertTrue(recorder.IsPaused, "The recording thread must acknowledge pause.");
                    double pausedAt = recorder.DurationSeconds;
                    Thread.Sleep(700);
                    AssertTrue(Math.Abs(recorder.DurationSeconds - pausedAt) < 0.01,
                        "Paused wall-clock time must not advance the audio/video timeline.");
                    recorder.SetPaused(false);
                    deadline = DateTime.UtcNow.AddSeconds(3);
                    while (recorder.DurationSeconds < pausedAt + 0.35 && !recorder.IsFinished && DateTime.UtcNow < deadline)
                        Thread.Sleep(20);
                    AssertTrue(recorder.DurationSeconds >= pausedAt + 0.35, "Resume must continue the same timeline.");
                    recorder.SetPaused(true);
                    deadline = DateTime.UtcNow.AddSeconds(3);
                    while (!recorder.IsPaused && !recorder.IsFinished && DateTime.UtcNow < deadline) Thread.Sleep(10);
                    double stoppedAt = recorder.DurationSeconds;
                    Thread.Sleep(400);
                    recorder.Stop();
                    deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!recorder.IsFinished && DateTime.UtcNow < deadline) Thread.Sleep(20);
                    AssertTrue(recorder.IsFinished && recorder.Error == null,
                        "Stopping while paused must finalize the same valid recording.");
                    AssertTrue(Math.Abs(recorder.DurationSeconds - stoppedAt) < 0.05,
                        "Stopping while paused must not retain the paused interval.");
                    AssertTrue(File.Exists(recorder.WorkingPath) && new FileInfo(recorder.WorkingPath).Length > 1000,
                        "Pause/resume must produce one nonempty MP4 with both audio sources enabled.");
                    string evidence = Environment.GetEnvironmentVariable("QSNC_TEST_MEDIA_DIR");
                    if (!String.IsNullOrWhiteSpace(evidence))
                    {
                        Directory.CreateDirectory(evidence);
                        File.Copy(recorder.WorkingPath, Path.Combine(evidence, "pause-resume.mp4"), true);
                        File.Copy(recorder.DiagnosticsPath, Path.Combine(evidence, "pause-resume.txt"), true);
                    }
                }
                finally
                {
                    recorder.Dispose();
                    if (recorder.IsFinished)
                    {
                        if (File.Exists(recorder.WorkingPath)) File.Delete(recorder.WorkingPath);
                        if (File.Exists(recorder.DiagnosticsPath)) File.Delete(recorder.DiagnosticsPath);
                    }
                }
            }
        }

        private static void TestPersistentMenu()
        {
            using (PersistentTrayMenu menu = new PersistentTrayMenu())
            {
                ToolStripMenuItem branch = new ToolStripMenuItem("Recording");
                PersistentChoice choice = new PersistentChoice("Choice");
                choice.Click += delegate { choice.Checked = !choice.Checked; };
                branch.DropDownItems.Add(choice);
                menu.Items.Add(branch);
                menu.Configure();
                menu.Show(new Point(10, 10));
                branch.ShowDropDown();
                AssertTrue(menu.Visible && branch.DropDown.Visible, "Both menu levels must start visible.");
                choice.PerformClick();
                AssertTrue(menu.Visible && branch.DropDown.Visible && choice.Checked,
                    "A routine selection must update its checkmark without closing either menu level: root="
                    + menu.Visible + ", branch=" + branch.DropDown.Visible + ", checked=" + choice.Checked
                    + ", preserve=" + menu.PreserveInteraction);
                Application.DoEvents();
                AssertTrue(!menu.PreserveInteraction, "Persistence must end after the click is dispatched.");
                menu.Close(ToolStripDropDownCloseReason.Keyboard);
                AssertTrue(!menu.Visible, "Escape must still dismiss a persistent menu.");
            }
        }

        private static void TestOutputShortcuts(string root)
        {
            SettingsStore store = new SettingsStore(Path.Combine(root, "outputs"), delegate { return root; });
            AppSettings settings = store.Load();
            CaptureDestination first = settings.Destinations[0];
            CaptureDestination second = SettingsStore.CreateDestination(Path.Combine(root, "other"), "Other");
            settings.Destinations.Add(second);
            store.Save(settings);
            AssertTrue(settings.OutputShortcuts[first.Id].Key == Keys.D1
                && settings.OutputShortcuts[second.Id].Key == Keys.D2, "Each destination gets a distinct output selector.");
            AssertTrue(settings.OutputConflict(first.Id, new ShortcutBinding { Key = Keys.A }) != null,
                "Bare output keys must be rejected.");
            AssertTrue(settings.OutputConflict(first.Id, settings.GetShortcut(CaptureAction.SnipRegion)) != null,
                "Output keys must not steal capture shortcuts.");
            AssertTrue(settings.OutputConflict(first.Id, settings.OutputShortcuts[second.Id]) != null,
                "Output keys must not steal another destination's selector.");
            ShortcutBinding custom = new ShortcutBinding { Modifiers = 6, Key = Keys.F12 };
            settings.OutputShortcuts[first.Id] = custom;
            store.Save(settings);
            AppSettings restored = store.Load();
            AssertTrue(restored.OutputShortcuts[first.Id].EqualsBinding(custom), "Custom output keys must survive relaunch.");
            AssertEqual(first.Id, restored.ActiveDestinationId, "Adding output shortcuts must preserve the active route.");
            settings.SetShortcut(CaptureAction.SnipRegion,
                new ShortcutBinding { Modifiers = 3, Key = Keys.F11, SecondModifiers = 6, SecondKey = Keys.F12 });
            AssertTrue(settings.OutputConflict(first.Id, custom) != null, "Second capture strokes also reserve their keys.");

            // "Reset Output Shortcuts to Defaults" clears the map and re-derives it, matching Mac.
            AppSettings resetting = store.Load();
            resetting.OutputShortcuts[resetting.Destinations[0].Id] =
                new ShortcutBinding { Modifiers = 6, Key = Keys.F9 };
            resetting.OutputShortcuts.Clear();
            AssertTrue(resetting.EnsureOutputShortcuts(), "Resetting must re-derive the default output keys.");
            AssertEqual((int)Keys.D1, (int)resetting.OutputShortcuts[resetting.Destinations[0].Id].Key,
                "The first output returns to its default selector.");
            AssertEqual(
                (int)(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT),
                (int)resetting.OutputShortcuts[resetting.Destinations[0].Id].Modifiers,
                "Default output selectors stay on Ctrl+Alt.");
            AssertEqual(resetting.Destinations.Count, resetting.OutputShortcuts.Count,
                "Every output gets a selector back after a reset.");
        }

        private static void TestRecordingQualityDefault(string root)
        {
            // A brand-new install starts on Balanced, matching the Mac.
            string fresh = Path.Combine(root, "fresh-quality");
            SettingsStore freshStore = new SettingsStore(fresh, delegate { return root; });
            AppSettings created = freshStore.Load();
            AssertEqual(RecordingQuality.Balanced.ToString(), created.RecordingQuality.ToString(),
                "A new Windows install should default to Balanced quality.");

            // An existing file that predates the key kept the original fixed bitrate, and
            // migrating it must not silently change what that user was already recording.
            string existing = Path.Combine(root, "existing-quality");
            Directory.CreateDirectory(existing);
            SettingsStore existingStore = new SettingsStore(existing, delegate { return root; });
            File.WriteAllText(existingStore.SettingsPath,
                "Version=1" + Environment.NewLine
                + "Destination=abc|SW5nZXN0|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(root))
                + Environment.NewLine);
            AppSettings migrated = existingStore.Load();
            AssertEqual(RecordingQuality.Legacy.ToString(), migrated.RecordingQuality.ToString(),
                "An older settings file must keep the original bitrate it was already using.");

            // An explicit choice always wins over either default.
            AppSettings chosen = freshStore.Load();
            chosen.RecordingQuality = RecordingQuality.High;
            freshStore.Save(chosen);
            AssertEqual(RecordingQuality.High.ToString(), freshStore.Load().RecordingQuality.ToString(),
                "A chosen quality must survive relaunch untouched.");
        }

        private static void TestRetiredQNameIsGone()
        {
            // "QSNC" is the retired pre-rebrand name and was shipping publicly in the install path
            // and in the user's own AppData. macOS already used HucksSnipNClip.
            AssertEqual("HucksSnipNClip", AppPaths.DataFolderName,
                "Windows data must live under the Huck name, matching macOS.");
            AssertEqual("QSNC", AppPaths.LegacyDataFolderName,
                "The retired name must still be known so existing data can be adopted.");
            AssertTrue(AppPaths.DataRoot.IndexOf("QSNC", StringComparison.OrdinalIgnoreCase) < 0,
                "The data folder must not contain the retired name: " + AppPaths.DataRoot);
            AssertTrue(AppPaths.SubFolder("Recovery").EndsWith("Recovery", StringComparison.Ordinal),
                "Recovery must sit inside the app's own data folder.");
            AssertEqual("HucksSnipNClip", AppPaths.FilePrefix,
                "Working and diagnostics files must carry the public name.");
        }

        private static void TestDefaultDestinationIsStandalone()
        {
            // Shipped software must not hunt the filesystem for a development folder. This used to
            // walk up ten parents looking for "Ingest", which on someone else's machine reaches
            // their user folder and the drive root.
            string chosen = SettingsStore.FindDefaultDestination();
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (String.IsNullOrWhiteSpace(pictures))
            {
                pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            AssertEqual(Path.Combine(pictures, "Huck's Snip 'n' Clip"), chosen,
                "A new install must default inside the user's own Pictures folder.");
            AssertTrue(chosen.IndexOf("Ingest", StringComparison.OrdinalIgnoreCase) < 0,
                "The default destination must never resolve to a development Ingest folder: " + chosen);
            AssertTrue(chosen.IndexOf("Development Workspace", StringComparison.OrdinalIgnoreCase) < 0,
                "The default destination must never reference a development workspace: " + chosen);

            // Even with an Ingest folder sitting beside the executable, nothing may pick it up.
            string decoy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ingest");
            bool created = false;
            try
            {
                if (!Directory.Exists(decoy)) { Directory.CreateDirectory(decoy); created = true; }
                AssertEqual(chosen, SettingsStore.FindDefaultDestination(),
                    "A neighbouring Ingest folder must not change where a new install saves.");
            }
            finally
            {
                if (created) { try { Directory.Delete(decoy); } catch { } }
            }
        }

        private static void TestRegionRecordingShade()
        {
            // The guide is pointless when the target already is the whole desktop, and a
            // degenerate rectangle must never produce a window.
            Rectangle desktop = SystemInformation.VirtualScreen;
            AssertTrue(RegionRecordingShade.TryShow(desktop) == null,
                "A full-desktop recording needs no region shade.");
            AssertTrue(RegionRecordingShade.TryShow(new Rectangle(0, 0, 0, 0)) == null,
                "An empty region must not create a shade window.");
            AssertTrue(RegionRecordingShade.TryShow(new Rectangle(10, 10, -5, 40)) == null,
                "A negative region must not create a shade window.");
        }

        private static void TestCaptureCompletionBehavior(string root)
        {
            using (Bitmap desktop = new Bitmap(20, 20))
            using (RegionSelectionForm snipSelector = new RegionSelectionForm(desktop))
            using (RegionSelectionForm clipSelector = new RegionSelectionForm(desktop, "Enter continues"))
            {
                AssertTrue(!snipSelector.RequiresConfirmation,
                    "A Region Snip should finish on mouse release without a confirm/redraw step.");
                AssertTrue(clipSelector.RequiresConfirmation,
                    "A Region Clip should retain explicit confirmation before its countdown.");
            }

            string clip = Path.Combine(root, "clipboard-clip.mp4");
            File.WriteAllBytes(clip, new byte[] { 1, 2, 3 });
            System.Collections.Specialized.StringCollection files = CaptureClipboard.CreateFileDropList(clip);
            AssertEqual(1, files.Count, "The clipboard should contain one completed Clip file.");
            AssertEqual(Path.GetFullPath(clip), files[0],
                "The clipboard file-drop entry should use the finished capture's absolute path.");
        }

        private static void TestFilenameDefaultDetection()
        {
            // The Filenames submenu shows Default/Custom and only enables Reset when it can act.
            AppSettings settings = new AppSettings();
            AssertTrue(String.IsNullOrEmpty(settings.FilenameLabel)
                && String.IsNullOrEmpty(settings.FilenameTemplate),
                "A fresh install starts on the default naming.");

            settings.FilenameTemplate = CaptureFilename.DefaultTemplate;
            AssertTrue(settings.FilenameTemplate == CaptureFilename.DefaultTemplate,
                "Explicitly storing the default template still counts as default.");

            DateTime moment = new DateTime(2026, 9, 14, 18, 45, 30, 125);
            string blank = CaptureFilename.Render("", "", "Snip", moment, 1);
            string reset = CaptureFilename.Render("", CaptureFilename.DefaultTemplate, "Snip", moment, 1);
            AssertEqual(blank, reset,
                "Resetting to the default template must reproduce the blank-setting name exactly.");
            AssertTrue(blank.StartsWith("HucksSnipNClip_Snip_", StringComparison.Ordinal),
                "The reset name keeps the public Huck prefix: " + blank);

            string clip = CaptureFilename.Render("", "", "Clip", moment, 1);
            AssertTrue(clip.StartsWith("HucksSnipNClip_Clip_", StringComparison.Ordinal),
                "Clip previews render their own kind: " + clip);
        }

        private static void TestFilenameTemplates(string root)
        {
            DateTime started = new DateTime(2026, 9, 14, 13, 4, 5, 678);
            AssertEqual("HucksSnipNClip_Clip_2026-09-14_13-04-05-678",
                CaptureFilename.Render("", "", "Clip", started, 1), "Blank settings preserve the public default filename exactly.");
            AssertEqual("Game_Snip_042", CaptureFilename.Render("Game", "{label}_{kind}_{counter}", "Snip", started, 42),
                "Labels and padded counters must render without changing the capture kind.");
            foreach (string invalid in new[] { "{bad}", "{kind", "kind}", "../escape", "CON", "NUL.txt", "end.", "end ", "a:b" })
            {
                bool rejected = false;
                try { CaptureFilename.Render("", invalid, "Snip", started, 1); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected, "Unsafe filename must be rejected: " + invalid);
            }
            string output = Path.Combine(root, "filename-output");
            CaptureSaveService service = new CaptureSaveService(Path.Combine(root, "filename-recovery"));
            using (Bitmap bitmap = new Bitmap(20, 20))
            {
                CaptureSaveResult first = service.SavePng(bitmap, output, "same");
                byte[] original = File.ReadAllBytes(first.SavedPath);
                CaptureSaveResult second = service.SavePng(bitmap, output, "same");
                AssertTrue(first.Succeeded && second.Succeeded && first.SavedPath != second.SavedPath,
                    "Repeated names must publish without overwriting an earlier capture.");
                AssertEqual(original.Length, File.ReadAllBytes(first.SavedPath).Length, "Collision must preserve the original file.");
            }
            SettingsStore store = new SettingsStore(Path.Combine(root, "filename-settings"), delegate { return root; });
            AppSettings settings = store.Load();
            settings.FilenameLabel = "Game"; settings.FilenameTemplate = "{label}_{counter}"; settings.NextFilenameCounter = 43;
            store.Save(settings);
            AppSettings loaded = store.Load();
            AssertEqual("Game", loaded.FilenameLabel, "The filename label must persist.");
            AssertEqual("{label}_{counter}", loaded.FilenameTemplate, "The filename template must persist.");
            AssertTrue(loaded.NextFilenameCounter == 43, "The next filename counter must survive relaunch.");
        }

        private static void TestMicrophoneDevices(string root)
        {
            System.Collections.Generic.List<MicrophoneDevice> devices = MicrophoneDevices.Enumerate();
            AssertTrue(devices.Count > 0, "This PC's active microphone must enumerate by identity and name.");
            foreach (MicrophoneDevice device in devices)
                AssertTrue(!String.IsNullOrWhiteSpace(device.Id) && !String.IsNullOrWhiteSpace(device.Name),
                    "Enumerated microphones must have durable IDs and display names.");
            using (WasapiMicrophoneCapture named = new WasapiMicrophoneCapture(devices[0].Id))
                AssertTrue(!named.UsedDefaultFallback, "An available named input must open without fallback.");
            using (WasapiMicrophoneCapture missing = new WasapiMicrophoneCapture("QSNC-Missing-Test-Device"))
                AssertTrue(missing.UsedDefaultFallback, "A disappeared microphone must visibly fall back to System Default.");
            SettingsStore store = new SettingsStore(Path.Combine(root, "devices"), delegate { return root; });
            AppSettings settings = store.Load();
            AssertTrue(!settings.ShowCursorInClips, "Cursor capture must remain opt-in.");
            settings.MicrophoneDeviceId = devices[0].Id; settings.ShowCursorInClips = true;
            store.Save(settings);
            settings = store.Load();
            AssertTrue(settings.ShowCursorInClips && settings.MicrophoneDeviceId == devices[0].Id,
                "Cursor choice and named microphone identity must survive relaunch.");
        }

        private static void TestTrayIcons()
        {
            using (Bitmap idle = TrayIconFactory.CreateBitmap(false))
            using (Bitmap quietRecording = TrayIconFactory.CreateBitmap(true, 0.0f, 0.30f))
            using (Bitmap activeRecording = TrayIconFactory.CreateBitmap(true, 0.72f, 0.68f))
            using (Bitmap clippingRecording = TrayIconFactory.CreateBitmap(true, 0.92f, 0.68f))
            using (Bitmap overloadedRecording = TrayIconFactory.CreateBitmap(true, 0.72f, 0.95f))
            using (Bitmap handTuned = TrayIconFactory.CreateBitmap(false, 0.0f, 0.0f, 16))
            {
                AssertEqual(32, idle.Width, "The idle tray icon should render at 32 pixels.");
                AssertEqual(32, quietRecording.Height, "The recording tray icon should render at 32 pixels.");
                AssertEqual(16, handTuned.Width, "The hand-tuned tray icon should render at 16 pixels.");
                AssertTrue(
                    CountPixelsNear(idle, Color.FromArgb(150, 150, 150), 10) > 350,
                    "The idle H should use the approved neutral grayscale palette.");
                AssertTrue(
                    CountOpaquePixels(handTuned) >= 145,
                    "The 16-pixel H should retain its thick hand-tuned posts and crossbar.");
                AssertEqual(0, handTuned.GetPixel(10, 3).A, "Film holes must remain transparent at tray size.");
                AssertEqual(0, handTuned.GetPixel(2, 3).A, "The small scissors cut must remain transparent.");
                AssertTrue(handTuned.GetPixel(8, 8).A > 200, "The crossbar must survive the cut paths.");
                AssertTrue(
                    CountPixelsNear(quietRecording, Color.FromArgb(201, 138, 46), 10) > 25,
                    "Ordinary CPU load should produce a proportional amber meter.");
                AssertTrue(
                    CountPixelsNear(activeRecording, Color.FromArgb(63, 157, 98), 10) > 150,
                    "The left side of the recording H should display microphone input.");
                AssertTrue(
                    CountPixelsNear(activeRecording, Color.FromArgb(201, 138, 46), 10) > 150,
                    "Higher ordinary CPU should display a taller amber right-side meter.");
                AssertTrue(
                    CountPixelsNear(activeRecording, Color.FromArgb(201, 138, 46), 10)
                        > CountPixelsNear(quietRecording, Color.FromArgb(201, 138, 46), 10),
                    "The amber right-side meter should grow with CPU usage.");
                AssertTrue(
                    CountPixelsNear(overloadedRecording, Color.FromArgb(192, 80, 63), 10) > 150,
                    "CPU above 85 percent should turn the whole right-side meter red.");
                AssertTrue(
                    CountPixelsNear(clippingRecording, Color.FromArgb(201, 138, 46), 10)
                        > CountPixelsNear(activeRecording, Color.FromArgb(201, 138, 46), 10),
                    "Input above 85 percent should add amber pixels above its green body.");
                AssertTrue(
                    CountOpaquePixels(activeRecording) > CountOpaquePixels(quietRecording),
                    "Meter height should communicate activity independently of hue.");
            }

            using (Icon icon = TrayIconFactory.CreateRecording(0.72f, 0.68f))
            using (Bitmap roundTripped = icon.ToBitmap())
            {
                AssertTrue(
                    CountPixelsNear(roundTripped, Color.FromArgb(63, 157, 98), 25) > 100,
                    "The microphone meter should survive conversion into a Windows icon handle.");

                using (Bitmap traySize = new Bitmap(16, 16))
                using (Graphics graphics = Graphics.FromImage(traySize))
                {
                    graphics.DrawIcon(icon, new Rectangle(0, 0, 16, 16));
                    AssertTrue(
                        CountOpaquePixels(traySize) > 100,
                        "The H and both meter positions should remain visible at tray size.");
                }
            }

            MeterSmoother smoother = new MeterSmoother();
            float attacked = smoother.Update(0.90f, 100);
            AssertTrue(attacked > 0.59f && attacked < 0.61f, "Meter attack should take about 150 ms.");
            float held = smoother.Update(0.0f, 400);
            AssertTrue(Math.Abs(held - attacked) < 0.001f, "A recent peak should remain held.");
            smoother.Update(0.0f, 400);
            float released = smoother.Update(0.0f, 200);
            AssertTrue(released < held && released > 0.0f, "Meter release should decay after the peak hold.");

            SystemLoadSampler sampler = new SystemLoadSampler();
            sampler.Sample();
            Thread.SpinWait(10000);
            float systemLoad = sampler.Sample();
            AssertTrue(systemLoad >= 0.0f && systemLoad <= 1.0f, "System load should stay normalized.");
        }

        private static int CountOpaquePixels(Bitmap bitmap)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > 200)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static int CountPixelsNear(Bitmap bitmap, Color expected, int tolerance)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color actual = bitmap.GetPixel(x, y);
                    if (Math.Abs(actual.R - expected.R) <= tolerance
                        && Math.Abs(actual.G - expected.G) <= tolerance
                        && Math.Abs(actual.B - expected.B) <= tolerance
                        && actual.A > 200)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void TestSettingsRoundTrip(string root)
        {
            string settingsRoot = Path.Combine(root, "settings");
            string defaultDestination = Path.Combine(root, "default-destination");
            SettingsStore store = new SettingsStore(settingsRoot, delegate { return defaultDestination; });

            AppSettings first = store.Load();
            AssertEqual(1, first.Destinations.Count, "One default destination should be created.");
            AssertEqual(defaultDestination, first.GetActiveDestination().Path, "Default destination should be active.");
            AssertEqual(15, first.RecordingFrameRate, "Existing behavior should default to 15 FPS.");
            AssertEqual((int)RecordingQuality.Balanced, (int)first.RecordingQuality, "A new install should default to Balanced quality, matching the Mac.");
            AssertEqual((int)RecordingAudioMode.Off, (int)first.RecordingAudioMode, "Recording audio should default to Off.");
            AssertEqual(100, first.ComputerAudioGainPercent, "Computer audio gain should default to 100%.");
            AssertEqual(100, first.MicrophoneGainPercent, "Microphone gain should default to 100%.");
            AssertTrue(first.RoutineNotificationsEnabled, "Routine setting notifications should default on.");

            string changedDestination = Path.Combine(root, "changed-destination");
            CaptureDestination added = SettingsStore.CreateDestination(changedDestination, "Project | Alpha");
            first.Destinations.Add(added);
            first.ActiveDestinationId = added.Id;
            first.RecordingFrameRate = 30;
            first.RecordingQuality = RecordingQuality.High;
            first.RecordingAudioMode = RecordingAudioMode.Computer;
            first.ComputerAudioGainPercent = 150;
            first.MicrophoneGainPercent = 75;
            first.RoutineNotificationsEnabled = false;
            first.SetShortcut(
                CaptureAction.ClipRegion,
                new ShortcutBinding
                {
                    Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
                    Key = Keys.F10
                });
            store.Save(first);

            AppSettings reloaded = store.Load();
            AssertEqual(2, reloaded.Destinations.Count, "Named destinations should survive reload.");
            AssertEqual(changedDestination, reloaded.GetActiveDestination().Path, "Active destination should survive reload.");
            AssertEqual("Project | Alpha", reloaded.GetActiveDestination().Name, "Destination name should survive reload.");
            AssertEqual(30, reloaded.RecordingFrameRate, "Recording frame rate should survive reload.");
            AssertEqual((int)RecordingQuality.High, (int)reloaded.RecordingQuality, "Recording quality should survive reload independently of FPS.");
            AssertEqual((int)RecordingAudioMode.Computer, (int)reloaded.RecordingAudioMode, "Recording audio mode should survive reload.");
            AssertEqual(150, reloaded.ComputerAudioGainPercent, "Computer audio gain should survive reload.");
            AssertEqual(75, reloaded.MicrophoneGainPercent, "Microphone gain should survive reload.");
            AssertTrue(!reloaded.RoutineNotificationsEnabled, "The routine-notification preference should survive reload.");
            AssertEqual(
                "Ctrl+Shift+F10",
                reloaded.GetShortcut(CaptureAction.ClipRegion).ToDisplayString(),
                "Custom shortcut should survive reload.");
        }

        private static void TestLegacySettingsMigration(string root)
        {
            string settingsRoot = Path.Combine(root, "legacy-settings");
            Directory.CreateDirectory(settingsRoot);
            string legacyPath = Path.Combine(root, "legacy-ingest");
            File.WriteAllText(
                Path.Combine(settingsRoot, "settings.ini"),
                "DestinationPath=" + legacyPath + Environment.NewLine);

            SettingsStore store = new SettingsStore(settingsRoot, delegate { return "unused"; });
            AppSettings migrated = store.Load();

            AssertEqual(1, migrated.Destinations.Count, "Legacy settings should create one named destination.");
            AssertEqual(legacyPath, migrated.GetActiveDestination().Path, "Legacy destination path should be preserved.");
            string migratedFile = File.ReadAllText(Path.Combine(settingsRoot, "settings.ini"));
            AssertEqual(15, migrated.RecordingFrameRate, "Legacy settings should migrate to the original 15 FPS behavior.");
            AssertTrue(migratedFile.Contains("Version=15"), "Migrated settings should use version 15.");
            AssertTrue(migratedFile.Contains("RecordingQuality=Legacy"), "Legacy settings should keep the original fixed bitrate.");
            AssertTrue(migratedFile.Contains("RecordingAudioMode=Off"), "Legacy settings should migrate to video-only audio behavior.");
            AssertTrue(migratedFile.Contains("ComputerAudioGainPercent=100"), "Legacy computer gain should migrate to 100%.");
            AssertTrue(migratedFile.Contains("MicrophoneGainPercent=100"), "Legacy microphone gain should migrate to 100%.");
            AssertTrue(
                migratedFile.Contains("RoutineNotificationsEnabled=True"),
                "Legacy settings should preserve routine notifications until the user disables them.");
            AssertTrue(!migratedFile.Contains("DestinationPath="), "Legacy setting should be removed after migration.");
        }

        private static void TestRecordingFrameRateSettings()
        {
            AssertEqual(0, SettingsStore.NormalizeFrameRate(0), "Match Display should be supported.");
            AssertEqual(15, SettingsStore.NormalizeFrameRate(15), "15 FPS should be supported.");
            AssertEqual(30, SettingsStore.NormalizeFrameRate(30), "30 FPS should be supported.");
            AssertEqual(60, SettingsStore.NormalizeFrameRate(60), "60 FPS should be supported.");
            AssertEqual(15, SettingsStore.NormalizeFrameRate(47), "Unsupported frame rates should fall back safely.");
        }

        private static void TestRecordingQualitySettings(string root)
        {
            string settingsRoot = Path.Combine(root, "quality-settings");
            SettingsStore store = new SettingsStore(settingsRoot, delegate { return Path.Combine(root, "quality-destination"); });
            AppSettings settings = store.Load();
            settings.RecordingFrameRate = 0;
            settings.RecordingAudioMode = RecordingAudioMode.ComputerAndMicrophone;
            settings.ComputerAudioGainPercent = 150;
            settings.MicrophoneGainPercent = 75;
            settings.RoutineNotificationsEnabled = false;
            settings.SetShortcut(CaptureAction.ClipScreen, ShortcutBinding.FromKeyData(Keys.F9, false));
            string settingsPath = Path.Combine(settingsRoot, "settings.ini");
            foreach (RecordingQuality quality in Enum.GetValues(typeof(RecordingQuality)))
            {
                settings.RecordingQuality = quality;
                store.Save(settings);
                AppSettings reloaded = store.Load();
                AssertEqual((int)quality, (int)reloaded.RecordingQuality, "Every quality choice should survive restart.");
                AssertEqual(0, reloaded.RecordingFrameRate, "Changing quality should preserve Match Display FPS.");
            }

            settings.RecordingQuality = RecordingQuality.Legacy;
            store.Save(settings);
            string currentFile = File.ReadAllText(settingsPath);
            // Simulate earlier schemas without the new key, retaining every existing preference.
            for (int version = 1; version <= 9; version++)
            {
                File.WriteAllText(settingsPath, currentFile.Replace("Version=15", "Version=" + version)
                    .Replace("RecordingQuality=Legacy" + Environment.NewLine, String.Empty));
                AppSettings migrated = store.Load();
                AssertEqual((int)RecordingQuality.Legacy, (int)migrated.RecordingQuality, "Older settings should migrate to Legacy quality.");
                AssertEqual(0, migrated.RecordingFrameRate, "Migration should preserve selected frame rate.");
                AssertEqual((int)RecordingAudioMode.ComputerAndMicrophone, (int)migrated.RecordingAudioMode, "Migration should preserve audio selection.");
                AssertEqual(150, migrated.ComputerAudioGainPercent, "Migration should preserve computer gain.");
                AssertEqual(75, migrated.MicrophoneGainPercent, "Migration should preserve microphone gain.");
                AssertTrue(!migrated.RoutineNotificationsEnabled, "Migration should preserve silenced setting notifications.");
                AssertEqual("F9", migrated.GetShortcut(CaptureAction.ClipScreen).ToDisplayString(), "Migration should preserve custom shortcuts.");
                AssertEqual(settings.GetActiveDestination().Path, migrated.GetActiveDestination().Path, "Migration should preserve routing.");
                AssertTrue(File.ReadAllText(settingsPath).Contains("Version=15"), "Migration should write schema 15.");
            }

            foreach (string invalidQuality in new[] { "Unknown", "99", "-1", "" })
            {
                File.WriteAllText(settingsPath, currentFile.Replace("RecordingQuality=Legacy", "RecordingQuality=" + invalidQuality));
                AssertEqual((int)RecordingQuality.Legacy, (int)store.Load().RecordingQuality, "Invalid persisted quality should fall back to Legacy.");
                AssertTrue(File.ReadAllText(settingsPath).Contains("RecordingQuality=Legacy"), "Invalid quality should be repaired on disk.");
            }

            File.WriteAllText(settingsPath, currentFile.Replace("RecordingQuality=Legacy", "RecordingQuality=balanced"));
            AssertEqual((int)RecordingQuality.Balanced, (int)store.Load().RecordingQuality, "Quality names should load without case sensitivity.");
            settings.RecordingQuality = (RecordingQuality)Int32.MaxValue;
            store.Save(settings);
            AssertEqual((int)RecordingQuality.Legacy, (int)store.Load().RecordingQuality, "Saving an invalid quality should persist the safe default.");
        }

        private static void TestRecordingQualityBitrates()
        {
            AssertEqual(8000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Legacy, 3840, 2160, 240), "Legacy must retain fixed bitrate at high resolution and FPS.");
            AssertEqual(8000000, RecordingQualitySettings.CalculateBitsPerSecond((RecordingQuality)99, 1920, 1080, 60), "Invalid quality should use Legacy bitrate.");
            AssertEqual(16000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Balanced, 1920, 1080, 60), "Balanced 1080p60 should target 16 Mbps.");
            AssertEqual(24000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 1920, 1080, 60), "High 1080p60 should target 24 Mbps.");
            AssertEqual(8000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Balanced, 1920, 1080, 30), "Balanced 1080p30 should scale proportionately.");
            AssertEqual(12000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 1920, 1080, 30), "High 1080p30 should scale proportionately.");
            AssertEqual(64000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Balanced, 3840, 2160, 60), "Four times the pixels should receive four times the bitrate below the cap.");
            AssertEqual(48000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 1920, 1080, 120), "Resolved refresh rates should affect bitrate.");
            AssertEqual(2000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Balanced, 16, 16, 1), "Small Balanced captures should have a safe bitrate floor.");
            AssertEqual(3000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 16, 16, 1), "Small High captures should have a safe bitrate floor.");
            AssertEqual(80000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 3840, 2160, 60), "High 4K60 should stay within the bitrate cap.");
            AssertEqual(80000000, RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.Balanced, Int32.MaxValue, Int32.MaxValue, 240), "Large dimension arithmetic must not overflow before capping.");
            foreach (int invalidFps in new[] { Int32.MinValue, 0, 241, Int32.MaxValue })
            {
                bool rejected = false;
                try { RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 1920, 1080, invalidFps); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                AssertTrue(rejected, "Unresolved or unsupported capture FPS must be rejected.");
            }

            foreach (Size invalidSize in new[] { new Size(0, 1080), new Size(1920, -1), new Size(15, 16), new Size(16, 15) })
            {
                bool rejected = false;
                try { RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, invalidSize.Width, invalidSize.Height, 60); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                AssertTrue(rejected, "Invalid capture dimensions must be rejected.");
            }
        }

        private static void TestRecordingDiagnostics()
        {
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            RecordingDiagnostics diagnostics = new RecordingDiagnostics(60, 1920, 1080, 24000000);

            diagnostics.ObserveFrame(
                frequency * 2 / 1000,
                frequency * 4 / 1000,
                frequency * 10 / 1000,
                frequency * 16 / 1000);
            diagnostics.ObserveFrame(
                frequency * 3 / 1000,
                frequency * 20 / 1000,
                frequency * 40 / 1000,
                frequency * 33 / 1000);
            diagnostics.Complete(frequency * 2);

            AssertEqual(2, (int)diagnostics.FrameCount, "Diagnostics should count captured input frames.");
            AssertEqual(1, (int)diagnostics.MissedDeadlineCount, "Diagnostics should count late frame writes.");
            AssertTrue(
                diagnostics.AchievedFramesPerSecond > 0.99
                    && diagnostics.AchievedFramesPerSecond < 1.01,
                "Diagnostics should calculate achieved input FPS from elapsed capture time.");
            AssertTrue(
                diagnostics.AverageCaptureMilliseconds > 2.4
                    && diagnostics.AverageCaptureMilliseconds < 2.6,
                "Diagnostics should average screen-copy time.");
            AssertTrue(
                diagnostics.MaximumWriteMilliseconds > 19.9
                    && diagnostics.MaximumWriteMilliseconds < 20.1,
                "Diagnostics should retain the slowest encoder write.");
            AssertTrue(
                diagnostics.LongestLatenessMilliseconds > 6.9,
                "Diagnostics should retain the longest missed-deadline lateness.");

            string report = diagnostics.ToReportText("Completed");
            AssertTrue(report.Contains("Requested FPS: 60"), "The report should include requested FPS.");
            AssertTrue(report.Contains("Achieved input FPS: 1.000"), "The report should include achieved input FPS.");
            AssertTrue(report.Contains("Missed next-frame deadlines: 1"), "The report should include missed deadlines.");
            AssertTrue(report.Contains("selected transform identity unavailable"), "The report should state the encoder identity limitation.");
            diagnostics.AddSkippedFrameSlots(2);
            AssertEqual(2, (int)diagnostics.SkippedFrameSlots, "Diagnostics should count skipped catch-up slots.");
            diagnostics.ObserveProtectedScreenRetry(6);
            AssertEqual(1, (int)diagnostics.ProtectedScreenRetryCount,
                "Diagnostics should count a protected-screen capture retry.");
            AssertEqual(7, (int)diagnostics.ProtectedScreenSkippedFrameSlots,
                "Diagnostics should include the unavailable attempt and later protected-screen slots.");
            AssertEqual(9, (int)diagnostics.SkippedFrameSlots,
                "Protected-screen slots should remain part of the total skipped-slot count.");
            string protectedReport = diagnostics.ToReportText("Completed");
            AssertTrue(protectedReport.Contains("Protected-screen capture retries: 1"),
                "The report should identify protected-screen retries.");

            AssertTrue(
                ScreenClipRecorder.IsTemporaryProtectedScreenFailure(new Win32Exception(6)),
                "An invalid screen handle should be treated as a temporary protected-screen failure.");
            AssertTrue(
                ScreenClipRecorder.IsTemporaryProtectedScreenFailure(new Win32Exception(5)),
                "Access denied while switching desktops should be treated as temporary.");
            AssertTrue(
                !ScreenClipRecorder.IsTemporaryProtectedScreenFailure(new Win32Exception(87)),
                "Unrelated capture errors should remain fatal.");
        }

        private static void TestVideoScheduleSkipsCatchUpBursts()
        {
            long target;
            long skipped;
            long frequency = 60000;
            long next = ScreenClipRecorder.AdvanceSchedule(0, 500, 60, frequency, out target, out skipped);
            AssertEqual(1, (int)next, "An on-time capture should target the next frame slot.");
            AssertEqual(1000, (int)target, "A 60 FPS schedule should use a 16.667 ms slot at 60 kHz.");
            AssertEqual(0, (int)skipped, "An on-time capture should not skip a slot.");

            next = ScreenClipRecorder.AdvanceSchedule(0, 2400, 60, frequency, out target, out skipped);
            AssertEqual(3, (int)next, "A late capture should advance to the next future slot.");
            AssertEqual(3000, (int)target, "The new target should be the first slot after now.");
            AssertEqual(2, (int)skipped, "A 40 ms stall should skip two 60 FPS catch-up slots.");
        }

        private static void TestRecordingPreferenceSaveRollback(string root)
        {
            string settingsRoot = Path.Combine(root, "recording-save");
            SettingsStore store = new SettingsStore(settingsRoot, delegate { return Path.Combine(root, "preset-destination"); });
            AppSettings settings = store.Load();
            settings.RecordingFrameRate = 30;
            settings.RecordingQuality = RecordingQuality.Balanced;
            settings.RecordingAudioMode = RecordingAudioMode.Microphone;
            settings.ComputerAudioGainPercent = 200;
            settings.MicrophoneGainPercent = 75;
            settings.RoutineNotificationsEnabled = false;
            store.Save(settings);
            string settingsPath = Path.Combine(settingsRoot, "settings.ini");
            string previousFile = File.ReadAllText(settingsPath);
            using (FileStream lockedSettings = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool rejected = false;
                try { store.SaveRecordingPreferences(settings, 60, RecordingQuality.High); }
                catch (IOException) { rejected = true; }
                AssertTrue(rejected, "A locked settings file should fail saving the gaming preset.");
                AssertEqual(30, settings.RecordingFrameRate, "Failed preset save should restore FPS.");
                AssertEqual((int)RecordingQuality.Balanced, (int)settings.RecordingQuality, "Failed preset save should restore quality.");
            }
            AssertEqual(previousFile, File.ReadAllText(settingsPath), "A failed locked-file save should preserve existing preferences on disk.");
            store.SaveRecordingPreferences(settings, 60, RecordingQuality.High);
            AppSettings saved = store.Load();
            AssertEqual(60, saved.RecordingFrameRate, "Gaming preset should save 60 FPS.");
            AssertEqual((int)RecordingQuality.High, (int)saved.RecordingQuality, "Gaming preset should save High quality.");
            AssertEqual((int)RecordingAudioMode.Microphone, (int)saved.RecordingAudioMode, "Gaming preset should preserve audio choice.");
            AssertEqual(200, saved.ComputerAudioGainPercent, "Gaming preset should preserve computer gain.");
            AssertEqual(75, saved.MicrophoneGainPercent, "Gaming preset should preserve microphone gain.");
            AssertTrue(!saved.RoutineNotificationsEnabled, "Gaming preset should preserve notification preference.");
            AssertEqual(settings.GetActiveDestination().Path, saved.GetActiveDestination().Path, "Gaming preset should preserve capture destination.");
            store.SaveRecordingPreferences(settings, 15, settings.RecordingQuality);
            AssertEqual((int)RecordingQuality.High, (int)store.Load().RecordingQuality, "Changing FPS should preserve quality independently.");
        }

        private static void TestFrameRowCopy()
        {
            const int width = 3;
            const int height = 4;
            const int rowBytes = width * 4;
            const int stride = rowBytes + 8;
            foreach (bool negativeStride in new[] { false, true })
            {
                IntPtr source = Marshal.AllocHGlobal(stride * height);
                IntPtr destination = Marshal.AllocHGlobal(rowBytes * height + 16);
                try
                {
                    byte[] sourceBytes = new byte[stride * height];
                    byte[] expected = new byte[rowBytes * height];
                    for (int row = 0; row < height; row++)
                    {
                        int physicalRow = negativeStride ? height - 1 - row : row;
                        for (int column = 0; column < rowBytes; column++)
                        {
                            byte value = (byte)(row * 30 + column + 1);
                            sourceBytes[physicalRow * stride + column] = value;
                            expected[row * rowBytes + column] = value;
                        }
                        for (int column = rowBytes; column < stride; column++)
                        {
                            sourceBytes[physicalRow * stride + column] = 0xFE;
                        }
                    }
                    Marshal.Copy(sourceBytes, 0, source, sourceBytes.Length);
                    byte[] actual = new byte[expected.Length + 16];
                    for (int index = 0; index < actual.Length; index++) { actual[index] = 0xCC; }
                    Marshal.Copy(actual, 0, destination, actual.Length);
                    BitmapData data = new BitmapData();
                    data.Scan0 = negativeStride ? IntPtr.Add(source, stride * (height - 1)) : source;
                    data.Stride = negativeStride ? -stride : stride;
                    MediaFoundationVideoWriter.CopyFrameRows(data, destination, width, height);
                    Marshal.Copy(destination, actual, 0, actual.Length);
                    bool matches = true;
                    for (int index = 0; index < expected.Length; index++)
                    {
                        matches &= actual[index] == expected[index];
                    }
                    AssertTrue(matches, "Frame copy should preserve row order and exclude padding for both stride signs.");
                    bool boundsPreserved = true;
                    for (int index = expected.Length; index < actual.Length; index++)
                    {
                        boundsPreserved &= actual[index] == 0xCC;
                    }
                    AssertTrue(boundsPreserved, "Frame copy must not write past the destination pixels.");
                }
                finally
                {
                    Marshal.FreeHGlobal(source);
                    Marshal.FreeHGlobal(destination);
                }
            }
        }

        private static void TestRecordingAudioSettings()
        {
            AssertTrue(Enum.IsDefined(typeof(RecordingAudioMode), RecordingAudioMode.Off), "Audio Off should be supported.");
            AssertTrue(Enum.IsDefined(typeof(RecordingAudioMode), RecordingAudioMode.Computer), "Computer Audio should be supported.");
            AssertTrue(Enum.IsDefined(typeof(RecordingAudioMode), RecordingAudioMode.Microphone), "Microphone audio should be supported.");
            AssertTrue(Enum.IsDefined(typeof(RecordingAudioMode), RecordingAudioMode.ComputerAndMicrophone), "Mixed audio should be supported.");
            AssertEqual(0, SettingsStore.NormalizeAudioGainPercent(0), "Zero gain should mute a source.");
            AssertEqual(300, SettingsStore.NormalizeAudioGainPercent(300), "Three-hundred-percent gain should be supported.");
            AssertEqual(100, SettingsStore.NormalizeAudioGainPercent(301), "Out-of-range gain should fall back safely.");
        }

        private static void TestPcmAudioMixer()
        {
            AssertEqual(2000, (int)PcmAudioMixer.MixSamples(1000, 200, 0, 100), "Computer gain should amplify samples.");
            AssertEqual(1500, (int)PcmAudioMixer.MixSamples(1000, 100, 500, 100), "Computer and microphone samples should mix.");
            AssertEqual(500, (int)PcmAudioMixer.MixSamples(1000, 0, 500, 100), "A zero gain should mute only its source.");
            AssertEqual((int)Int16.MaxValue, (int)PcmAudioMixer.MixSamples(30000, 200, 10000, 200), "Positive overflow should clip safely.");
            AssertEqual((int)Int16.MinValue, (int)PcmAudioMixer.MixSamples(-30000, 200, -10000, 200), "Negative overflow should clip safely.");

            AssertEqual(2000, (int)PcmAudioMixer.ApplyGain(1000, 200), "A single track should apply its own gain.");
            AssertEqual(0, (int)PcmAudioMixer.ApplyGain(1000, 0), "A zero gain should mute its own track.");
            AssertEqual((int)Int16.MaxValue, (int)PcmAudioMixer.ApplyGain(30000, 200), "A single track should clip positively.");
            AssertEqual((int)Int16.MinValue, (int)PcmAudioMixer.ApplyGain(-30000, 200), "A single track should clip negatively.");

            PcmAudioFormat format = new PcmAudioFormat(48000, 2, 16);
            AssertEqual(2, new PcmAudioMixer(format, true, true, 100, 100, true).AudioTrackCount,
                "Computer plus microphone should record two separate tracks.");
            AssertTrue(!new PcmAudioMixer(format, true, false, 100, 100, true).SeparateTracks,
                "One source cannot be split into two tracks.");
            AssertTrue(!new PcmAudioMixer(format, true, true, 100, 100, false).SeparateTracks,
                "A single-track recording must keep mixing both sources.");
            AssertEqual(1, new PcmAudioMixer(format, false, true, 100, 100, true).AudioTrackCount,
                "A microphone-only recording stays one track.");
        }

        private static void TestRecordingResolution()
        {
            // A ceiling caps height and keeps the aspect ratio.
            Size scaled = RecordingResolutionSettings.Resolve(3840, 2160, RecordingResolutionCeiling.P1080);
            AssertEqual(1920, scaled.Width, "A 4K source should scale to 1920 wide at the 1080p ceiling.");
            AssertEqual(1080, scaled.Height, "A 4K source should scale to 1080 tall at the 1080p ceiling.");

            Size ultrawide = RecordingResolutionSettings.Resolve(3440, 1440, RecordingResolutionCeiling.P720);
            AssertEqual(720, ultrawide.Height, "An ultrawide source should honour the 720p ceiling.");
            AssertEqual(1720, ultrawide.Width, "An ultrawide source should keep its aspect ratio.");

            // A ceiling never enlarges a smaller source.
            Size small = RecordingResolutionSettings.Resolve(1280, 720, RecordingResolutionCeiling.P1440);
            AssertEqual(1280, small.Width, "A 720p source must not be enlarged to 1440p.");
            AssertEqual(720, small.Height, "A 720p source must keep its own height.");

            Size exact = RecordingResolutionSettings.Resolve(1920, 1080, RecordingResolutionCeiling.P1080);
            AssertEqual(1080, exact.Height, "A source already at the ceiling is left alone.");

            Size native = RecordingResolutionSettings.Resolve(2560, 1440, RecordingResolutionCeiling.Native);
            AssertEqual(2560, native.Width, "Native keeps the full captured width.");
            AssertEqual(1440, native.Height, "Native keeps the full captured height.");

            // H.264 needs even dimensions on both axes.
            Size odd = RecordingResolutionSettings.Resolve(1921, 1081, RecordingResolutionCeiling.Native);
            AssertEqual(1920, odd.Width, "An odd width trims one pixel.");
            AssertEqual(1080, odd.Height, "An odd height trims one pixel.");
            Size oddScaled = RecordingResolutionSettings.Resolve(1366, 768, RecordingResolutionCeiling.P720);
            AssertEqual(0, oddScaled.Width % 2, "A scaled width must stay even.");
            AssertEqual(0, oddScaled.Height % 2, "A scaled height must stay even.");

            AssertEqual("1080p", RecordingResolutionSettings.GetLabel(RecordingResolutionCeiling.P1080),
                "The 1080p ceiling should be labelled plainly.");
            AssertEqual("Native", RecordingResolutionSettings.GetLabel(RecordingResolutionCeiling.Native),
                "The default ceiling should read as Native.");
            AssertEqual("1440p", RecordingResolutionSettings.GetLabel(RecordingResolutionSettings.Parse("P1440")),
                "A stored ceiling should round-trip.");
            AssertEqual("Native", RecordingResolutionSettings.GetLabel(RecordingResolutionSettings.Parse("nonsense")),
                "An unreadable ceiling falls back to Native.");
            AssertEqual("Native", RecordingResolutionSettings.GetLabel(RecordingResolutionSettings.Parse("")),
                "A missing ceiling falls back to Native.");
        }

        private static void TestDeliveryReporting()
        {
            RecordingDiagnostics diagnostics = new RecordingDiagnostics(60, 1920, 1080, 16000000);
            for (int i = 0; i < 45; i++) diagnostics.ObserveFrame(0, 0, 0, Int64.MaxValue);
            diagnostics.Complete(System.Diagnostics.Stopwatch.Frequency);

            AssertEqual("45 new FPS / 60 max", diagnostics.DeliverySummary,
                "Delivery must report new frames against the requested maximum.");
            AssertEqual(0, (int)diagnostics.EncoderDropCount, "A healthy recording has no encoder drops.");
            AssertEqual("45 new FPS / 60 max", RecordingDiagnostics.DescribeDelivery(diagnostics),
                "Fewer new frames than requested must not be called an encoder drop.");

            diagnostics.ObserveEncoderDrop();
            AssertEqual("45 new FPS / 60 max — 1 encoder drop", RecordingDiagnostics.DescribeDelivery(diagnostics),
                "A real writer rejection must be reported as an encoder drop.");
            diagnostics.ObserveEncoderDrop();
            AssertEqual("45 new FPS / 60 max — 2 encoder drops", RecordingDiagnostics.DescribeDelivery(diagnostics),
                "Multiple encoder drops must be pluralized.");

            AssertEqual("0", RecordingDiagnostics.FormatDelivered(-5.0), "Delivery can never be negative.");
            AssertEqual("30", RecordingDiagnostics.FormatDelivered(29.6), "Delivery rounds to whole frames.");
            AssertTrue(RecordingDiagnostics.DescribeDelivery(null) == null,
                "A capture without diagnostics reports no delivery claim.");
        }

        private static void TestAudioPeakTracker()
        {
            AudioPeakTracker tracker = new AudioPeakTracker();
            AssertEqual(0, tracker.TakePeak(), "A new audio tracker should begin silent.");
            tracker.Observe(500);
            tracker.Observe(-1200);
            AssertEqual(1200, tracker.TakePeak(), "The tracker should retain the largest absolute sample.");
            AssertEqual(0, tracker.TakePeak(), "Reading the audio peak should reset its window.");
            tracker.Observe(Int16.MinValue);
            AssertEqual(32768, tracker.TakePeak(), "The tracker should handle the full negative PCM range.");
        }

        private static void TestShortcutCatalogOrder()
        {
            CaptureAction[] actions = ShortcutCatalog.Actions;
            AssertEqual(6, actions.Length, "Every capture action should appear in the shortcut catalog.");
            AssertEqual((int)CaptureAction.SnipRegion, (int)actions[0], "Snips should begin with Region.");
            AssertEqual((int)CaptureAction.SnipWindow, (int)actions[1], "Snip Window should follow Snip Region.");
            AssertEqual((int)CaptureAction.SnipScreen, (int)actions[2], "Snip Screen should finish the snip group.");
            AssertEqual((int)CaptureAction.ClipRegion, (int)actions[3], "Clips should begin with Region.");
            AssertEqual((int)CaptureAction.ClipWindow, (int)actions[4], "Clip Window should follow Clip Region.");
            AssertEqual((int)CaptureAction.ClipScreen, (int)actions[5], "Clip Screen should finish the clip group.");
        }

        private static void TestCaptureActionMenuRow()
        {
            int actionInvocations = 0;
            int rebindInvocations = 0;
            using (CaptureActionMenuRow row = new CaptureActionMenuRow(
                "Snip Region",
                "Ctrl+Alt+Shift+S",
                true,
                true,
                "Capture a region."))
            {
                row.ActionInvoked += delegate { actionInvocations++; };
                row.RebindRequested += delegate { rebindInvocations++; };
                row.PerformLayout();

                AssertEqual("Snip Region", row.ActionText, "The action side should show the capture action.");
                AssertEqual("Ctrl+Alt+Shift+S", row.BindingText, "The binding side should show the current shortcut.");
                AssertTrue(row.ActionBounds.Width > 0, "The action side should have clickable width.");
                AssertTrue(row.BindingBounds.Width > 0, "The binding side should have clickable width.");
                AssertTrue(row.ActionBounds.Right < row.BindingBounds.Left, "The two click targets should not overlap.");
                AssertTrue(
                    row.BindingBounds.Width > row.ActionBounds.Width,
                    "The shortcut side should receive more room than the short action label.");

                row.SimulateActionPointerEnterForTest();
                AssertTrue(row.ActionHighlighted, "Hovering an action should highlight its background.");
                AssertEqual(
                    SystemColors.Highlight.ToArgb(),
                    row.ActionHoverBackColor.ToArgb(),
                    "An action hover should paint a complete blue background.");
                AssertEqual(
                    SystemColors.HighlightText.ToArgb(),
                    row.ActionTextColor.ToArgb(),
                    "An action hover should pair the blue background with highlight text.");
                row.SimulateActionPointerLeaveForTest();
                AssertTrue(!row.ActionHighlighted, "Leaving an action should always clear its highlight.");
                AssertEqual(
                    SystemColors.Menu.ToArgb(),
                    row.ActionHoverBackColor.ToArgb(),
                    "Leaving an action should restore the off-white menu hover layer.");
                AssertEqual(
                    SystemColors.MenuText.ToArgb(),
                    row.ActionTextColor.ToArgb(),
                    "Leaving an action should restore ordinary menu text.");

                row.InvokeActionForTest();
                row.InvokeRebindForTest();
                AssertEqual(1, actionInvocations, "The action side should invoke capture independently.");
                AssertEqual(1, rebindInvocations, "The binding side should invoke rebinding independently.");

                row.SimulateBindingPointerEnterForTest();
                row.BeginBindingCapture();
                AssertEqual("Press shortcut…", row.BindingText, "Rebinding should announce itself inside the row.");
                AssertTrue(row.BindingHighlighted, "The chosen shortcut button should be highlighted while listening.");
                AssertEqual(
                    SystemColors.Highlight.ToArgb(),
                    row.BindingHoverBackColor.ToArgb(),
                    "The shortcut hover layer should stay blue behind its white text.");
                AssertEqual(
                    SystemColors.HighlightText.ToArgb(),
                    row.BindingTextColor.ToArgb(),
                    "A highlighted shortcut should use the matching highlight text colour.");
                row.ShowPendingBinding(ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.C, false), 3);
                AssertEqual(
                    "Ctrl+Alt+C, … 3",
                    row.BindingText,
                    "A captured chord prefix should show how long the second step has.");
                row.ShowPendingBinding(ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.C, false), 1);
                AssertEqual(
                    "Ctrl+Alt+C, … 1",
                    row.BindingText,
                    "The chord countdown should stay visible as the window closes.");
                row.ShowPendingBinding(
                    ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.Shift | Keys.C, false),
                    3);
                AssertTrue(
                    TextRenderer.MeasureText(
                        row.BindingText,
                        row.Font,
                        Size.Empty,
                        TextFormatFlags.SingleLine).Width <= row.BindingBounds.Width,
                    "The chord countdown should fit the shortcut button without being clipped.");

                row.CompleteBindingCapture("Ctrl+Alt+Shift+1, Ctrl+Alt+Shift+2");
                AssertEqual(
                    "Ctrl+Alt+Shift+1, Ctrl+Alt+Shift+2",
                    row.BindingText,
                    "The completed binding should replace the prompt in place.");
                AssertTrue(
                    TextRenderer.MeasureText(
                        row.BindingText,
                        row.Font,
                        Size.Empty,
                        TextFormatFlags.SingleLine).Width <= row.BindingBounds.Width,
                    "A long two-step chord should be fully visible without relying on its tooltip.");
                AssertTrue(
                    !row.BindingHighlighted,
                    "An accepted shortcut button should lose its selected look even under the pointer.");
                AssertEqual(
                    SystemColors.Menu.ToArgb(),
                    row.BindingHoverBackColor.ToArgb(),
                    "A completed shortcut should restore the normal menu hover layer while suppressed.");
                AssertEqual(
                    SystemColors.MenuText.ToArgb(),
                    row.BindingTextColor.ToArgb(),
                    "A completed shortcut should restore readable dark menu text.");
                row.SimulateBindingPointerMoveForTest();
                AssertTrue(
                    row.BindingHighlighted,
                    "Moving the pointer again should restore the ordinary hover highlight.");
                row.SimulateBindingPointerLeaveForTest();
                AssertTrue(!row.BindingHighlighted, "Leaving the shortcut button should clear its hover highlight.");

                row.Scale(new SizeF(1.5f, 1.5f));
                row.PerformLayout();
                AssertTrue(
                    row.BindingBounds.Right <= row.ClientSize.Width,
                    "The binding button should remain inside the row after display scaling.");
            }

            using (CaptureActionMenuRow disabled = new CaptureActionMenuRow(
                "Clip Screen",
                "Ctrl+Alt+Shift+R",
                false,
                false,
                "Record a screen."))
            {
                disabled.ActionInvoked += delegate { actionInvocations++; };
                disabled.RebindRequested += delegate { rebindInvocations++; };
                disabled.InvokeActionForTest();
                disabled.InvokeRebindForTest();
                AssertEqual(1, actionInvocations, "A disabled action side should not invoke capture.");
                AssertEqual(1, rebindInvocations, "A disabled binding side should not invoke rebinding.");
            }
        }

        private static void TestShortcutSettings(string root)
        {
            AppSettings settings = new AppSettings();
            AssertEqual(
                "Ctrl+Alt+Shift+S",
                settings.GetShortcut(CaptureAction.SnipRegion).ToDisplayString(),
                "Snip Region should have its documented default shortcut.");
            AssertEqual(
                "Ctrl+Alt+Shift+V",
                settings.GetShortcut(CaptureAction.ClipWindow).ToDisplayString(),
                "Clip Window should have its documented default shortcut.");

            ShortcutBinding numeric = new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL,
                Key = Keys.NumPad3
            };
            AssertEqual("Ctrl+Num 3", numeric.ToDisplayString(), "Numeric keypad shortcuts should be readable.");

            ShortcutBinding bareKey = ShortcutBinding.FromKeyData(Keys.R, false);
            AssertTrue(bareKey.IsValid(), "A modifier-free key should be valid.");
            AssertEqual("R", bareKey.ToDisplayString(), "A bare key should have a concise label.");

            ShortcutBinding ctrlShiftR = ShortcutBinding.FromKeyData(
                Keys.Control | Keys.Shift | Keys.R,
                false);
            AssertEqual("Ctrl+Shift+R", ctrlShiftR.ToDisplayString(), "Ctrl+Shift+R should capture without Alt.");

            ShortcutBinding winF1 = ShortcutBinding.FromKeyData(Keys.F1, true);
            AssertEqual("Win+F1", winF1.ToDisplayString(), "Windows-key combinations should be represented.");

            ShortcutBinding punctuation = ShortcutBinding.FromKeyData(Keys.OemQuestion, false);
            AssertEqual("/", punctuation.ToDisplayString(), "Punctuation keys should have readable labels.");

            ShortcutBinding modifierOnly = ShortcutBinding.FromKeyData(Keys.Control | Keys.ControlKey, false);
            AssertTrue(!modifierOnly.IsValid(), "A modifier key alone should not be accepted as the trigger key.");

            using (ShortcutCaptureBox captureBox = new ShortcutCaptureBox(ShortcutCatalog.GetDefault(CaptureAction.ClipScreen)))
            {
                AssertTrue(!captureBox.CaptureArmed, "A focused shortcut field should not capture until explicitly armed.");
                captureBox.BeginCapture();
                AssertTrue(captureBox.CaptureArmed, "Clicking a shortcut field should arm capture.");
                AssertTrue(
                    captureBox.TryCaptureKeyData(Keys.Control | Keys.Shift | Keys.R, false),
                    "The editor control should accept Ctrl+Shift+R.");
                AssertEqual(
                    "Ctrl+Shift+R",
                    captureBox.Binding.ToDisplayString(),
                    "The editor control should retain the captured combination.");
                captureBox.CompletePendingCapture();
                AssertTrue(!captureBox.CaptureArmed, "Finishing a shortcut should disarm capture.");
                captureBox.BeginCapture();
                AssertTrue(
                    captureBox.TryCaptureKeyData(Keys.Tab, false),
                    "The editor control should accept normally navigational keys.");
                AssertEqual("Tab", captureBox.Binding.ToDisplayString(), "Bare Tab should remain captured.");
                captureBox.CompletePendingCapture();

                captureBox.BeginCapture();
                AssertTrue(
                    captureBox.TryCaptureKeyData(Keys.Control | Keys.Alt | Keys.C, false),
                    "The editor should capture a chord prefix.");
                AssertTrue(
                    captureBox.TryCaptureKeyData(Keys.R, false),
                    "The editor should capture a chord's second key.");
                AssertEqual(
                    "Ctrl+Alt+C, R",
                    captureBox.Binding.ToDisplayString(),
                    "The editor should present a two-step chord clearly.");
                AssertTrue(captureBox.Binding.HasSecondStroke, "The captured binding should be marked as a chord.");
                AssertTrue(!captureBox.CaptureArmed, "Completing a chord should disarm capture.");
            }

            string roundTripRoot = Path.Combine(root, "shortcut-round-trip");
            SettingsStore roundTripStore = new SettingsStore(
                roundTripRoot,
                delegate { return Path.Combine(root, "round-trip-destination"); });
            AppSettings roundTripSettings = roundTripStore.Load();
            roundTripSettings.SetShortcut(CaptureAction.ClipScreen, ctrlShiftR);
            roundTripStore.Save(roundTripSettings);
            AssertEqual(
                "Ctrl+Shift+R",
                roundTripStore.Load().GetShortcut(CaptureAction.ClipScreen).ToDisplayString(),
                "Ctrl+Shift+R should survive the settings-file round trip.");

            roundTripSettings.SetShortcut(CaptureAction.ClipScreen, ShortcutBinding.FromKeyData(Keys.F9, false));
            roundTripStore.Save(roundTripSettings);
            AssertEqual(
                "F9",
                roundTripStore.Load().GetShortcut(CaptureAction.ClipScreen).ToDisplayString(),
                "A bare key should survive the settings-file round trip.");

            ShortcutBinding chord = ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.C, false)
                .WithSecondStroke(ShortcutBinding.FromKeyData(Keys.R, false));
            roundTripSettings.SetShortcut(CaptureAction.ClipScreen, chord);
            roundTripStore.Save(roundTripSettings);
            ShortcutBinding reloadedChord = roundTripStore.Load().GetShortcut(CaptureAction.ClipScreen);
            AssertEqual(
                "Ctrl+Alt+C, R",
                reloadedChord.ToDisplayString(),
                "A two-step chord should survive the settings-file round trip.");
            AssertTrue(reloadedChord.HasSecondStroke, "The reloaded binding should remain a chord.");

            string duplicateRoot = Path.Combine(root, "duplicate-shortcuts");
            SettingsStore store = new SettingsStore(duplicateRoot, delegate { return Path.Combine(root, "destination"); });
            settings.SetShortcut(CaptureAction.SnipScreen, settings.GetShortcut(CaptureAction.SnipRegion));
            bool rejected = false;
            try
            {
                store.Save(settings);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            AssertTrue(rejected, "Duplicate global shortcuts should be rejected.");

            AppSettings prefixSettings = new AppSettings();
            prefixSettings.Destinations.Add(SettingsStore.CreateDestination(
                Path.Combine(root, "prefix-destination"),
                "Prefix"));
            prefixSettings.ActiveDestinationId = prefixSettings.Destinations[0].Id;
            ShortcutBinding sharedPrefix = prefixSettings.GetShortcut(CaptureAction.SnipRegion)
                .WithSecondStroke(ShortcutBinding.FromKeyData(Keys.R, false));
            prefixSettings.SetShortcut(CaptureAction.SnipScreen, sharedPrefix);
            prefixSettings.SetShortcut(
                CaptureAction.SnipRegion,
                prefixSettings.GetShortcut(CaptureAction.SnipRegion)
                    .WithSecondStroke(ShortcutBinding.FromKeyData(Keys.T, false)));
            bool prefixRejected = false;
            try
            {
                store.Save(prefixSettings);
            }
            catch (InvalidOperationException)
            {
                prefixRejected = true;
            }

            AssertTrue(prefixRejected, "Two chord bindings with the same first stroke should be rejected.");
        }

        private static void TestDisplayRefreshRate()
        {
            AssertTrue(Screen.PrimaryScreen != null, "Windows should report a primary display.");
            int refreshRate = ScreenCaptureService.GetDisplayRefreshRate(Screen.PrimaryScreen);
            AssertTrue(refreshRate >= 15 && refreshRate <= 240, "Display refresh rate should be usable for recording.");
        }

        private static void TestCopyableDialog()
        {
            string message = "Clip Screen and Clip Region begin with the same shortcut.";
            AssertEqual(
                "Shortcut Prefix Conflict" + Environment.NewLine + Environment.NewLine + message,
                CopyableDialog.GetCopyAllText("Shortcut Prefix Conflict", message),
                "Copy All should include the popup title and complete message.");

            using (CopyableMessageForm dialog = new CopyableMessageForm(
                message,
                "Shortcut Prefix Conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1))
            {
                AssertEqual(message, dialog.MessageText, "The popup message should remain selectable text.");
                AssertEqual(2, dialog.CopyMenuItemCount, "The popup should provide Copy and Copy All commands.");
            }
        }

        private static void TestStartupManager()
        {
            AssertEqual("HucksSnipNClip", StartupManager.ValueName,
                "The app and installer must use the same current startup registry value.");
            AssertEqual("QSnipAndClip", StartupManager.LegacyValueName,
                "The retired startup value must remain recognizable for migration.");
            AssertEqual(
                "\"C:\\Program Files\\Huck's Snip 'n' Clip\\QSnipAndClip.exe\"",
                StartupManager.BuildLaunchCommand("C:\\Program Files\\Huck's Snip 'n' Clip\\QSnipAndClip.exe"),
                "The Windows startup command should safely quote paths containing spaces.");

            string current = "D:\\Source\\hucks-snip-n-clip\\artifacts\\QSnipAndClip.exe";
            string moved = "C:\\Users\\Example\\Documents\\hucks-snip-n-clip\\artifacts\\QSnipAndClip.exe";
            AssertEqual(
                (int)StartupState.Disabled,
                (int)StartupManager.ClassifyCommand(null, current),
                "A missing startup entry should read as disabled.");
            AssertEqual(
                (int)StartupState.Enabled,
                (int)StartupManager.ClassifyCommand(StartupManager.BuildLaunchCommand(current), current),
                "A startup entry naming this executable should read as enabled.");
            AssertEqual(
                (int)StartupState.EnabledForAnotherPath,
                (int)StartupManager.ClassifyCommand(StartupManager.BuildLaunchCommand(moved), current),
                "A startup entry left behind by an older copy should be recognised, not read as disabled.");

            string valueName = "QSnipAndClipTest-" + Guid.NewGuid().ToString("N");
            try
            {
                StartupManager.SetEnabled(true, valueName, moved);
                string configured;
                AssertEqual(
                    (int)StartupState.EnabledForAnotherPath,
                    (int)StartupManager.GetState(valueName, current, out configured),
                    "A stale startup entry should be visible before repair.");
                AssertTrue(
                    StartupManager.RepairStaleEntry(valueName, current),
                    "A stale startup entry should be repaired to the running executable.");
                AssertEqual(
                    (int)StartupState.Enabled,
                    (int)StartupManager.GetState(valueName, current, out configured),
                    "Repair should leave the entry pointing at the running executable.");
                AssertEqual(
                    StartupManager.BuildLaunchCommand(current),
                    configured,
                    "The repaired startup entry should hold the quoted current executable path.");
                AssertTrue(
                    !StartupManager.RepairStaleEntry(valueName, current),
                    "A correct startup entry should not be rewritten.");

                StartupManager.SetEnabled(false, valueName, current);
                AssertEqual(
                    (int)StartupState.Disabled,
                    (int)StartupManager.GetState(valueName, current, out configured),
                    "Turning startup off should remove the entry.");
                AssertTrue(
                    !StartupManager.RepairStaleEntry(valueName, current),
                    "Repair should not create a startup entry the user never asked for.");
            }
            finally
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                    true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }
            }
        }

        private static void TestUpdateChecker()
        {
            AssertEqual("0.1.6", UpdateChecker.ParseVersion("v0.1.6").ToString(3),
                "GitHub v-prefixed release tags should parse.");
            AssertEqual("0.1.6", UpdateChecker.ParseVersion("0.1.6").ToString(3),
                "Plain release versions should parse.");
            string hash = new string('a', 64);
            AssertEqual(hash, UpdateChecker.ParseChecksum(hash + "  installer.exe"),
                "A standard SHA-256 file should yield its hash token.");
            AssertTrue(new UpdateCheckResult
            {
                CurrentVersion = new Version(0, 1, 5),
                LatestVersion = new Version(0, 1, 6)
            }.UpdateAvailable, "A newer GitHub release should be offered.");
            AssertTrue(!new UpdateCheckResult
            {
                CurrentVersion = new Version(0, 1, 5),
                LatestVersion = new Version(0, 1, 5)
            }.UpdateAvailable, "The installed release should not update to itself.");
            AssertTrue(UpdateChecker.LatestReleaseApiUrl.Contains("Huckletsplay/hucks-snip-n-clip"),
                "Update checks must target the public download repository.");
        }

        private static void TestVideoBoundsNormalization()
        {
            Rectangle normalized = ScreenClipRecorder.NormalizeVideoBounds(
                new Rectangle(-1919, 37, 641, 481));
            AssertEqual(-1919, normalized.X, "Video bounds should preserve a negative desktop X coordinate.");
            AssertEqual(37, normalized.Y, "Video bounds should preserve the desktop Y coordinate.");
            AssertEqual(640, normalized.Width, "Odd video width should trim one pixel.");
            AssertEqual(480, normalized.Height, "Odd video height should trim one pixel.");

            bool rejected = false;
            try
            {
                ScreenClipRecorder.NormalizeVideoBounds(new Rectangle(0, 0, 15, 100));
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }

            AssertTrue(rejected, "A video region smaller than 16 pixels should be rejected.");
        }

        private static void TestPngSave(string root)
        {
            string destination = Path.Combine(root, "snips");
            string recovery = Path.Combine(root, "recovery");
            CaptureSaveService service = new CaptureSaveService(recovery);

            using (Bitmap image = new Bitmap(7, 5))
            {
                image.SetPixel(0, 0, Color.Magenta);
                CaptureSaveResult result = service.SavePng(image, destination);

                AssertTrue(result.Succeeded, "PNG save should succeed.");
                AssertTrue(!result.UsedRecovery, "Normal save should not use recovery.");
                AssertTrue(File.Exists(result.SavedPath), "Saved PNG should exist.");
                AssertTrue(
                    Path.GetFileName(result.SavedPath).StartsWith(
                        "HucksSnipNClip_Snip_",
                        StringComparison.Ordinal),
                    "New snips should use Huck's public filename prefix.");

                using (Bitmap decoded = new Bitmap(result.SavedPath))
                {
                    AssertEqual(7, decoded.Width, "Saved PNG width should match.");
                    AssertEqual(5, decoded.Height, "Saved PNG height should match.");
                }
            }
        }

        private static void TestRecoveryFallback(string root)
        {
            string blockedDestination = Path.Combine(root, "blocked-destination");
            File.WriteAllText(blockedDestination, "This file prevents a directory with the same path.");

            string recovery = Path.Combine(root, "fallback-recovery");
            CaptureSaveService service = new CaptureSaveService(recovery);

            using (Bitmap image = new Bitmap(3, 2))
            {
                CaptureSaveResult result = service.SavePng(image, blockedDestination);
                AssertTrue(result.Succeeded, "Recovery save should succeed.");
                AssertTrue(result.UsedRecovery, "Blocked destination should use recovery.");
                AssertTrue(File.Exists(result.SavedPath), "Recovered PNG should exist.");
                AssertTrue(result.Error != null, "Recovery result should retain the destination error.");
            }
        }

        private static void TestClipRouting(string root)
        {
            string workingDirectory = Path.Combine(root, "working");
            string destination = Path.Combine(root, "clips");
            string recovery = Path.Combine(root, "clip-recovery");
            Directory.CreateDirectory(workingDirectory);
            string workingPath = Path.Combine(workingDirectory, "completed.mp4");
            File.WriteAllBytes(workingPath, new byte[] { 1, 2, 3, 4, 5 });

            CaptureSaveService service = new CaptureSaveService(recovery);
            CaptureSaveResult result = service.RouteCompletedClip(workingPath, destination);

            AssertTrue(result.Succeeded, "Clip routing should succeed.");
            AssertTrue(!result.UsedRecovery, "Normal clip routing should not use recovery.");
            AssertTrue(File.Exists(result.SavedPath), "Routed clip should exist.");
            AssertTrue(
                Path.GetFileName(result.SavedPath).StartsWith(
                    "HucksSnipNClip_Clip_",
                    StringComparison.Ordinal),
                "New clips should use Huck's public filename prefix.");
            AssertTrue(!File.Exists(workingPath), "Working clip should be removed after a verified route.");
            AssertTrue(result.SavedPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), "Clip should retain the MP4 extension.");

            string blockedDestination = Path.Combine(root, "blocked-clip-destination");
            File.WriteAllText(blockedDestination, "This file prevents a destination directory.");
            string recoveryWorkingPath = Path.Combine(workingDirectory, "recovery-completed.mp4");
            File.WriteAllBytes(recoveryWorkingPath, new byte[] { 6, 7, 8, 9 });
            CaptureSaveResult recoveryResult = service.RouteCompletedClip(recoveryWorkingPath, blockedDestination);

            AssertTrue(recoveryResult.Succeeded, "Clip recovery routing should succeed.");
            AssertTrue(recoveryResult.UsedRecovery, "A blocked clip destination should use recovery.");
            AssertTrue(File.Exists(recoveryResult.SavedPath), "Recovered clip should exist.");
            AssertTrue(!File.Exists(recoveryWorkingPath), "Working clip should be removed after recovery routing.");
        }

        private static void TestNativeMp4Encoding(string root)
        {
            string path = Path.Combine(root, "native-encoder-test.mp4");
            PcmAudioFormat audioFormat = new PcmAudioFormat(48000, 2, 16);
            using (MediaFoundationVideoWriter writer = new MediaFoundationVideoWriter(
                path,
                320,
                180,
                15,
                RecordingQualitySettings.CalculateBitsPerSecond(RecordingQuality.High, 320, 180, 15),
                audioFormat))
            using (Bitmap frame = new Bitmap(320, 180))
            {
                for (int index = 0; index < 15; index++)
                {
                    using (Graphics graphics = Graphics.FromImage(frame))
                    {
                        graphics.Clear(Color.FromArgb(20 + (index * 10), 70, 150));
                        graphics.FillRectangle(Brushes.White, index * 12, 70, 50, 40);
                    }

                    writer.WriteFrame(frame);
                    int audioFrames = audioFormat.SampleRate / 15;
                    byte[] audio = CreateSineWave(audioFormat, audioFrames, index * audioFrames);
                    writer.WriteAudio(audio, audioFrames, index * 10000000L / 15);
                }

                writer.FinalizeVideo();
            }

            AssertTrue(File.Exists(path), "Native MP4 encoder should create a file.");
            FileInfo file = new FileInfo(path);
            AssertTrue(file.Length > 1000, "Native MP4 should contain encoded video data.");

            byte[] header = new byte[12];
            using (FileStream stream = File.OpenRead(path))
            {
                AssertEqual(header.Length, stream.Read(header, 0, header.Length), "MP4 header should be readable.");
            }

            string boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            AssertEqual("ftyp", boxType, "Native recording should use an MP4 container.");
            string containerMarkers = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
            AssertTrue(containerMarkers.Contains("soun"), "Native recording should contain an audio track handler.");
            AssertTrue(containerMarkers.Contains("mp4a"), "Native recording should contain AAC audio samples.");
        }

        private static void TestWasapiLoopbackInitialization()
        {
            using (WasapiLoopbackCapture capture = new WasapiLoopbackCapture())
            {
                AssertEqual(48000, capture.Format.SampleRate, "Computer audio should use a stable 48 kHz rate.");
                AssertEqual(2, capture.Format.Channels, "Computer audio should be captured as stereo.");
                AssertEqual(16, capture.Format.BitsPerSample, "Computer audio should be captured as 16-bit PCM.");
                capture.Start();
            }
        }

        private static void TestWasapiMicrophoneInitialization()
        {
            Exception captureError = null;
            PcmAudioFormat captureFormat = null;
            Thread captureThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (WasapiMicrophoneCapture capture = new WasapiMicrophoneCapture())
                    {
                        captureFormat = capture.Format;
                        capture.Start();
                    }
                }
                catch (Exception exception)
                {
                    captureError = exception;
                }
            }));
            captureThread.SetApartmentState(ApartmentState.MTA);
            captureThread.Start();
            captureThread.Join();

            if (captureError != null)
            {
                throw new InvalidOperationException("Microphone capture failed on its recording thread.", captureError);
            }

            AssertEqual(48000, captureFormat.SampleRate, "Microphone audio should use a stable 48 kHz rate.");
            AssertEqual(2, captureFormat.Channels, "Microphone audio should be converted to stereo.");
            AssertEqual(16, captureFormat.BitsPerSample, "Microphone audio should be converted to 16-bit PCM.");
        }

        private static byte[] CreateSineWave(PcmAudioFormat format, int frames, int startFrame)
        {
            byte[] data = new byte[frames * format.BlockAlign];
            for (int frame = 0; frame < frames; frame++)
            {
                double phase = 2.0 * Math.PI * 440.0 * (startFrame + frame) / format.SampleRate;
                short sample = (short)(Math.Sin(phase) * 8000.0);
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int offset = (frame * format.BlockAlign) + (channel * 2);
                    data[offset] = (byte)(sample & 0xff);
                    data[offset + 1] = (byte)((sample >> 8) & 0xff);
                }
            }

            return data;
        }

        private static void TestShortcutConflictDetection()
        {
            Dictionary<CaptureAction, ShortcutBinding> bindings =
                new Dictionary<CaptureAction, ShortcutBinding>();
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                bindings[action] = ShortcutCatalog.GetDefault(action);
            }

            CaptureAction other;
            AssertEqual(
                (int)ShortcutConflictKind.None,
                (int)ShortcutConflicts.Find(
                    CaptureAction.ClipRegion,
                    ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.J, false),
                    bindings,
                    out other),
                "An unused combination should not report a conflict.");

            ShortcutBinding takenPrefix = bindings[CaptureAction.ClipScreen].Clone();
            AssertEqual(
                (int)ShortcutConflictKind.SameFirstStroke,
                (int)ShortcutConflicts.Find(CaptureAction.ClipRegion, takenPrefix, bindings, out other),
                "Reusing another action's shortcut should be reported as a prefix conflict.");
            AssertEqual((int)CaptureAction.ClipScreen, (int)other, "The conflict should name the other action.");

            ShortcutBinding secondStepCollision = bindings[CaptureAction.ClipRegion]
                .WithSecondStroke(bindings[CaptureAction.ClipScreen].GetFirstStroke());
            AssertEqual(
                (int)ShortcutConflictKind.SecondStrokeStartsAnotherAction,
                (int)ShortcutConflicts.Find(CaptureAction.ClipRegion, secondStepCollision, bindings, out other),
                "A chord whose second step is another action's shortcut should be rejected clearly.");
            AssertEqual((int)CaptureAction.ClipScreen, (int)other, "The step conflict should name the other action.");

            string stepMessage = ShortcutConflicts.Describe(
                ShortcutConflictKind.SecondStrokeStartsAnotherAction,
                CaptureAction.ClipRegion,
                secondStepCollision,
                CaptureAction.ClipScreen,
                bindings[CaptureAction.ClipScreen]);
            AssertTrue(
                stepMessage.Contains("Clip Screen") && stepMessage.Contains("second step"),
                "The step-conflict explanation should name the other action instead of blaming Windows.");
            AssertTrue(
                !stepMessage.Contains("another program has reserved"),
                "A conflict Q creates for itself should not be described as another program's.");

            bindings[CaptureAction.ClipRegion] = secondStepCollision;
            AssertEqual(
                (int)ShortcutConflictKind.FirstStrokeCompletesAnotherChord,
                (int)ShortcutConflicts.Find(
                    CaptureAction.SnipRegion,
                    bindings[CaptureAction.ClipScreen].GetFirstStroke(),
                    bindings,
                    out other),
                "Claiming a combination that completes an existing chord should be reported.");

            AppSettings inconsistent = new AppSettings();
            inconsistent.Destinations.Add(SettingsStore.CreateDestination(
                Path.Combine(Path.GetTempPath(), "QSNC-conflict-destination"),
                "Conflict"));
            inconsistent.ActiveDestinationId = inconsistent.Destinations[0].Id;
            inconsistent.SetShortcut(
                CaptureAction.ClipRegion,
                inconsistent.GetShortcut(CaptureAction.ClipRegion)
                    .WithSecondStroke(inconsistent.GetShortcut(CaptureAction.ClipScreen).GetFirstStroke()));
            string conflictRoot = Path.Combine(
                Path.GetTempPath(),
                "QSNC-Tests-conflict-" + Guid.NewGuid().ToString("N"));
            SettingsStore conflictStore = new SettingsStore(
                conflictRoot,
                delegate { return Path.Combine(conflictRoot, "destination"); });
            bool rejected = false;
            try
            {
                conflictStore.Save(inconsistent);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            finally
            {
                if (Directory.Exists(conflictRoot))
                {
                    Directory.Delete(conflictRoot, true);
                }
            }

            AssertTrue(
                rejected,
                "Settings should refuse a chord whose second step is another action's shortcut.");
        }

        private static void TestRegistrationFailureMessages()
        {
            ShortcutBinding chord = ShortcutBinding.FromKeyData(Keys.Control | Keys.Alt | Keys.C, false)
                .WithSecondStroke(ShortcutBinding.FromKeyData(Keys.R, false));

            string firstStrokeMessage = ShortcutConflicts.DescribeRegistrationFailure(
                CaptureAction.ClipRegion,
                chord,
                false);
            AssertTrue(
                firstStrokeMessage.Contains("Ctrl+Alt+C") && !firstStrokeMessage.Contains("Ctrl+Alt+C, R"),
                "A refused first stroke should be named on its own.");

            string secondStrokeMessage = ShortcutConflicts.DescribeRegistrationFailure(
                CaptureAction.ClipRegion,
                chord,
                true);
            AssertTrue(
                secondStrokeMessage.Contains("second step, R"),
                "A refused second step should be named as the second step.");
        }

        /// <summary>
        /// Drives the real capture session with injected keystrokes. This is the path that made a
        /// chord look accepted while only its first stroke was saved.
        /// </summary>
        private static void TestShortcutCaptureChordWindow()
        {
            ShortcutBinding fastChord = CaptureInjectedShortcut(400, 1200, true);
            AssertTrue(fastChord != null, "An injected chord should complete the capture session.");
            AssertEqual(
                "Ctrl+Alt+C, R",
                fastChord.ToDisplayString(),
                "A promptly typed second stroke should produce a two-step chord.");
            AssertTrue(fastChord.HasSecondStroke, "The captured binding should be marked as a chord.");

            ShortcutBinding slowChord = CaptureInjectedShortcut(2200, 3000, true);
            AssertTrue(slowChord != null, "A slower second stroke should still complete the session.");
            AssertEqual(
                "Ctrl+Alt+C, R",
                slowChord.ToDisplayString(),
                "A second stroke typed after the old 1.5 second window should still form a chord.");

            ShortcutBinding lapsed = CaptureInjectedShortcut(0, 600, false);
            AssertTrue(lapsed != null, "A lapsed chord window should still deliver a binding.");
            AssertEqual(
                "Ctrl+Alt+C",
                lapsed.ToDisplayString(),
                "An unfinished chord should save the single stroke the countdown showed.");
            AssertTrue(
                !lapsed.HasSecondStroke,
                "An unfinished chord should not be stored as a two-step binding.");
        }

        private static ShortcutBinding CaptureInjectedShortcut(
            int secondStrokeDelayMilliseconds,
            int secondStrokeWindowMilliseconds,
            bool sendSecondStroke)
        {
            ShortcutBinding captured = null;
            bool completed = false;
            bool canceled = false;
            List<int> countdown = new List<int>();

            using (KeyboardShortcutCaptureSession session = new KeyboardShortcutCaptureSession(
                10000,
                secondStrokeWindowMilliseconds))
            {
                session.ChordWaitChanged += delegate(object sender, ShortcutChordWaitEventArgs e)
                {
                    countdown.Add(e.SecondsRemaining);
                };
                session.Completed += delegate(object sender, ShortcutCaptureCompletedEventArgs e)
                {
                    captured = e.Binding;
                    canceled = e.Canceled;
                    completed = true;
                };
                session.Start();

                SendStroke(new[] { Keys.ControlKey, Keys.Menu }, Keys.C);
                if (sendSecondStroke)
                {
                    PumpFor(secondStrokeDelayMilliseconds);
                    SendStroke(new Keys[0], Keys.R);
                }

                PumpUntil(delegate { return completed; }, secondStrokeWindowMilliseconds + 4000);
            }

            AssertTrue(completed, "The capture session should always finish.");
            AssertTrue(!canceled, "An injected shortcut should not report cancellation.");
            AssertTrue(countdown.Count > 0, "The chord window should report its remaining seconds.");
            return captured;
        }

        private static void PumpFor(int milliseconds)
        {
            int deadline = Environment.TickCount + milliseconds;
            while (Environment.TickCount < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds)
        {
            int deadline = Environment.TickCount + timeoutMilliseconds;
            while (!condition() && Environment.TickCount < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }

            Application.DoEvents();
        }

        private static void SendStroke(Keys[] modifiers, Keys key)
        {
            for (int index = 0; index < modifiers.Length; index++)
            {
                SendKey(modifiers[index], false);
            }

            SendKey(key, false);
            SendKey(key, true);
            for (int index = modifiers.Length - 1; index >= 0; index--)
            {
                SendKey(modifiers[index], true);
            }
        }

        private static void SendKey(Keys key, bool keyUp)
        {
            NativeInput.Input input = new NativeInput.Input();
            input.Type = NativeInput.InputKeyboard;
            input.Data.Keyboard.VirtualKey = (ushort)key;
            input.Data.Keyboard.ScanCode = 0;
            input.Data.Keyboard.Flags = keyUp ? NativeInput.KeyEventKeyUp : 0u;
            input.Data.Keyboard.Time = 0;
            input.Data.Keyboard.ExtraInfo = IntPtr.Zero;
            NativeInput.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeInput.Input)));
            Thread.Sleep(10);
            Application.DoEvents();
        }

        private static class NativeInput
        {
            internal const int InputKeyboard = 1;
            internal const uint KeyEventKeyUp = 0x0002;

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint SendInput(uint count, Input[] inputs, int size);

            [StructLayout(LayoutKind.Sequential)]
            internal struct KeyboardInput
            {
                public ushort VirtualKey;
                public ushort ScanCode;
                public uint Flags;
                public uint Time;
                public IntPtr ExtraInfo;
            }

            [StructLayout(LayoutKind.Explicit)]
            internal struct InputUnion
            {
                [FieldOffset(0)]
                public KeyboardInput Keyboard;
                [FieldOffset(0)]
                public MouseInput Mouse;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct MouseInput
            {
                public int X;
                public int Y;
                public uint Data;
                public uint Flags;
                public uint Time;
                public IntPtr ExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct Input
            {
                public int Type;
                public InputUnion Data;
            }
        }

        private static void TestRecordingControlDefaults()
        {
            // The documented defaults. These are what a fresh install and a reset both produce.
            ShortcutBinding pause = RecordingControlCatalog.GetDefault(RecordingControlAction.PauseResume);
            ShortcutBinding stop = RecordingControlCatalog.GetDefault(RecordingControlAction.Stop);
            AssertEqual("Ctrl+Alt+Shift+P", pause.ToDisplayString(),
                "Pause/Resume Recording should default to Ctrl+Alt+Shift+P.");
            AssertEqual("Ctrl+Alt+Shift+X", stop.ToDisplayString(),
                "Stop Recording should default to Ctrl+Alt+Shift+X.");
            AssertEqual(2, RecordingControlCatalog.Actions.Length,
                "There are exactly two recording controls.");
            AssertEqual("Pause/Resume Recording",
                RecordingControlCatalog.GetName(RecordingControlAction.PauseResume),
                "The Pause/Resume control keeps its product name.");
            AssertEqual("Stop Recording", RecordingControlCatalog.GetName(RecordingControlAction.Stop),
                "The Stop control keeps its product name.");

            // Escape never stops a recording: an accidental cancel costs the whole take.
            AssertTrue(RecordingControlCatalog.IsForbiddenKey(Keys.Escape),
                "Escape must be refused as a recording control.");
            AssertTrue(!RecordingControlCatalog.IsForbiddenKey(Keys.X),
                "Ordinary keys remain available as recording controls.");
        }

        private static void TestRecordingControlConflicts()
        {
            AppSettings settings = new AppSettings();
            settings.Destinations.Add(new CaptureDestination
            {
                Id = "dest-1", Name = "Ingest", Path = @"C:\Ingest"
            });
            settings.OutputShortcuts["dest-1"] = new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, Key = Keys.D1
            };

            // A free combination is accepted.
            ShortcutBinding free = new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                Key = Keys.K
            };
            AssertTrue(settings.RecordingControlConflict(RecordingControlAction.Stop, free) == null,
                "An unused combination must be accepted as a recording control.");

            // A capture shortcut's own key is taken. Ctrl+Alt+Shift+S is Snip Region by default.
            ShortcutBinding snipRegion = ShortcutCatalog.GetDefault(CaptureAction.SnipRegion);
            AssertTrue(settings.RecordingControlConflict(RecordingControlAction.Stop, snipRegion) != null,
                "A recording control must not take a capture shortcut's combination.");

            // The second stroke of a chord is reserved too: Windows can only claim it once.
            settings.SetShortcut(
                CaptureAction.ClipScreen,
                ShortcutCatalog.GetDefault(CaptureAction.ClipScreen).WithSecondStroke(
                    new ShortcutBinding
                    {
                        Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                        Key = Keys.K
                    }));
            AssertTrue(settings.RecordingControlConflict(RecordingControlAction.Stop, free) != null,
                "A recording control must not take the second stroke of a capture chord.");

            // An Output shortcut's key is taken.
            ShortcutBinding output = new ShortcutBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, Key = Keys.D1
            };
            AssertTrue(settings.RecordingControlConflict(RecordingControlAction.Stop, output) != null,
                "A recording control must not take an Output shortcut's combination.");

            // The two recording controls must differ from each other, but each may keep its own.
            ShortcutBinding pauseDefault = RecordingControlCatalog.GetDefault(RecordingControlAction.PauseResume);
            AssertTrue(settings.RecordingControlConflict(RecordingControlAction.Stop, pauseDefault) != null,
                "Stop must not take the Pause/Resume combination.");
            AssertTrue(
                settings.RecordingControlConflict(RecordingControlAction.PauseResume, pauseDefault) == null,
                "A control keeping its own existing combination is not a conflict.");

            // Shape rules: a modifier is required, chords are not accepted here, Escape never is.
            AssertTrue(settings.RecordingControlConflict(
                    RecordingControlAction.Stop, new ShortcutBinding { Modifiers = 0, Key = Keys.K }) != null,
                "A bare key must be refused as a recording control.");
            AssertTrue(settings.RecordingControlConflict(
                    RecordingControlAction.Stop,
                    new ShortcutBinding
                    {
                        Modifiers = NativeMethods.MOD_CONTROL, Key = Keys.K,
                        SecondModifiers = NativeMethods.MOD_CONTROL, SecondKey = Keys.L
                    }) != null,
                "A two-step chord must be refused as a recording control.");
            AssertTrue(settings.RecordingControlConflict(
                    RecordingControlAction.Stop,
                    new ShortcutBinding
                    {
                        Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                        Key = Keys.Escape
                    }) != null,
                "Escape must be refused as a recording control.");

            // The reverse direction matters just as much: an Output shortcut cannot take a
            // recording control's key.
            AppSettings outputs = new AppSettings();
            outputs.Destinations.Add(new CaptureDestination
            {
                Id = "dest-2", Name = "Downloads", Path = @"C:\Downloads"
            });
            AssertTrue(
                outputs.OutputConflict("dest-2",
                    RecordingControlCatalog.GetDefault(RecordingControlAction.Stop)) != null,
                "An Output shortcut must not take a recording control's combination.");
        }

        private static void TestRecordingControlRepair()
        {
            // A capture shortcut moved onto Ctrl+Alt+Shift+X leaves Stop with a key Windows will
            // not give it. The repair moves Stop, and never the user's capture shortcut.
            AppSettings settings = new AppSettings();
            settings.SetShortcut(
                CaptureAction.SnipScreen,
                new ShortcutBinding
                {
                    Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
                    Key = Keys.X
                });
            AssertTrue(settings.EnsureRecordingShortcuts(),
                "A collided recording control must be repaired.");
            AssertEqual("Ctrl+Alt+Shift+X",
                settings.GetShortcut(CaptureAction.SnipScreen).ToDisplayString(),
                "Repair must never move the user's capture shortcut.");
            AssertTrue(settings.GetRecordingShortcut(RecordingControlAction.Stop).ToDisplayString()
                    != "Ctrl+Alt+Shift+X",
                "Stop must have moved off the colliding combination.");
            AssertTrue(settings.RecordingControlConflict(
                    RecordingControlAction.Stop,
                    settings.GetRecordingShortcut(RecordingControlAction.Stop)) == null,
                "The repaired Stop binding must itself be free.");
            AssertTrue(!settings.EnsureRecordingShortcuts(),
                "A settled configuration must not keep rewriting itself.");
        }

        private static void TestRecordingControlSettings(string root)
        {
            SettingsStore store = new SettingsStore(
                Path.Combine(root, "recording-controls"), delegate { return root; });
            AppSettings settings = store.Load();

            AssertEqual("Ctrl+Alt+Shift+P",
                settings.GetRecordingShortcut(RecordingControlAction.PauseResume).ToDisplayString(),
                "A fresh install gets the default Pause/Resume recording shortcut.");
            AssertTrue(settings.ShowFloatingRecordingControls,
                "The floating recording controls default to shown.");
            AssertEqual(50, settings.FloatingControllerOpacityPercent,
                "The floating controller defaults to 50% opacity.");
            AssertEqual(FloatingRecordingController.DefaultSize, settings.FloatingControllerSize,
                "A fresh install starts the controller at its default size.");

            settings.SetRecordingShortcut(
                RecordingControlAction.Stop,
                new ShortcutBinding
                {
                    Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, Key = Keys.K
                });
            settings.FloatingControllerOpacityPercent = 80;
            settings.FloatingControllerSize = 128;
            settings.ControllerLayouts.SetPosition("layout-one", new Point(120, 340));
            settings.ControllerLayouts.SetPosition("layout-two", new Point(-900, 55));
            store.Save(settings);

            AppSettings reloaded = store.Load();
            AssertEqual("Ctrl+Shift+K",
                reloaded.GetRecordingShortcut(RecordingControlAction.Stop).ToDisplayString(),
                "An edited recording shortcut must persist.");
            AssertEqual(80, reloaded.FloatingControllerOpacityPercent,
                "The chosen controller opacity must persist.");
            AssertEqual(128, reloaded.FloatingControllerSize,
                "A controller size set by dragging the notch must persist.");

            Point one, two;
            AssertTrue(reloaded.ControllerLayouts.TryGetPosition("layout-one", out one)
                    && one == new Point(120, 340),
                "A learned layout position must persist exactly.");
            AssertTrue(reloaded.ControllerLayouts.TryGetPosition("layout-two", out two)
                    && two == new Point(-900, 55),
                "Each layout keeps its own independent position, negative coordinates included.");

            // Switching the controller off must not erase opacity or any learned position.
            reloaded.ShowFloatingRecordingControls = false;
            store.Save(reloaded);
            AppSettings afterHiding = store.Load();
            AssertTrue(!afterHiding.ShowFloatingRecordingControls,
                "Turning the floating controls off must persist.");
            AssertEqual(80, afterHiding.FloatingControllerOpacityPercent,
                "Hiding the controller must not forget its opacity.");
            AssertEqual(128, afterHiding.FloatingControllerSize,
                "Hiding the controller must not forget its size.");
            AssertEqual(2, afterHiding.ControllerLayouts.Count,
                "Hiding the controller must not delete any layout profile.");
            AssertEqual("Ctrl+Shift+K",
                afterHiding.GetRecordingShortcut(RecordingControlAction.Stop).ToDisplayString(),
                "Hiding the controller must not disturb the recording shortcuts.");

            string contents = File.ReadAllText(store.SettingsPath);
            AssertTrue(contents.StartsWith("Version=15", StringComparison.Ordinal),
                "Recording controls carry settings schema 15.");
        }

        private static void TestRecordingControlMigrationFromSchema13(string root)
        {
            // A real schema-13 file: every value here is the user's and must survive untouched.
            string directory = Path.Combine(root, "schema-13");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.ini");
            File.WriteAllText(path, String.Join("\r\n", new[]
            {
                "Version=13",
                "ShowCursorInClips=True",
                "MicrophoneDeviceId=",
                "FilenameLabel=",
                "FilenameTemplate=",
                "NextFilenameCounter=12",
                "ActiveDestinationId=aaa",
                "RecordingFrameRate=0",
                "RecordingQuality=Balanced",
                "RecordingResolution=Native",
                "RecordingAudioMode=ComputerAndMicrophone",
                "ComputerAudioGainPercent=100",
                "MicrophoneGainPercent=100",
                "RoutineNotificationsEnabled=True",
                "Destination=aaa|SW5nZXN0|QzpcSW5nZXN0",
                "OutputShortcut.aaa=3,49",
                "Shortcut.SnipRegion=7,83",
                "Shortcut.SnipWindow=7,87",
                "Shortcut.SnipScreen=7,70",
                "Shortcut.ClipRegion=7,67",
                "Shortcut.ClipWindow=7,86",
                "Shortcut.ClipScreen=7,82",
                ""
            }), new System.Text.UTF8Encoding(false));

            SettingsStore store = new SettingsStore(directory, delegate { return root; });
            AppSettings migrated = store.Load();

            // The new keys arrive at their documented defaults...
            AssertEqual("Ctrl+Alt+Shift+P",
                migrated.GetRecordingShortcut(RecordingControlAction.PauseResume).ToDisplayString(),
                "Migration adds the default Pause/Resume recording shortcut.");
            AssertEqual("Ctrl+Alt+Shift+X",
                migrated.GetRecordingShortcut(RecordingControlAction.Stop).ToDisplayString(),
                "Migration adds the default Stop recording shortcut.");
            AssertTrue(migrated.ShowFloatingRecordingControls,
                "Migration turns the floating controls on.");
            AssertEqual(50, migrated.FloatingControllerOpacityPercent,
                "Migration starts the controller at 50% opacity.");
            AssertEqual(FloatingRecordingController.DefaultSize, migrated.FloatingControllerSize,
                "Migration starts the controller at its default size.");
            AssertEqual(0, migrated.ControllerLayouts.Count,
                "Migration invents no layout positions; the first right-drag teaches one.");

            // ...and nothing the user had chosen moved.
            AssertEqual(12, (int)migrated.NextFilenameCounter, "The filename counter must survive migration.");
            AssertEqual("aaa", migrated.ActiveDestinationId, "The active output must survive migration.");
            AssertEqual(1, migrated.Destinations.Count, "The named destination must survive migration.");
            AssertEqual("Ingest", migrated.Destinations[0].Name, "The destination name must survive migration.");
            AssertEqual("Ctrl+Alt+1", migrated.OutputShortcuts["aaa"].ToDisplayString(),
                "The Output shortcut must survive migration.");
            AssertEqual("ComputerAndMicrophone", migrated.RecordingAudioMode.ToString(),
                "The audio choice must survive migration.");
            AssertEqual("Balanced", migrated.RecordingQuality.ToString(),
                "The recording quality must survive migration.");
            AssertTrue(migrated.ShowCursorInClips, "Show Cursor in Clips must survive migration.");
            AssertEqual(0, migrated.RecordingFrameRate, "Match Display frame rate must survive migration.");
            foreach (CaptureAction action in ShortcutCatalog.Actions)
            {
                AssertEqual(
                    ShortcutCatalog.GetDefault(action).ToDisplayString(),
                    migrated.GetShortcut(action).ToDisplayString(),
                    "Capture shortcut " + ShortcutCatalog.GetName(action) + " must survive migration.");
            }
        }

        private static void TestDisplayLayoutIdentity()
        {
            DisplayLayoutEntry left = new DisplayLayoutEntry
            {
                MonitorId = @"\\?\DISPLAY#DEL4066#5&1", Bounds = new Rectangle(0, 0, 1920, 1080),
                Dpi = 96, Orientation = 0, Primary = true
            };
            DisplayLayoutEntry right = new DisplayLayoutEntry
            {
                MonitorId = @"\\?\DISPLAY#LEN61C1#5&2", Bounds = new Rectangle(1920, 0, 2560, 1440),
                Dpi = 120, Orientation = 0, Primary = false
            };

            string single = DisplayLayoutIdentity.Compose(new[] { left });
            string pair = DisplayLayoutIdentity.Compose(new[] { left, right });
            string pairReversed = DisplayLayoutIdentity.Compose(new[] { right, left });

            AssertEqual(16, single.Length, "A layout key stays short enough to live in a settings line.");
            AssertTrue(single != pair,
                "A single-display setup and a two-display setup are different layouts.");
            AssertEqual(pair, pairReversed,
                "Windows' own enumeration order must not change a layout's identity.");
            AssertEqual(pair, DisplayLayoutIdentity.Compose(new[] { left, right }),
                "The same displays must produce the same key every time.");

            // Rearranging the same monitors is deliberately a different layout: a position that
            // was perfect on the right-hand screen is inside the recording once it moves left.
            DisplayLayoutEntry moved = new DisplayLayoutEntry
            {
                MonitorId = right.MonitorId, Bounds = new Rectangle(-2560, 0, 2560, 1440),
                Dpi = right.Dpi, Orientation = right.Orientation, Primary = false
            };
            AssertTrue(DisplayLayoutIdentity.Compose(new[] { left, moved }) != pair,
                "Rearranging the same monitors must create a separate layout identity.");

            // Resolution, DPI scaling and orientation each change the usable geometry.
            AssertTrue(DisplayLayoutIdentity.Compose(new[]
                {
                    left,
                    new DisplayLayoutEntry
                    {
                        MonitorId = right.MonitorId, Bounds = new Rectangle(1920, 0, 1920, 1080),
                        Dpi = right.Dpi, Orientation = right.Orientation
                    }
                }) != pair,
                "A resolution change must create a separate layout identity.");
            AssertTrue(DisplayLayoutIdentity.Compose(new[]
                {
                    left,
                    new DisplayLayoutEntry
                    {
                        MonitorId = right.MonitorId, Bounds = right.Bounds,
                        Dpi = 144, Orientation = right.Orientation
                    }
                }) != pair,
                "A DPI scaling change must create a separate layout identity.");
            AssertTrue(DisplayLayoutIdentity.Compose(new[]
                {
                    left,
                    new DisplayLayoutEntry
                    {
                        MonitorId = right.MonitorId, Bounds = right.Bounds,
                        Dpi = right.Dpi, Orientation = 1
                    }
                }) != pair,
                "An orientation change must create a separate layout identity.");

            // A different physical monitor in the same place is a different desk.
            AssertTrue(DisplayLayoutIdentity.Compose(new[]
                {
                    left,
                    new DisplayLayoutEntry
                    {
                        MonitorId = @"\\?\DISPLAY#ACR0123#5&9", Bounds = right.Bounds,
                        Dpi = right.Dpi, Orientation = right.Orientation
                    }
                }) != pair,
                "Swapping in a different physical monitor must create a separate layout identity.");

            // Three displays are their own layout again.
            DisplayLayoutEntry third = new DisplayLayoutEntry
            {
                MonitorId = @"\\?\DISPLAY#SAM7788#5&3", Bounds = new Rectangle(-1920, 0, 1920, 1080),
                Dpi = 96, Orientation = 0
            };
            string triple = DisplayLayoutIdentity.Compose(new[] { left, right, third });
            AssertTrue(triple != pair && triple != single,
                "A three-display setup is its own layout.");

            // The live reading must at least produce a usable key on this machine.
            string current = DisplayLayoutIdentity.Current();
            AssertTrue(!String.IsNullOrEmpty(current) && current != "unknown",
                "The connected displays must yield a layout key: " + current);
            AssertEqual(current, DisplayLayoutIdentity.Current(),
                "Reading the same unchanged desk twice must give the same key.");
        }

        private static void TestControllerLayoutStore()
        {
            RecordingControllerLayouts layouts = new RecordingControllerLayouts();
            layouts.SetPosition("desk", new Point(100, 200));
            layouts.SetPosition("laptop", new Point(10, 20));
            layouts.SetPosition("dock", new Point(3000, 400));

            Point desk, laptop, dock;
            AssertTrue(layouts.TryGetPosition("desk", out desk) && desk == new Point(100, 200),
                "Each layout keeps its own position.");
            AssertTrue(layouts.TryGetPosition("laptop", out laptop) && laptop == new Point(10, 20),
                "A second layout is independent of the first.");
            AssertTrue(layouts.TryGetPosition("dock", out dock) && dock == new Point(3000, 400),
                "A third layout is independent of the other two.");

            // Correcting one layout must never move another.
            layouts.SetPosition("laptop", new Point(44, 55));
            layouts.TryGetPosition("desk", out desk);
            AssertTrue(desk == new Point(100, 200),
                "Updating one layout must leave every other layout alone.");

            Point missing;
            AssertTrue(!layouts.TryGetPosition("never-seen", out missing),
                "An unknown layout has no stored position and starts from a safe default.");

            // Bounded: a laptop that has been plugged into a hundred desks must not grow the
            // settings file forever. The oldest layout is the one that goes.
            RecordingControllerLayouts many = new RecordingControllerLayouts();
            for (int i = 0; i < RecordingControllerLayouts.MaxProfiles + 8; i++)
            {
                many.SetPosition("layout-" + i, new Point(i, i));
            }

            AssertEqual(RecordingControllerLayouts.MaxProfiles, many.Count,
                "The layout collection stays bounded.");
            Point evicted;
            AssertTrue(!many.TryGetPosition("layout-0", out evicted),
                "The least recently used layout is the one dropped.");
            Point newest;
            AssertTrue(many.TryGetPosition(
                    "layout-" + (RecordingControllerLayouts.MaxProfiles + 7), out newest),
                "The most recent layout is always kept.");

            // Touching a layout renews it without moving its controller.
            RecordingControllerLayouts renewed = new RecordingControllerLayouts();
            for (int i = 0; i < RecordingControllerLayouts.MaxProfiles; i++)
            {
                renewed.SetPosition("keep-" + i, new Point(i, i));
            }

            renewed.Touch("keep-0");
            renewed.SetPosition("newcomer", new Point(9, 9));
            Point kept;
            AssertTrue(renewed.TryGetPosition("keep-0", out kept) && kept == new Point(0, 0),
                "Touching a layout renews it without changing where its controller sits.");
            Point pushedOut;
            AssertTrue(!renewed.TryGetPosition("keep-1", out pushedOut),
                "The next-oldest layout is evicted once a renewed one is safe.");
        }

        private static void TestControllerPlacement()
        {
            Rectangle primary = new Rectangle(0, 0, 1920, 1040);
            Rectangle secondary = new Rectangle(1920, 0, 2560, 1400);
            List<Rectangle> areas = new List<Rectangle> { primary, secondary };
            Size controller = FloatingRecordingController.Measure(FloatingRecordingController.DefaultSize);

            // A layout nobody has taught starts low and to the right of the recorded display,
            // fully on-screen.
            Point fallback = RecordingControllerPlacement.Default(primary, controller);
            AssertTrue(primary.Contains(new Rectangle(fallback, controller)),
                "A new layout's default position must be completely on the recording display.");
            Point secondaryFallback = RecordingControllerPlacement.Default(secondary, controller);
            AssertTrue(secondary.Contains(new Rectangle(secondaryFallback, controller)),
                "The default lands on whichever display is being recorded.");
            AssertTrue(fallback != secondaryFallback,
                "The safe default follows the recorded display rather than one fixed point.");

            // A position that is already fine is left exactly alone.
            Point settled = new Point(400, 500);
            AssertTrue(RecordingControllerPlacement.Clamp(settled, controller, areas) == settled,
                "A reachable saved position must be restored exactly, not nudged.");
            Point onSecond = new Point(2200, 900);
            AssertTrue(RecordingControllerPlacement.Clamp(onSecond, controller, areas) == onSecond,
                "A saved position on a second display must be restored exactly.");

            // A position learned on a bigger desk is pulled back rather than thrown away.
            Point unreachable = new Point(6000, 5000);
            Point rescued = RecordingControllerPlacement.Clamp(unreachable, controller, areas);
            AssertTrue(rescued != unreachable, "An unreachable saved position must be corrected.");
            bool landed = false;
            foreach (Rectangle area in areas)
            {
                if (area.Contains(new Rectangle(rescued, controller))) landed = true;
            }

            AssertTrue(landed, "A clamped position must put the whole controller on a real display.");

            Point negative = RecordingControllerPlacement.Clamp(
                new Point(-4000, -3000), controller, areas);
            landed = false;
            foreach (Rectangle area in areas)
            {
                if (area.Contains(new Rectangle(negative, controller))) landed = true;
            }

            AssertTrue(landed, "A position off the top-left of every display must also be rescued.");

            // Partly off an edge: only the overhang is taken back.
            Point overhang = new Point(primary.Right - 20, 300);
            Point tucked = RecordingControllerPlacement.Clamp(overhang, controller, areas);
            AssertTrue(tucked.Y == 300, "Clamping must only correct the axis that is actually off.");
        }

        private static void TestControllerLayoutGeometry()
        {
            // The controller is one shape scaled from a single number: the width of the H's own
            // 64-unit square. Everything else - the two buttons under the two posts, the resize
            // notch - is laid out from it.
            Size small = FloatingRecordingController.Measure(FloatingRecordingController.MinimumSize);
            Size normal = FloatingRecordingController.Measure(FloatingRecordingController.DefaultSize);
            Size large = FloatingRecordingController.Measure(FloatingRecordingController.MaximumSize);

            AssertTrue(normal.Width > 0 && normal.Height > 0, "The controller has a real size.");
            AssertTrue(small.Width < normal.Width && normal.Width < large.Width,
                "A larger controller size must produce a larger window.");
            AssertTrue(normal.Height > normal.Width,
                "Buttons sit below the H, so the controller is taller than it is wide.");

            // Aspect ratio is fixed: dragging the notch scales the whole thing, never stretches it.
            double smallRatio = (double)small.Width / small.Height;
            double largeRatio = (double)large.Width / large.Height;
            AssertTrue(Math.Abs(smallRatio - largeRatio) < 0.02,
                "Resizing must scale the controller, not distort it: " + smallRatio + " vs " + largeRatio);

            AssertEqual(FloatingRecordingController.MinimumSize,
                FloatingRecordingController.NormalizeSize(1),
                "A nonsense size is pulled up to the smallest usable controller.");
            AssertEqual(FloatingRecordingController.MaximumSize,
                FloatingRecordingController.NormalizeSize(10000),
                "A nonsense size is pulled down to the largest usable controller.");
            AssertEqual(96, FloatingRecordingController.NormalizeSize(96),
                "A size inside the range is kept exactly.");
            AssertTrue(FloatingRecordingController.DefaultSize >= FloatingRecordingController.MinimumSize
                    && FloatingRecordingController.DefaultSize <= FloatingRecordingController.MaximumSize,
                "The default size must itself be a legal size.");

            // Opacity: the persisted choice is 10-100%, and 50% is the default.
            AssertEqual(50, FloatingRecordingController.DefaultOpacityPercent,
                "The floating controller defaults to 50% opacity.");
            AssertEqual(10, FloatingRecordingController.NormalizeOpacityPercent(0),
                "Opacity never falls below 10%, which would make the controller unusable.");
            AssertEqual(10, FloatingRecordingController.NormalizeOpacityPercent(-40),
                "A nonsense opacity is pulled up to the 10% floor.");
            AssertEqual(100, FloatingRecordingController.NormalizeOpacityPercent(400),
                "Opacity never exceeds 100%.");
            AssertEqual(35, FloatingRecordingController.NormalizeOpacityPercent(35),
                "A chosen opacity inside the range is kept exactly.");
        }

        private static void TestControllerButtonsSitUnderThePosts()
        {
            // Huck's layout: each button sits directly under one of the H's posts and is exactly
            // as wide as that post, at every size. This is checked against the numbers AND against
            // the pixels the controller actually draws, because the window is shaped from those
            // pixels - if they disagree, the window is the wrong shape.
            foreach (int size in new[] { FloatingRecordingController.MinimumSize, 72, 120,
                FloatingRecordingController.MaximumSize })
            {
                Rectangle leftPost = FloatingRecordingController.MeasureLeftPost(size);
                Rectangle rightPost = FloatingRecordingController.MeasureRightPost(size);
                Rectangle leftButton = FloatingRecordingController.MeasureLeftButton(size);
                Rectangle rightButton = FloatingRecordingController.MeasureRightButton(size);

                AssertEqual(leftPost.Left, leftButton.Left,
                    "The left button must start where the left post starts at size " + size + ".");
                AssertEqual(leftPost.Right, leftButton.Right,
                    "The left button must be exactly as wide as the left post at size " + size + ".");
                AssertEqual(rightPost.Left, rightButton.Left,
                    "The right button must start where the right post starts at size " + size + ".");
                AssertEqual(rightPost.Right, rightButton.Right,
                    "The right button must be exactly as wide as the right post at size " + size + ".");
                AssertTrue(leftButton.Top >= rightPost.Bottom - 1 && rightButton.Top >= leftPost.Bottom - 1,
                    "Both buttons sit below the H, not beside it, at size " + size + ".");
                AssertTrue(leftButton.Right < rightButton.Left,
                    "The two buttons are separate, with the H's gap between them, at size " + size + ".");
            }

            using (Bitmap drawn = FloatingRecordingController.RenderPreview(120, 0.0f, 0.0f, false, false))
            {
                Rectangle leftButton = FloatingRecordingController.MeasureLeftButton(120);
                Rectangle rightButton = FloatingRecordingController.MeasureRightButton(120);
                int middleY = leftButton.Top + leftButton.Height / 2;

                AssertTrue(drawn.GetPixel(leftButton.Left + 3, middleY).A == 255,
                    "The left button must be drawn where it was measured.");
                AssertTrue(drawn.GetPixel(rightButton.Left + 3, middleY).A == 255,
                    "The right button must be drawn where it was measured.");

                // Nothing bridges the two buttons: there is no panel, so the gap is see-through.
                int betweenX = (leftButton.Right + rightButton.Left) / 2;
                AssertEqual(0, drawn.GetPixel(betweenX, middleY).A,
                    "The space between the two buttons must be empty - there is no box behind them.");

                // And nothing sits to the left of the H or below the buttons.
                AssertEqual(0, drawn.GetPixel(1, middleY).A,
                    "Nothing is drawn to the left of the H.");
                AssertEqual(0, drawn.GetPixel(betweenX, drawn.Height - 1).A,
                    "Nothing is drawn below the buttons.");

                AssertEqual(0, drawn.GetPixel(1, 1).A,
                    "The corner beside the H stays empty.");
            }
        }

        private static void TestResizeNotchLivesOnTheHAndHidesUntilHovered()
        {
            // The notch is a handle, not part of the mark, so it stays out of sight until the
            // pointer is on the controller. It sits INSIDE the H's right post, which is what makes
            // that possible: a notch outside the H would have to leave the window's shape to hide,
            // and a shape the pointer is not over cannot notice the pointer arriving.
            foreach (int size in new[] { FloatingRecordingController.MinimumSize, 72, 120 })
            {
                Rectangle grip = FloatingRecordingController.MeasureGrip(size);
                Rectangle rightPost = FloatingRecordingController.MeasureRightPost(size);

                AssertTrue(rightPost.Contains(grip),
                    "The notch must sit inside the H's right post at size " + size
                        + ": post " + rightPost + " does not contain grip " + grip + ".");
                AssertEqual(rightPost.Right, grip.Right,
                    "The notch is in the post's top-RIGHT corner at size " + size + ".");
                AssertEqual(rightPost.Top, grip.Top,
                    "The notch is in the post's TOP-right corner at size " + size + ".");
                AssertTrue(grip.Width > 0 && grip.Height > 0,
                    "The notch has a real grabbable area at size " + size + ".");
            }

            Rectangle notch = FloatingRecordingController.MeasureGrip(120);
            // Two pixels in from the corner, inside the triangle either way.
            int x = notch.Right - 3, y = notch.Top + 2;

            using (Bitmap resting = FloatingRecordingController.RenderPreview(120, 0.0f, 0.0f, false, false))
            using (Bitmap hovered = FloatingRecordingController.RenderPreview(120, 0.0f, 0.0f, false, false, true))
            {
                Color restingPixel = resting.GetPixel(x, y);
                Color hoveredPixel = hovered.GetPixel(x, y);

                AssertTrue(restingPixel.R == 255 && restingPixel.G == 255 && restingPixel.B == 255,
                    "With no pointer on it, the notch is invisible and the H's corner is plain "
                        + "white: got " + restingPixel.R + "," + restingPixel.G + "," + restingPixel.B + ".");
                AssertTrue(hoveredPixel.R < 200 && hoveredPixel.G < 200 && hoveredPixel.B < 200,
                    "With the pointer on the controller the notch appears: got "
                        + hoveredPixel.R + "," + hoveredPixel.G + "," + hoveredPixel.B + ".");

                // Showing and hiding the notch must never change the window's silhouette, or the
                // window would change shape under the pointer every time it arrived.
                AssertEqual(restingPixel.A, hoveredPixel.A,
                    "Revealing the notch must not change which pixels are solid.");
                int differingAlpha = 0;
                for (int row = 0; row < resting.Height; row++)
                    for (int column = 0; column < resting.Width; column++)
                        if (resting.GetPixel(column, row).A != hovered.GetPixel(column, row).A)
                            differingAlpha++;
                AssertEqual(0, differingAlpha,
                    "The controller's silhouette must be identical with and without the notch "
                        + "showing, so the window's shape never changes under the pointer.");
            }

            // The drawn silhouette must survive a resize: every size produces a controller whose
            // posts and buttons still line up and whose corners are still empty.
            using (Bitmap smallest = FloatingRecordingController.RenderPreview(
                FloatingRecordingController.MinimumSize, 0.5f, 0.5f, false, false))
            using (Bitmap largest = FloatingRecordingController.RenderPreview(
                FloatingRecordingController.MaximumSize, 0.5f, 0.5f, false, false))
            {
                AssertEqual(FloatingRecordingController.Measure(FloatingRecordingController.MinimumSize).Width,
                    smallest.Width, "The smallest controller renders at its measured width.");
                AssertEqual(FloatingRecordingController.Measure(FloatingRecordingController.MaximumSize).Width,
                    largest.Width, "The largest controller renders at its measured width.");
                AssertEqual(0, largest.GetPixel(1, 1).A,
                    "A resized controller still has no panel behind it.");
            }
        }

        private static void TestControllerIsWhiteAndUnpanelled()
        {
            // The controller draws a WHITE H with a solid track. The tray keeps its grey mark and
            // its faint track - a 16 px icon competing with a taskbar is a different problem.
            using (Bitmap tray = TrayIconFactory.CreateBitmap(true, 0.0f, 0.0f, 64))
            using (Bitmap floating = TrayIconFactory.CreateBitmap(
                true, 0.0f, 0.0f, 64, Color.White, 255))
            {
                // Mid-height inside the left post, where the H body is and no meter has risen.
                Color trayBody = tray.GetPixel(16, 20);
                Color floatingBody = floating.GetPixel(16, 20);

                AssertTrue(floatingBody.A == 255,
                    "The controller's H must be solid, because its window is shaped from these "
                        + "pixels: alpha was " + floatingBody.A + ".");
                AssertTrue(floatingBody.R == 255 && floatingBody.G == 255 && floatingBody.B == 255,
                    "The controller's H must be white: got "
                        + floatingBody.R + "," + floatingBody.G + "," + floatingBody.B + ".");
                AssertTrue(trayBody.A < 128,
                    "The tray mark keeps its faint track: alpha was " + trayBody.A + ".");
                AssertTrue(trayBody.R == 150,
                    "The tray mark stays grey: got " + trayBody.R + ".");
            }

            // There is no panel: outside the H, the controller draws nothing at all. A pixel in the
            // corner of the H's own square must stay fully transparent even at a solid track.
            using (Bitmap floating = TrayIconFactory.CreateBitmap(
                true, 1.0f, 1.0f, 64, Color.White, 255))
            {
                AssertEqual(0, floating.GetPixel(1, 1).A,
                    "The corner beside the H must stay transparent - there is no box behind it.");
                AssertEqual(0, floating.GetPixel(32, 3).A,
                    "The gap above the crossbar must stay transparent.");
            }

            // The paused and finishing marks follow the same colour.
            using (Bitmap paused = TrayIconFactory.CreateStatusBitmap(true, 64, Color.White, 255))
            {
                AssertTrue(paused.GetPixel(16, 20).R == 255,
                    "The paused controller H is white too.");
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            assertions++;
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            assertions++;
            if (!String.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    message + " Expected: " + expected + "; actual: " + actual + ".");
            }
        }

        private static void AssertEqual(int expected, int actual, string message)
        {
            assertions++;
            if (expected != actual)
            {
                throw new InvalidOperationException(
                    message + " Expected: " + expected + "; actual: " + actual + ".");
            }
        }
    }
}
