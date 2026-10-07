// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;
using Meltype.Composition;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>Windows 版だけのテスト。</summary>
internal static class WindowsTests
{
    [Test]
    public static void Games_AreDetectedFromInstallFolder()
    {
        Assert.True(ForegroundTracker.LooksLikeGame(@"D:\SteamLibrary\steamapps\common\Apex Legends\r5apex.exe"), "Steam のゲーム");
        Assert.True(ForegroundTracker.LooksLikeGame(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"), "Epic のゲーム");
        Assert.True(ForegroundTracker.LooksLikeGame(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Rainbow Six Siege\RainbowSix.exe"), "Ubisoft のゲーム");
        Assert.True(ForegroundTracker.LooksLikeGame(@"C:\Program Files (x86)\SEGA\PHANTASYSTARONLINE2_JP\pso2_bin\pso2.exe"), "PSO2 NGS (専用のランチャー)");
        Assert.True(!ForegroundTracker.LooksLikeGame(@"C:\Program Files (x86)\Steam\steam.exe"), "Steam のクライアントは除く");
        Assert.True(!ForegroundTracker.LooksLikeGame(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"), "ランチャーは除く");
        Assert.True(!ForegroundTracker.LooksLikeGame(@"C:\Program Files\Microsoft VS Code\Code.exe"), "ふつうのアプリ");

        var settings = new Settings();
        Assert.True(settings.IsGame("r5apex.exe", looksLikeGame: true), "ゲームのフォルダーなら止める");
        settings.StopInGames = false;
        Assert.True(!settings.IsGame("r5apex.exe", looksLikeGame: true), "設定で OFF にしたら止めない");
        settings.AppRules.Add(new AppRule { Process = "minecraft.exe", Enabled = true, Profile = AppProfile.Game });
        Assert.True(settings.IsGame("minecraft.exe", looksLikeGame: false), "アプリ別設定で「ゲーム」にしたら止める");
    }
    [Test]
    public static void NativeMessageProbe_IsLimitedToUnityInspector()
    {
        var pane = new FocusInfo(false, false, null, "ControlType 50033 \"UnityEditor.InspectorWindow\" (UnityGUIViewWndClass)", ClassName: "UnityGUIViewWndClass");
        var hint = new NativeMessageInputProbe.Snapshot(1, 123, 0x0201, 1, 1, 1, 1_000, 0x1234, 0, 0);

        Assert.True(NativeMessageInputProbe.ShouldUseUnityFallback("Unity.exe", pane, hint, 123, 1_100), "Unity Inspector + native hint");
        Assert.True(!NativeMessageInputProbe.ShouldUseUnityFallback("chrome.exe", pane, hint, 123, 1_100), "他アプリには広げない");
        Assert.True(!NativeMessageInputProbe.ShouldUseUnityFallback("Unity.exe", pane, hint with { TextInputHint = 0 }, 123, 1_100), "hint が無ければ使わない");
        Assert.True(!NativeMessageInputProbe.ShouldUseUnityFallback("Unity.exe", pane, hint, 999, 1_100), "別スレッドの状態を使わない");
        Assert.True(!NativeMessageInputProbe.ShouldUseUnityFallback("Unity.exe", pane, hint, 123, 20_000), "古い状態を使わない");

        var edit = new FocusInfo(true, false, null, "Edit", ClassName: "UnityGUIViewWndClass");
        Assert.True(!NativeMessageInputProbe.ShouldUseUnityFallback("Unity.exe", edit, hint, 123, 1_100), "UIA が入力欄なら fallback は不要");
    }

    [Test]
    public static void NativeMessageProbe_MissingDllFailsOpen()
    {
        using var probe = new NativeMessageInputProbe(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll"));
        Assert.True(!probe.Available, "DLL が無くても例外にせず従来判定へ戻る");
    }

}