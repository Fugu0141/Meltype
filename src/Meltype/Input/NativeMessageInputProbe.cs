// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Meltype.Composition;
using Meltype.Diagnostics;
using Meltype.IME;

namespace Meltype.Input;

/// <summary>
/// UI Automation から入力欄が見えないアプリ向けの実験的な補助検出。
/// 対象スレッドへ WH_GETMESSAGE / WH_CALLWNDPROCRET を入れ、対象プロセス内から IMM 状態を読む。
/// Issue #111 の Unity Editor だけに限定して有効化する。
/// </summary>
internal sealed class NativeMessageInputProbe : IDisposable
{
    internal const uint Magic = 0x3148494D;
    internal const uint Version = 1;
    private const string MappingName = @"Local\Meltype.InputMessageHook.v1";
    private const int WH_GETMESSAGE = 3, WH_CALLWNDPROCRET = 12;
    private const int MaxSnapshotAgeMs = 10_000;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ProbeState
    {
        public uint Magic;
        public uint Version;
        public long Sequence;
        public uint ProcessId;
        public uint ThreadId;
        public uint Message;
        public uint Source;
        public int HasImeContext;
        public int ImeOpen;
        public int TextInputHint;
        public int Reserved;
        public ulong Tick;
        public long Hwnd;
        public uint NotifyCode;
        public uint Flags;
        public int SuppressNextCharacter;
        public uint SuppressThreadId;
    }

    internal readonly record struct Snapshot(
        uint ProcessId, uint ThreadId, uint Message, int HasImeContext, int ImeOpen,
        int TextInputHint, ulong Tick, long Hwnd, uint NotifyCode, uint Flags);

    private readonly object _gate = new();
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private IntPtr _module;
    private IntPtr _getMessageProc;
    private IntPtr _callWndRetProc;
    private IntPtr _getMessageHook;
    private IntPtr _callWndRetHook;
    private uint _threadId;
    private bool _loggedUnavailable;

    public NativeMessageInputProbe(string? dllPath = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            dllPath ??= Path.Combine(AppContext.BaseDirectory, "meltype_input_hook.dll");
            if (!File.Exists(dllPath)) return;

            _mapping = MemoryMappedFile.CreateOrOpen(MappingName, 4096, MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, 4096, MemoryMappedFileAccess.ReadWrite);
            ResetState();

            _module = NativeLibrary.Load(dllPath);
            _getMessageProc = NativeLibrary.GetExport(_module, "MeltypeGetMessageHookProc");
            _callWndRetProc = NativeLibrary.GetExport(_module, "MeltypeCallWndRetHookProc");
        }
        catch (Exception ex)
        {
            Log.Warn($"ネイティブ入力欄プローブを読み込めません: {ex.Message}");
            Dispose();
        }
    }

    public bool Available => _module != IntPtr.Zero && _view is not null;

    internal static bool SupportsProcess(string processName) =>
        string.Equals(Path.GetFileNameWithoutExtension(processName), "Unity", StringComparison.OrdinalIgnoreCase);

    /// <summary>Unity Editor の UIA では Pane にしか見えない Inspector でだけ native hint を採用する。</summary>
    internal static bool ShouldUseUnityFallback(string processName, FocusInfo info, Snapshot snapshot, uint expectedThread, ulong now)
    {
        if (!SupportsProcess(processName)) return false;
        if (!string.Equals(info.ClassName, "UnityGUIViewWndClass", StringComparison.Ordinal)) return false;
        if (info.IsPassword || info.IsTextInput) return false;
        if (snapshot.ThreadId == 0 || snapshot.ThreadId != expectedThread) return false;
        if (snapshot.TextInputHint == 0) return false;
        if (now >= snapshot.Tick && now - snapshot.Tick > MaxSnapshotAgeMs) return false;
        return true;
    }

    public void Retarget(ImeTarget? target, string processName)
    {
        lock (_gate)
        {
            if (!Available || target is null || !SupportsProcess(processName))
            {
                Unhook();
                return;
            }
            if (_threadId == target.ThreadId && _getMessageHook != IntPtr.Zero && _callWndRetHook != IntPtr.Zero) return;

            Unhook();
            ResetState();
            _getMessageHook = Native.SetWindowsHookExNative(WH_GETMESSAGE, _getMessageProc, _module, target.ThreadId);
            _callWndRetHook = Native.SetWindowsHookExNative(WH_CALLWNDPROCRET, _callWndRetProc, _module, target.ThreadId);
            if (_getMessageHook == IntPtr.Zero || _callWndRetHook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Unhook();
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    Log.Warn($"Unity の入力メッセージフックを設定できませんでした (Win32 error {error})。UI Automation の判定だけを使います。");
                }
                return;
            }

            _threadId = target.ThreadId;
            Log.Info($"Unity の入力メッセージを監視します (thread {target.ThreadId})。");
        }
    }

    public bool CanCapture(string processName, FocusInfo info)
    {
        if (!Available || _threadId == 0) return false;
        var target = ImeTarget.FromForeground();
        if (target is null || target.ThreadId != _threadId) return false;
        if (!TryRead(out var snapshot)) return false;
        return ShouldUseUnityFallback(processName, info, snapshot, target.ThreadId, unchecked((ulong)Environment.TickCount64));
    }

    /// <summary>
    /// WH_GETMESSAGE 側には MSG を WM_NULL に差し替える機構も用意する。
    /// 現在は診断・検出を優先し、自動では使わない。
    /// </summary>
    public void ArmSuppressNextCharacter()
    {
        lock (_gate)
        {
            if (_view is null || _threadId == 0) return;
            _view.Write(Marshal.OffsetOf<ProbeState>(nameof(ProbeState.SuppressThreadId)).ToInt64(), _threadId);
            _view.Write(Marshal.OffsetOf<ProbeState>(nameof(ProbeState.SuppressNextCharacter)).ToInt64(), 1);
        }
    }

    internal bool TryRead(out Snapshot snapshot)
    {
        snapshot = default;
        var view = _view;
        if (view is null) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = view.ReadInt64(8);
            if ((before & 1) != 0) continue;
            view.Read(0, out ProbeState state);
            var after = view.ReadInt64(8);
            if (before != after || (after & 1) != 0 || state.Magic != Magic || state.Version != Version) continue;
            snapshot = new Snapshot(state.ProcessId, state.ThreadId, state.Message, state.HasImeContext, state.ImeOpen,
                state.TextInputHint, state.Tick, state.Hwnd, state.NotifyCode, state.Flags);
            return true;
        }
        return false;
    }

    private void ResetState()
    {
        if (_view is null) return;
        var state = new ProbeState { Magic = Magic, Version = Version };
        _view.Write(0, ref state);
        _view.Flush();
    }

    private void Unhook()
    {
        if (_getMessageHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_getMessageHook); _getMessageHook = IntPtr.Zero; }
        if (_callWndRetHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_callWndRetHook); _callWndRetHook = IntPtr.Zero; }
        _threadId = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Unhook();
            if (_module != IntPtr.Zero)
            {
                NativeLibrary.Free(_module);
                _module = IntPtr.Zero;
            }
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
        }
    }
}
