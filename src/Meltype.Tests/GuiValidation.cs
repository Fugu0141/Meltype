// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;
using Meltype.UI;

namespace Meltype.Tests;

// Test-only host for real Windows hooks, composition windows, IME and commit delivery.
// Scripted cases feed CaptureGate explicitly: they do NOT claim physical-keyboard coverage.
// No automation of focus, native controls or keyboard input is implemented here.
internal static class GuiValidation
{
    public static int Run(string[] args)
    {
        var directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new Form { Text = "Meltype GUI validation (isolated)", Width = 640, Height = 340, StartPosition = FormStartPosition.CenterScreen };
        form.CreateControl();
        var settings = new Settings { Enabled = args.Contains("--physical"), AutoUpdate = false, WelcomeShown = true, FileLog = false, LogTypedText = false, ConversionEngine = ConversionEngine.System, LiveConversion = false };
        // Physical follow-ups opt in. Keep other currently running applications outside test capture.
        settings.AppRules = System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return p.ProcessName; })
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => p is not ("Code" or "msedge"))
            .Select(p => new AppRule { Process = p + ".exe", Enabled = false }).ToList();
        using var engine = new MeltypeEngine(settings, Path.Combine(directory, "config.json"), Path.Combine(directory, "model.json"), directory);
        var detector = CompositionDetector.CreateDefault(directory);
        detector.SpellChecker = Detection.WindowsSpellChecker.Shared;
        using var service = new CompositionService(form, detector, new CompositionOptions {
            LiveConversion = () => engine.AppSettings.LiveConversion,
            DirectMode = () => engine.KeyboardDirect,
            ClassifyDirect = engine.ClassifyDirect, DirectDecided = engine.OnDirectDecided,
            AutoCorrect = () => engine.Settings.AutoCorrectAfterCommit,
            Level = () => engine.AppSettings.DetectionLevel,
            Engine = () => ConversionEngine.System,
            Placement = () => engine.Settings.CompositionPlacement, Size = () => engine.Settings.CompositionSize,
            ModeIndicator = () => engine.Settings.Enabled && engine.Settings.ShowModeIndicator,
            ModeIndicatorOnFocus = () => engine.Settings.ShowModeIndicatorOnFocus,
            History = new ConversionHistory(Path.Combine(directory, "conversions.json")),
            Languages = new LanguageMemory(Path.Combine(directory, "languages.json")),
            UserDictionary = new UserDictionary(Path.Combine(directory, "user-dictionary.json")),
            TranslationHistory = new TranslationHistory(Path.Combine(directory, "translations.json")),
            Candidates = CandidateDictionary.Load(directory), ContextRules = ContextRules.Load(directory),
            Misspellings = MisspellingDictionary.Load(directory),
        });
        engine.AttachComposition(service);
        void Record(object value) => File.AppendAllText(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(value) + Environment.NewLine);
        Application.ThreadException += (_, e) => Record(new { kind = "exception", error = e.Exception.ToString() });
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        form.Controls.Add(panel);
        panel.Controls.Add(new Label { AutoSize = true, Text = "Test data isolated. Physical typing requires --physical.\nScript buttons bypass hook input only; focus the allowed target within 15 seconds." });
        var allowedTitle = new TextBox { Width = 580, Text = "● test.txt - Cherry-ToDo - Visual Studio Code" };
        panel.Controls.Add(allowedTitle);
        service.Controller.Committed += text => {
            var chars = new char[512]; Native.InternalGetWindowText(Native.GetForegroundWindow(), chars, chars.Length);
            if (new string(chars).TrimEnd('\0') == allowedTitle.Text)
                Record(new { kind = "commit", text, at = DateTimeOffset.Now });
            else Record(new { kind = "commit_outside_test_target", text_length = text.Length, at = DateTimeOffset.Now });
        };
        var status = new Label { AutoSize = true, Text = "Ready" };
        var buttons = new FlowLayoutPanel { Width = 590, Height = 110 };
        panel.Controls.Add(buttons); panel.Controls.Add(status);
        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        Queue<(long Due, Action Action)> queue = new();
        void Key(int vk) {
            service.Gate.OnKey(new KeyEvent(vk, 0, false, false, false, Environment.TickCount64), _ => true);
            service.Controller.Pump();
            service.Gate.OnKey(new KeyEvent(vk, 0, false, true, false, Environment.TickCount64), _ => true);
            service.Controller.Pump();
        }
        void Arm(string name, string text, params int[] endings) {
            queue.Clear(); var title = allowedTitle.Text; var start = Environment.TickCount64 + 15000;
            void Guard(Action action) {
                var chars = new char[512]; Native.InternalGetWindowText(Native.GetForegroundWindow(), chars, chars.Length);
                var actual = new string(chars).TrimEnd('\0');
                if (actual != title || !service.Focus.CanCapture) {
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
                    string process; try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); process = p.ProcessName; } catch { process = "unavailable"; }
                    Record(new { kind = "guard_rejected", name, title_match = actual == title, can_capture = service.Focus.CanCapture, process, at = DateTimeOffset.Now });
                    queue.Clear(); status.Text = "Guard rejected: target title / editable focus"; return;
                }
                action();
            }
            queue.Enqueue((start, () => Guard(() => {
                service.Controller.Reset(); service.Gate.Abort(); service.ResetContext();
                foreach (var c in text) {
                    var upper = char.IsUpper(c);
                    void Enqueue(int vk, bool up) => service.Gate.OnKey(new KeyEvent(vk, 0, false, up, false, Environment.TickCount64), _ => true);
                    if (upper) Enqueue(VirtualKeys.Shift, false);
                    Enqueue(char.ToUpperInvariant(c), false); Enqueue(char.ToUpperInvariant(c), true);
                    if (upper) Enqueue(VirtualKeys.Shift, true);
                    service.Controller.Pump();
                }
                Record(new { kind = "loaded", name, text, at = DateTimeOffset.Now }); status.Text = name + ": loaded";
            })));
            for (var i = 0; i < endings.Length; i++) { var vk = endings[i]; queue.Enqueue((start + (i + 1) * 8000, () => Guard(() => { Key(vk); Record(new { kind = "key", name, vk, composing = service.Controller.IsComposing }); status.Text = name + ": key " + vk; }))); }
            status.Text = name + ": armed (15s)";
            Record(new { kind = "armed", name, at = DateTimeOffset.Now, scope = "CaptureGate feed -> real CompositionService / UI / Windows delivery; physical hook bypassed" });
        }
        void Button(string caption, Action action) { var b = new Button { Text = caption, AutoSize = true }; b.Click += (_, _) => action(); buttons.Controls.Add(b); }
        Button("JA: konnichiha", () => Arm("JA", "konnichiha", VirtualKeys.Space, VirtualKeys.Down, VirtualKeys.Return));
        Button("EN: hello", () => Arm("EN", "hello", VirtualKeys.Space));
        Button("Edit: watashix", () => Arm("BACKSPACE", "watashix", VirtualKeys.Back, VirtualKeys.Return));
        Button("Cancel: nihongo", () => Arm("ESCAPE", "nihongo", VirtualKeys.Escape, VirtualKeys.Escape));
        Button("Switch: arigatou", () => Arm("FOCUS", "arigatou"));
        Button("Mixed: Commitni", () => Arm("COMMITNI", "Commitni", VirtualKeys.Space, VirtualKeys.Down, VirtualKeys.Return));
        Button("Mixed: commitni", () => Arm("commitni", "commitni", VirtualKeys.Space, VirtualKeys.Return));
        Button("Control: Commit", () => Arm("COMMIT", "Commit", VirtualKeys.Space));
        Button("Settings", () => new SettingsForm(engine).Show());
        Button("Dictionary", () => new UserDictionaryForm(service).Show());
        Button("Learned words", () => new LearnedWordsForm(service.Languages, service.History).Show());
        Button("Exit / stop hooks", form.Close);
        timer.Tick += (_, _) => { if (queue.Count > 0 && Environment.TickCount64 >= queue.Peek().Due) queue.Dequeue().Action(); };
        form.Shown += (_, _) => { engine.Start(); timer.Start(); Record(new { kind = "started", isolated = directory, ime = service.ConverterAvailable, physical_capture_enabled = settings.Enabled }); };
        Application.Run(form);
        engine.DetachComposition();
        Record(new { kind = "stopped" });
        return 0;
    }
}
