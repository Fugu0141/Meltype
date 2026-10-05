// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>Additional observations; does not change Quality gates or product logic.</summary>
internal static class Validation
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Root = "";
    private static int Seed;
    private static long Deadline;
    private static string CurrentCase = "startup";
    private static readonly Detection.ScoreEngine Engine = TestSupport.CreateEngine();
    private sealed record CorpusCase(string id, string category, string input, string expected, string? context = null, string oracle = "exact committed text");
    private sealed record ActionKey(int vk, bool shift, int delay, int action);
    private sealed record Outcome(string? failure, string signature, int events, int lost, int duplicated, int reordered, string? exception = null);
    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    private static void Save(string name, object value) => File.WriteAllText(Path.Combine(Root, name), Serialize(value), new UTF8Encoding(false));
    private static void Line(StreamWriter writer, object value) => writer.WriteLine(Serialize(value));
    private static void Failure(string id, string category, object expected, object actual, object? events = null, int? @case = null, string? exception = null, string? input = null, string? context = null)
    {
        var caseOption = @case.HasValue ? $" --case {@case}" : "";
        File.AppendAllText(Path.Combine(Root, "failures.jsonl"), Serialize(new { id, category, seed = Seed, @case, input, context, expected, actual, events, elapsed_ms = (double?)null, exception,
            repro = $"dotnet run --project src/Meltype.Core.Tests -c Release -- --validate {category} --seed {Seed}{caseOption} --out replay-results" }) + "\n");
    }
    public static int Run(string[] args)
    {
        string Option(string key, string fallback) { var i = Array.IndexOf(args, key); return i >= 0 ? args[i + 1] : fallback; }
        Root = Path.GetFullPath(Option("--out", "artifacts/test-results")); Directory.CreateDirectory(Root);
        Seed = int.Parse(Option("--seed", "20261005"));
        int? only = args.Contains("--case") ? int.Parse(Option("--case", "0")) : null;
        CompositionTests.Detector.SpellChecker = Detection.BuiltInWordChecker.Shared;
        using var watchdog = new Timer(_ => { if (Interlocked.Read(ref Deadline) > 0 && Stopwatch.GetTimestamp() > Interlocked.Read(ref Deadline)) {
            Save("timeout.json", new { seed = Seed, @case = CurrentCase, status = "TIMEOUT", repro = "Use the same mode, seed and case" }); Environment.Exit(124);
        } }, null, 1000, 1000);
        void Guard(string id) { CurrentCase = id; Interlocked.Exchange(ref Deadline, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60); }
        Guard("startup");
        switch (args.FirstOrDefault())
        {
            case "corpus": Corpus(only, int.Parse(Option("--count", "10000")), Guard); break;
            case "fuzz": Fuzz(only, int.Parse(Option("--count", "100000")), Guard, args.Contains("--replay") ? Option("--replay", "") : null); break;
            case "stress": Stress(int.Parse(Option("--count", "1000000")), Guard); break;
            case "fault": Fault(); break;
            case "privacy": Privacy(); break;
            case "strings": Strings(Guard); break;
            case "harness-check": HarnessCheck(); break;
            case "dictionary": DictionaryBenchmark(Guard); break;
            case "probe": Probe(Option("--input", ""), Option("--context", "")); break;
            case "diff": Diff(Option("--baseline", "artifacts/test-results/corpus-baseline.jsonl"), Option("--current", "artifacts/test-results/corpus.jsonl")); break;
            default: throw new ArgumentException("Modes: corpus, fuzz, stress, fault, privacy, strings, diff");
        }
        Console.WriteLine($"Completed {args[0]} seed={Seed} out={Root}"); return 0;
    }
    private static void HarnessCheck()
    {
        var keys = Enumerable.Range(0, 4).Select(i => new KeyEvent(0x41 + i, i, false, false, false, 1000 + i)).ToList();
        static (int lost, int duplicates, int reorder) Integrity(List<KeyEvent> input, List<KeyEvent> output) =>
            (input.Count(e => !output.Contains(e)), output.Count - output.Distinct().Count(), output.Zip(output.Skip(1)).Count(p => p.First.Scan >= p.Second.Scan));
        Assert.Equal((0, 0, 0), Integrity(keys, keys));
        Assert.Equal((1, 0, 0), Integrity(keys, keys.Skip(1).ToList()));
        Assert.Equal(1, Integrity(keys, keys.Append(keys[^1]).ToList()).duplicates);
        Assert.True(Integrity(keys, keys.AsEnumerable().Reverse().ToList()).reorder > 0, "order oracle must catch deliberate reversal");
        Assert.True(Observe(Sequence(48391)).signature == Observe(Sequence(48391)).signature, "independent case determinism");
        Save("harness-check.json", new { deliberate_loss_detected = true, deliberate_duplicate_detected = true, deliberate_reorder_detected = true, deterministic_case = 48391, ok = true });
    }
    private static void DictionaryBenchmark(Action<string> guard)
    {
        var watch = Stopwatch.StartNew(); var dictionary = CandidateDictionary.Load(null); var load = watch.Elapsed.TotalMilliseconds;
        string[] reading = ["はし", "えがお", "かんがえるかお", "にほん", "ねこ", "存在しない合成キー", "", "😀"];
        for (var i = 0; i < 1000; i++) dictionary.Lookup(reading[i % reading.Length]);
        var latencies = new double[1000000]; long hits = 0;
        watch.Restart(); for (var i = 0; i < latencies.Length; i++) { if (i % 1000 == 0) guard(i.ToString()); var start = Stopwatch.GetTimestamp(); if (dictionary.Lookup(reading[i % reading.Length]).Count > 0) hits++; latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        var elapsed = watch.Elapsed.TotalMilliseconds; Array.Sort(latencies);
        Save("dictionary.json", new { load_ms = load, lookups = latencies.Length, hits, elapsed_ms = elapsed, p50_ms = latencies[499999], p95_ms = latencies[949999], p99_ms = latencies[989999], max_ms = latencies[^1], managed_heap = GC.GetTotalMemory(true), scope = "Built-in exact candidate lookup, including miss/empty/Unicode; no synthetic million-entry dictionary or prefix stress" });
    }

    private static void Probe(string input, string context)
    {
        var k = new CompositionTests.Keyboard(userDictionary: new UserDictionary(null)); k.Host.PrecedingText = context;
        k.Type(input + "\n"); var result = new { input, context, actual = k.Host.Document, composing = k.Controller.IsComposing, captured = k.Gate.IsCaptured };
        Save("probe.json", result); Console.WriteLine(Serialize(result));
    }

    private static List<CorpusCase> GenerateCorpus()
    {
        // Extend repository-authored Quality cases; no third-party corpus or IME dictionary extraction.
        var original = ((System.Collections.IEnumerable)typeof(Quality).GetField("Typing", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Cast<object>()
            .Select(x => { var t = x.GetType(); string P(string k) => (string)t.GetProperty(k)!.GetValue(x)!;
                return new CorpusCase("", P("Category"), P("Typed"), P("Expected"), (string?)t.GetProperty("Before")!.GetValue(x)); }).ToList();
        var ja = original.Where(c => c.category == "日本語" && !c.input.Contains(' ')).ToList();
        var en = original.Where(c => c.category == "英語" && c.context is null).ToList();
        var mix = original.Where(c => c.category == "混在").ToList();
        var amb = original.Where(c => c.category == "曖昧な語").ToList();
        if (amb.Count == 0) amb = original.Where(c => c.context is not null).ToList();
        var all = new List<CorpusCase>(); var unique = new HashSet<string>(original.Select(c => c.input + "|" + c.context));
        void Add(string category, string input, string expected, string? context = null) {
            if (unique.Add(input + "|" + context)) all.Add(new($"C{all.Count:D5}", category, input, expected, context)); }
        void Fill(string category, int total, Func<int, (string input, string expected, string? context)> create) {
            var start = all.Count; for (var i = 0; all.Count - start < total; i++) {
                if (i > 200000) throw new Exception("Corpus generator exhausted unique cases: " + category);
                var c = create(i); Add(category, c.input, c.expected, c.context);
            }
        }
        Fill("Japanese", 2000, i => { var a = ja[i % ja.Count]; var b = ja[(i / ja.Count) % ja.Count]; return (a.input + b.input + (i / (ja.Count * ja.Count) == 0 ? "" : (i / (ja.Count * ja.Count)).ToString()), a.expected + b.expected + (i / (ja.Count * ja.Count) == 0 ? "" : (i / (ja.Count * ja.Count)).ToString()), null); });
        Fill("English", 2000, i => { var a = en[i % en.Count]; var b = en[(i / en.Count) % en.Count]; var tail = i >= en.Count * en.Count ? " " + en[(i / (en.Count * en.Count)) % en.Count].input : ""; return (a.input + " " + b.input + tail, a.expected + " " + b.expected + tail, null); });
        Fill("Mixed", 3000, i => { var a = mix[i % mix.Count]; var b = ja[(i / mix.Count) % ja.Count]; var n = i / (mix.Count * ja.Count); return (n == 0 ? a.input + b.input : b.input + a.input + n, n == 0 ? a.expected + b.expected : b.expected + a.expected + n, a.context); });
        Fill("Ambiguous/context", 1000, i => { var a = amb[i % amb.Count]; var number = (1000 + i / amb.Count).ToString(); return (a.input + number, a.expected + number, a.context); });
        Fill("Symbols/numbers", 1000, i => ($"{1000 + i}.{(i * 17) % 1000:D3}", $"{1000 + i}.{(i * 17) % 1000:D3}", null));
        Fill("Code/terminal", 1000, i => { var n = i / 5; return (i % 5) switch {
            0 => ($"var value{n} = ", LineKind.Code.ToString(), "C#/JS"),
            1 => ($"// synthetic comment {n}", LineKind.Comment.ToString(), "C/JS"),
            2 => ($"# synthetic comment {n}", LineKind.Comment.ToString(), "Python/PowerShell"),
            3 => ($"value{n} = \"synthetic", LineKind.String.ToString(), "string"),
            _ => ($"-- synthetic comment {n}", LineKind.Comment.ToString(), "SQL") }; });
        return all;
    }
    private static void Corpus(int? only, int count, Action<string> guard)
    {
        var data = GenerateCorpus(); Save("corpus-generation.json", new { total = data.Count, source = "Quality.Typing repository authored seeds + synthetic concatenation/numeric/context templates", license = "GPL-3.0-or-later", limitations = "Derived expectations are hypotheses; failed concatenations require oracle review. Code cases exercise LineContext, not an editor. Unicode covered separately." });
        using var writer = new StreamWriter(Path.Combine(Root, "corpus.jsonl"), false, new UTF8Encoding(false));
        var rates = new Dictionary<string, (int pass, int total)>(); var timer = Stopwatch.StartNew();
        var indices = only.HasValue ? new[] { only.Value } : Enumerable.Range(0, Math.Min(count, data.Count)).Select(j => j * data.Count / Math.Min(count, data.Count)).ToArray();
        foreach (var i in indices) {
            var c = data[i]; guard(i.ToString()); string actual; string? exception = null; var watch = Stopwatch.StartNew();
            try { if (c.category == "Code/terminal") actual = LineContext.Classify(c.input).ToString(); else {
                var k = new CompositionTests.Keyboard(userDictionary: new UserDictionary(null)); k.Host.PrecedingText = c.context; k.Type(c.input + "\n"); actual = k.Host.Document;
            }} catch (Exception ex) { actual = "EXCEPTION"; exception = ex.ToString(); }
            var ok = actual == c.expected && exception is null; var prior = rates.GetValueOrDefault(c.category); rates[c.category] = (prior.pass + (ok ? 1 : 0), prior.total + 1);
            Line(writer, new { c.id, @case = i, c.category, c.input, c.context, c.expected, actual, ok, elapsed_ms = watch.Elapsed.TotalMilliseconds, exception });
            if (!ok) Failure(c.id, "corpus", c.expected, actual, @case: i, exception: exception, input: c.input, context: c.context);
        }
        Save("corpus-summary.json", new { count = rates.Values.Sum(x => x.total), pass = rates.Values.Sum(x => x.pass), elapsed_ms = timer.Elapsed.TotalMilliseconds,
            categories = rates.Select(x => new { category = x.Key, passed = x.Value.pass, total = x.Value.total, rate = (double)x.Value.pass / x.Value.total }) });
    }

    // SplitMix64 makes case generation independent of runtime Random implementation and run order.
    private sealed class Rng(ulong state) { public int Next(int max) { state += 0x9E3779B97F4A7C15UL; var z = state; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return (int)((z ^ (z >> 31)) % (uint)max); } }
    private static List<ActionKey> Sequence(int index)
    {
        var r = new Rng(unchecked((ulong)Seed * 1000003 + (uint)index));
        int[] special = [0x20, 0x0D, 0x1B, 8, 0x2E, 0x25, 0x26, 0x27, 0x28, 9, 0x75, 0x76, 0x78, 0x79, 0xF3, 0xA0, 0xA2, 0xA4];
        // 5..100 key presses => 10..200 down/up events (plus explicitly counted shift events).
        var length = 5 + r.Next(96); var list = new List<ActionKey>(); var eventCount = 0;
        for (var j = 0; j < length; j++) { var kind = r.Next(10); var shift = kind < 6 && r.Next(8) == 0; var size = shift ? 4 : 2; if (eventCount + size > 200) break;
            list.Add(new(kind < 6 ? 0x41 + r.Next(26) : kind == 6 ? 0x30 + r.Next(10) : special[r.Next(special.Length)], shift, r.Next(9) == 0 ? 1000 : 10 + r.Next(100), r.Next(30))); eventCount += size; }
        return list;
    }
    private static Outcome Observe(List<ActionKey> events, bool composition = true)
    {
        var settings = TestSupport.DefaultSettings(); var env = new FakeEnvironment(); var session = new InputSession(Engine, () => settings, env);
        var input = new List<KeyEvent>(); var output = new List<KeyEvent>(); var keyboard = composition ? new CompositionTests.Keyboard(userDictionary: new UserDictionary(null)) : null;
        long now = 10000; string? issue = null; string? exception = null;
        void Drain() { while (session.TakePendingForFlush() is { } keys) output.AddRange(keys); }
        void Send(int vk, bool up) { var key = new KeyEvent(vk, input.Count, false, up, false, ++now); input.Add(key); if (!session.OnKey(key)) output.Add(key); keyboard?.Key(vk, up); }
        try { foreach (var e in events) {
            now += e.delay; if (e.action == 0) { session.OnContextChanged(now); keyboard?.Controller.CommitPending(); keyboard?.Controller.Pump(); }
            if (e.action == 1) { output.AddRange(session.Abort()); keyboard?.Controller.Reset(); keyboard?.Controller.Pump(); }
            if (e.action == 2) env.ModifierDown = !env.ModifierDown;
            if (e.action == 3 && keyboard is not null) { keyboard.Controller.CommitPending(); keyboard.Controller.Pump(); keyboard.Direct = !keyboard.Direct; }
            session.OnTimer(now); if (e.action % 3 == 0) Drain(); if (e.shift) { env.ModifierDown = true; Send(0xA0, false); } Send(e.vk, false); Send(e.vk, true); if (e.shift) { Send(0xA0, true); env.ModifierDown = false; }
            if (e.action % 4 == 0) Drain();
            if (keyboard?.Host.View is { } v) {
                if (v.Candidates.Count > 0 && (v.SelectedIndex < 0 || v.SelectedIndex >= v.Candidates.Count)) issue ??= "candidate index";
                if (v.Converting && v.Clauses is { Count: > 0 } && (v.SelectedClause < 0 || v.SelectedClause >= v.Clauses.Count)) issue ??= "clause index";
            }
            if (env.Flushes.Count > 100) env.Flushes.Clear(); if (env.Ended.Count > 100) env.Ended.Clear();
        }
            session.OnTimer(now + 10000); Drain(); output.AddRange(session.Abort());
            keyboard?.Controller.CommitPending(); keyboard?.Controller.Pump();
            if (keyboard is not null && (keyboard.Controller.IsComposing || keyboard.Showing is not null || keyboard.Gate.IsCaptured)) issue ??= "stale composition after CommitPending";
        } catch (Exception ex) { issue = "exception"; exception = ex.ToString(); }
        var lost = input.Count(e => !output.Contains(e)); var duplicate = output.Count - output.Distinct().Count();
        var reordered = output.Zip(output.Skip(1)).Count(p => p.First.Scan >= p.Second.Scan);
        if (lost > 0) issue ??= "loss"; if (duplicate > 0) issue ??= "duplication"; if (reordered > 0) issue ??= "reorder";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(new { output, document = keyboard?.Host.Document, showing = keyboard?.Showing, compositionEvents = keyboard?.Host.Events, state = session.State }))));
        return new(issue, digest, input.Count, lost, duplicate, reordered, exception);
    }
    private static List<ActionKey> Shrink(List<ActionKey> original, string property)
    {
        bool Fails(List<ActionKey> c) => Observe(c).failure == property;
        var current = original.ToList();
        for (var chunk = Math.Max(1, current.Count / 2); chunk >= 1; chunk /= 2) {
            for (var i = 0; i < current.Count;) { var trial = current.Take(i).Concat(current.Skip(i + chunk)).ToList(); if (trial.Count > 0 && Fails(trial)) current = trial; else i++; }
            if (chunk == 1) break;
        }
        for (var i = 0; i < current.Count; i++) { var trial = current.ToList(); trial[i] = trial[i] with { vk = 0x41, delay = 10 }; if (Fails(trial)) current = trial; }
        return current;
    }
    private static void Fuzz(int? only, int count, Action<string> guard, string? replay)
    {
        using var writer = new StreamWriter(Path.Combine(Root, "fuzz.jsonl"), false, new UTF8Encoding(false));
        long totalEvents = 0; var failures = 0; var exceptions = 0; long loss = 0, duplicates = 0, reorders = 0; var timer = Stopwatch.StartNew(); var shrunk = new HashSet<string>();
        if (replay is not null) { var document = JsonDocument.Parse(File.ReadAllText(replay)); var seq = JsonSerializer.Deserialize<List<ActionKey>>(document.RootElement.GetProperty("sequence"), Json)!; Save("replay.json", Observe(seq)); return; }
        for (var i = 0; i < count; i++) { if (only.HasValue && only != i) continue; guard(i.ToString()); var sequence = Sequence(i); var watch = Stopwatch.StartNew();
            var first = Observe(sequence); var second = Observe(sequence); var property = first.failure ?? (first.signature != second.signature ? "determinism" : null);
            totalEvents += first.events; loss += first.lost; duplicates += first.duplicated; reorders += first.reordered; if (first.exception is not null) exceptions++; if (property is not null) {
                failures++; var minimal = property == "determinism" || !shrunk.Add(property) ? sequence : Shrink(sequence, property);
                Failure($"F{i:D6}", "fuzz", "integrity, valid state and deterministic result", property, minimal, i, first.exception);
                if (minimal != sequence || only.HasValue) Save($"fuzz-repro-{i}.json", new { seed = Seed, @case = i, property, original_events = first.events, minimized_events = Observe(minimal).events, sequence = minimal, outcome = Observe(minimal) });
            }
            Line(writer, new { id = $"F{i:D6}", seed = Seed, @case = i, events = first.events, ok = property is null, property, first.signature, first.lost, first.duplicated, first.reordered, elapsed_ms = watch.Elapsed.TotalMilliseconds });
            if ((i + 1) % 1000 == 0) Console.WriteLine($"fuzz {i + 1}/{count} failures={failures} seconds={timer.Elapsed.TotalSeconds:F1}");
        }
        Save("fuzz-summary.json", new { sequences = only.HasValue ? 1 : count, events = totalEvents, replay_events = totalEvents, seed = Seed, failures, exceptions, hangs = 0, lost = loss, duplicated = duplicates, reordered = reorders, elapsed_ms = timer.Elapsed.TotalMilliseconds,
            scope = "Serial managed InputSession with delayed drains, timers, abort, context change and CompositionController using fake host/converter. No OS hook, SendInput, concurrency or target focus guarantee." });
    }

    private static void Stress(int count, Action<string> guard)
    {
        var settings = TestSupport.DefaultSettings(); var env = new FakeEnvironment(); var session = new InputSession(Engine, () => settings, env);
        int[] checkpoints = [0, 10000, 100000, 250000, 500000, 750000, 1000000]; var snapshots = new List<object>();
        var latency = new double[count]; var output = new Queue<KeyEvent>(); var expected = new Queue<KeyEvent>(); var lost = 0; var duplicated = 0; var reordered = 0; var exceptions = 0; int delivered = 0;
        var gc0 = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        void Snapshot(int n) { var before = GC.GetTotalMemory(false); var after = GC.GetTotalMemory(true); using var p = Process.GetCurrentProcess(); p.Refresh(); snapshots.Add(new { events = n, working_set = p.WorkingSet64, private_bytes = p.PrivateMemorySize64, heap_before_gc = before, heap_after_gc = after, threads = p.Threads.Count, handles = p.HandleCount, cpu_ms = p.TotalProcessorTime.TotalMilliseconds }); }
        void Verify() { while (output.TryDequeue(out var key)) { delivered++; if (!expected.TryDequeue(out var wanted)) duplicated++; else if (key != wanted) reordered++; } }
        void Drain() { while (session.TakePendingForFlush() is { } keys) foreach (var k in keys) output.Enqueue(k); Verify(); }
        for (var i = 0; i < 2000; i++) session.OnKey(new KeyEvent(0x58, i, false, false, false, i)); session.Abort();
        Snapshot(0); var timer = Stopwatch.StartNew(); var pattern = "konnichiwa hello github 123 ";
        for (var i = 0; i < count; i++) {
            if (i % 1000 == 0) guard(i.ToString()); var c = pattern[(i / 2) % pattern.Length]; var key = new KeyEvent(c == ' ' ? 0x20 : char.ToUpperInvariant(c), i, false, i % 2 == 1, false, 10000L + i * 10); expected.Enqueue(key);
            var start = Stopwatch.GetTimestamp(); try { if (!session.OnKey(key)) output.Enqueue(key); if (i % 16 == 0) Drain(); } catch (Exception ex) { exceptions++; Failure($"S{i}", "stress", "no exception", ex.Message, new[] { key }, i, ex.ToString()); }
            latency[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; Verify();
            env.Flushes.Clear(); env.Ended.Clear(); if (checkpoints.Contains(i + 1)) Snapshot(i + 1);
        }
        session.OnTimer(10000L + count * 10L + 10000); Drain(); foreach (var k in session.Abort()) output.Enqueue(k); Verify(); lost = expected.Count;
        var elapsed = timer.Elapsed.TotalMilliseconds; Array.Sort(latency);
        double Percentile(double q) => latency[Math.Clamp((int)Math.Ceiling(latency.Length * q) - 1, 0, latency.Length - 1)];
        Save("stress.json", new { events = count, delivered, elapsed_ms = elapsed, throughput = count / (elapsed / 1000), p50_ms = Percentile(.5), p95_ms = Percentile(.95), p99_ms = Percentile(.99), max_ms = latency[^1], lost, duplicated, reordered, exceptions,
            gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc0[i]), snapshots, scope = "Managed InputSession down/up events and synthetic reinjection consumer; not physical Windows keystrokes. Snapshot GC included in elapsed; latency includes periodic drains, excludes snapshots and validation." });
        if (lost + duplicated + reordered > 0) Failure("S-INTEGRITY", "stress", "zero integrity violations", new { lost, duplicated, reordered });
    }

    private static void Fault()
    {
        var dir = Path.Combine(Root, "isolated-fault"); Directory.CreateDirectory(dir); var results = new List<object>();
        void Check(string id, Action action) { try { action(); results.Add(new { id, ok = true }); } catch (Exception ex) { results.Add(new { id, ok = false, exception = ex.ToString() }); Failure(id, "fault", "safe fallback / preserved data / roundtrip", ex.Message, exception: ex.ToString()); } }
        var inputs = new Dictionary<string, string> { ["empty"] = "", ["object"] = "{}", ["null"] = "null", ["truncated"] = "{\"Mode\":", ["wrong-type"] = "{\"IdleFlushMs\":\"bad\"}", ["unknown"] = "{\"SettingsVersion\":5,\"Unknown\":true}", ["huge"] = "{\"JapaneseThreshold\":2147483647,\"MaxPendingKeys\":2147483647}", ["negative"] = "{\"IdleFlushMs\":-1}", ["null-rules"] = "{\"AppRules\":null}", ["nested"] = new string('[',100) + "0" + new string(']',100), ["bom"] = "\uFEFF{}", ["whitespace"] = "\n\t { } \r\n", ["large"] = "{\"Unknown\":\"" + new string('x', 1000000) + "\"}" };
        foreach (var (id, json) in inputs) Check("config-" + id, () => { var path = Path.Combine(dir, "config-" + id + ".json"); File.WriteAllText(path, json); var settings = Settings.Load(path); Assert.True(settings.IdleFlushMs >= 100 && settings.MaxPendingKeys <= 12, "normalized safe bounds"); settings.Clone(); });
        foreach (var json in new[] { "", "{", "null", "[]", "{\"api\":null}", "{\"api\":{\"English\":\"bad\"}}", "{\"api\":{\"English\":true,\"Count\":2147483647}}" }) Check("learning-" + results.Count, () => { var path = Path.Combine(dir, "learning-" + results.Count + ".json"); File.WriteAllText(path, json); var m = new LanguageMemory(path); m.Get("api"); m.Entries(); m.Remember("api", true, true); var reloaded = new LanguageMemory(path); Assert.Equal(true, reloaded.Get("api")); });
        Check("learning-roundtrip-reset", () => { var path = Path.Combine(dir, "roundtrip.json"); var m = new LanguageMemory(path); m.Remember("synthetic", true, true); Assert.Equal(true, new LanguageMemory(path).Get("synthetic")); m.Remember("synthetic", false, true); Assert.Equal(false, new LanguageMemory(path).Get("synthetic")); m.Clear(); Assert.Equal(0, new LanguageMemory(path).Count); });
        Check("config-roundtrip", () => { var path = Path.Combine(dir, "roundtrip-config.json"); var s = TestSupport.DefaultSettings(); s.Save(path); Assert.Equal(s.ToJson(), Settings.Load(path).ToJson()); });
        Check("learning-readonly", () => { var path = Path.Combine(dir, "readonly.json"); var m = new LanguageMemory(path); m.Remember("before", true, true); var before = File.ReadAllText(path); File.SetAttributes(path, FileAttributes.ReadOnly); try { m.Remember("after", true, true); Assert.Equal(before, File.ReadAllText(path)); } finally { File.SetAttributes(path, FileAttributes.Normal); } });
        Check("learning-temp-leftover", () => { var path = Path.Combine(dir, "temp-leftover.json"); var m = new LanguageMemory(path); m.Remember("before", true, true); File.WriteAllText(path + ".tmp", "{"); m.Remember("after", true, true); Assert.Equal(true, new LanguageMemory(path).Get("before")); Assert.Equal(true, new LanguageMemory(path).Get("after")); });
        Save("fault.json", results);
    }
    private static void Privacy()
    {
        var canary = "MELTYPE_TEST_SECRET_" + Guid.NewGuid().ToString("N"); var dir = Path.Combine(Root, "isolated-privacy"); Directory.CreateDirectory(dir);
        var settings = TestSupport.DefaultSettings(); var env = new FakeEnvironment { Permission = CollectPermission.Deny("password fixture") }; var session = new InputSession(Engine, () => settings, env); var typist = new Typist(session); var model = new Learning.UserModel(Path.Combine(dir, "model.json"));
        Diagnostics.Log.RecordText = false; Diagnostics.Log.SetFileOutput(Path.Combine(dir, "disabled.log"));
        typist.Type(canary + "\n"); model.Save(); Diagnostics.Log.Info("synthetic input " + Diagnostics.Log.Text(canary)); Diagnostics.Log.FlushFile(); Diagnostics.Log.SetFileOutput(null);
        var absent = Directory.GetFiles(dir).All(p => !File.ReadAllText(p).Contains(canary)); var absentRing = !Diagnostics.Log.Snapshot().Any(e => e.Message.Contains(canary));
        var disabled = !Diagnostics.Log.Text(canary).Contains(canary); Diagnostics.Log.RecordText = true; var enabled = Diagnostics.Log.Text(canary).Contains(canary); Diagnostics.Log.RecordText = false;
        var ordinaryCanary = "meltypetestsecret" + new string(Guid.NewGuid().ToString("N").Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
        Diagnostics.Log.SetFileOutput(Path.Combine(dir, "learning-disabled.log"));
        var languages = new LanguageMemory(Path.Combine(dir, "languages.json")); languages.Remember(ordinaryCanary, true, true);
        Diagnostics.Log.FlushFile(); Diagnostics.Log.SetFileOutput(null);
        var learningRedacted = !File.ReadAllText(Path.Combine(dir, "learning-disabled.log")).Contains(ordinaryCanary);
        if (!learningRedacted) Failure("PRIVACY-LEARNING-LOG", "privacy", "RecordText=false redacts full learned word in logs", "full synthetic learned word appears in file/ring log; learning file storage is intentional", input: ordinaryCanary);
        Save("privacy.json", new { permission_denied_passes_events = typist.Swallowed.Count == 0, no_learning = env.Ended.Count == 0, canary_absent_from_isolated_files = absent, canary_absent_from_ring = absentRing, log_text_off_redacts = disabled, log_text_on_includes = enabled, learning_text_off_redacts = learningRedacted,
            password_real_ui = "NOT_RUN: fake deny permission does not verify UI Automation password detection or hook bypass", network = "NOT_RUN: source audit only; no live packet/ETW capture", canary_hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canary))) });
    }
    private static void Strings(Action<string> guard)
    {
        var rows = new List<object>(); var r = new Rng((ulong)Seed); var chars = "abcxyzABC0123 日本語😀ｅ\u0301!?/-";
        foreach (var length in new[] { 0, 1, 2, 3, 6, 16, 64, 256, 1024, 10000 }) for (var i = 0; i < 10; i++) {
            guard($"string-{length}-{i}"); var input = new string(Enumerable.Range(0, length).Select(_ => chars[r.Next(chars.Length)]).ToArray()); var watch = Stopwatch.StartNew(); string? exception = null; bool ok;
            try { var text = new CompositionText(CompositionTests.Detector); foreach (var c in input) text.Append(c); ok = text.Raw == input; } catch (Exception ex) { ok = false; exception = ex.ToString(); }
            rows.Add(new { id = $"U-{length}-{i}", length, ok, exception, elapsed_ms = watch.Elapsed.TotalMilliseconds }); if (!ok) Failure($"U-{length}-{i}", "strings", "raw conservation", exception ?? "Raw != input", input: input, exception: exception);
        }
        Save("strings.json", rows);
    }
    private static void Diff(string baseline, string current)
    {
        var before = File.ReadLines(baseline).Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToDictionary(x => x.GetProperty("id").GetString()!);
        var diffs = File.ReadLines(current).Select(x => JsonDocument.Parse(x).RootElement.Clone()).Select(c => { var id = c.GetProperty("id").GetString()!; var found = before.TryGetValue(id, out var b); var same = found && b.GetProperty("actual").GetString() == c.GetProperty("actual").GetString(); return new { @case = id, category = c.GetProperty("category"), input = c.GetProperty("input"), context = c.GetProperty("context"), baseline = found ? b.GetProperty("actual").GetString() : null, current = c.GetProperty("actual").GetString(), classification = !found ? "unknown" : same ? "same" : c.GetProperty("ok").GetBoolean() ? "improved" : b.GetProperty("ok").GetBoolean() ? "regression candidate" : "unknown" }; }).ToList();
        Save("differential.json", diffs); File.WriteAllText(Path.Combine(Root, "differential.md"), "# Differential observations\n\n" + string.Join("\n", diffs.GroupBy(x => x.classification).Select(g => $"- {g.Key}: {g.Count()}")) + "\n\nSame product commit / identical corpus. This is a repeatability and test-only-change comparison, not evidence of a production improvement.\n");
    }
}
