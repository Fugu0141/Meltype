// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Meltype.Composition;

namespace Meltype.Tests;

internal static class WindowsValidation
{
    // Passive product API inspection only. Never focus a window or type through a custom automation layer.
    public static int Focus(string[] args)
    {
        using var inspector = new FocusInspector();
        using var done = new ManualResetEventSlim();
        inspector.Invalidate();
        bool fixture = false, password = false, capture = false; string? before = null, after = null;
        inspector.RequestSurroundingText((b, a) => {
            fixture = inspector.Current.Name.StartsWith("MELTYPE_VALIDATION_", StringComparison.Ordinal);
            password = inspector.Current.IsPassword;
            capture = inspector.CanCapture;
            // Save only booleans and lengths, and only when focus is our synthetic fixture.
            if (fixture) { before = b; after = a; }
            done.Set();
        });
        var completed = done.Wait(5000);
        var expectedPassword = args.ElementAtOrDefault(2) == "password";
        var ok = completed && fixture && password == expectedPassword && capture == !expectedPassword && (!password || before is null && after is null);
        var result = JsonSerializer.Serialize(new { completed, fixture, password, can_capture = capture, surrounding_before_length = before?.Length, surrounding_after_length = after?.Length, expected_password = expectedPassword, ok,
            scope = "Real Chromium UIA + product FocusInspector; passive read only, no physical input / hook / persistence E2E" });
        File.WriteAllText(args[1], result); Console.WriteLine(result); return ok ? 0 : 1;
    }
}
