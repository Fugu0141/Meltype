// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro
//
// Thread-specific message hook used by Meltype to detect text-entry state in
// applications whose controls are not exposed through UI Automation (Issue #111).
//
// The hook deliberately does not record typed characters. It only publishes
// message IDs and IMM state through a small session-local shared memory block.

#include <windows.h>
#include <imm.h>
#include <stdint.h>

#pragma comment(lib, "imm32.lib")

namespace
{
constexpr wchar_t MappingName[] = L"Local\\Meltype.InputMessageHook.v1";
constexpr uint32_t Magic = 0x3148494D;
constexpr uint32_t Version = 1;
constexpr uint32_t SourceGetMessage = 1;
constexpr uint32_t SourceCallWndRet = 2;
constexpr uint32_t FlagSuppressed = 1;

#pragma pack(push, 1)
struct ProbeState
{
    uint32_t magic;
    uint32_t version;
    volatile LONG64 sequence;
    uint32_t processId;
    uint32_t threadId;
    uint32_t message;
    uint32_t source;
    int32_t hasImeContext;
    int32_t imeOpen;
    int32_t textInputHint;
    int32_t reserved;
    uint64_t tick;
    int64_t hwnd;
    uint32_t notifyCode;
    uint32_t flags;
    volatile LONG suppressNextCharacter;
    uint32_t suppressThreadId;
};
#pragma pack(pop)

static HANDLE g_mapping = nullptr;
static ProbeState* g_state = nullptr;

ProbeState* SharedState()
{
    if (g_state != nullptr) return g_state;
    g_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, MappingName);
    if (g_mapping == nullptr) return nullptr;
    g_state = static_cast<ProbeState*>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ProbeState)));
    if (g_state == nullptr)
    {
        CloseHandle(g_mapping);
        g_mapping = nullptr;
    }
    return g_state;
}

bool IsImeMessage(UINT message)
{
    return message == WM_IME_SETCONTEXT ||
           message == WM_IME_STARTCOMPOSITION ||
           message == WM_IME_COMPOSITION ||
           message == WM_IME_ENDCOMPOSITION ||
           message == WM_IME_NOTIFY ||
           message == WM_IME_CHAR;
}

bool IsProbeMessage(UINT message)
{
    return IsImeMessage(message) ||
           message == WM_SETFOCUS ||
           message == WM_KILLFOCUS ||
           message == WM_LBUTTONDOWN ||
           message == WM_RBUTTONDOWN ||
           message == WM_MBUTTONDOWN ||
           message == WM_LBUTTONUP ||
           message == WM_RBUTTONUP ||
           message == WM_MBUTTONUP ||
           message == WM_KEYDOWN ||
           message == WM_CHAR ||
           message == WM_SYSCHAR;
}

void QueryIme(HWND hwnd, int32_t& hasContext, int32_t& open)
{
    hasContext = 0;
    open = 0;
    if (hwnd == nullptr) return;
    HIMC context = ImmGetContext(hwnd);
    if (context == nullptr) return;
    hasContext = 1;
    open = ImmGetOpenStatus(context) ? 1 : 0;
    ImmReleaseContext(hwnd, context);
}

void Publish(HWND hwnd, UINT message, UINT source, WPARAM originalWParam, uint32_t flags)
{
    if (!IsProbeMessage(message)) return;
    ProbeState* state = SharedState();
    if (state == nullptr || state->magic != Magic || state->version != Version) return;

    int32_t hasContext = 0;
    int32_t open = 0;
    QueryIme(hwnd, hasContext, open);

    LONG hint = state->textInputHint;
    if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN ||
        message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP)
        hint = (hasContext != 0 && open != 0) ? 1 : 0;
    else if (message == WM_IME_STARTCOMPOSITION || message == WM_IME_COMPOSITION || message == WM_IME_CHAR)
        hint = 1;
    else if (message == WM_IME_NOTIFY && originalWParam == IMN_SETOPENSTATUS && open != 0)
        hint = 1;
    else if (message == WM_KILLFOCUS || (message == WM_IME_SETCONTEXT && originalWParam == 0))
        hint = 0;
    else if (message == WM_IME_SETCONTEXT && originalWParam != 0 && hasContext != 0)
        hint = 1;

    InterlockedIncrement64(&state->sequence);
    MemoryBarrier();
    state->processId = GetCurrentProcessId();
    state->threadId = GetCurrentThreadId();
    state->message = message;
    state->source = source;
    state->hasImeContext = hasContext;
    state->imeOpen = open;
    state->textInputHint = hint;
    state->tick = GetTickCount64();
    state->hwnd = reinterpret_cast<int64_t>(hwnd);
    state->notifyCode = message == WM_IME_NOTIFY ? static_cast<uint32_t>(originalWParam) : 0;
    state->flags = flags;
    MemoryBarrier();
    InterlockedIncrement64(&state->sequence);
}

bool ShouldSuppress(ProbeState* state, UINT message)
{
    if (state == nullptr || InterlockedCompareExchange(&state->suppressNextCharacter, 0, 0) == 0) return false;
    if (state->suppressThreadId != GetCurrentThreadId()) return false;
    if (message != WM_CHAR && message != WM_SYSCHAR && message != WM_IME_CHAR) return false;
    InterlockedExchange(&state->suppressNextCharacter, 0);
    return true;
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK MeltypeGetMessageHookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0 && lParam != 0)
    {
        MSG* msg = reinterpret_cast<MSG*>(lParam);
        const UINT originalMessage = msg->message;
        const WPARAM originalWParam = msg->wParam;
        uint32_t flags = 0;
        ProbeState* state = SharedState();
        if (wParam == PM_REMOVE && ShouldSuppress(state, originalMessage))
        {
            msg->message = WM_NULL;
            msg->wParam = 0;
            msg->lParam = 0;
            flags |= FlagSuppressed;
        }
        Publish(msg->hwnd, originalMessage, SourceGetMessage, originalWParam, flags);
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

extern "C" __declspec(dllexport) LRESULT CALLBACK MeltypeCallWndRetHookProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0 && lParam != 0)
    {
        const CWPRETSTRUCT* data = reinterpret_cast<const CWPRETSTRUCT*>(lParam);
        Publish(data->hwnd, data->message, SourceCallWndRet, data->wParam, 0);
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH)
    {
        if (g_state != nullptr) { UnmapViewOfFile(g_state); g_state = nullptr; }
        if (g_mapping != nullptr) { CloseHandle(g_mapping); g_mapping = nullptr; }
    }
    return TRUE;
}
