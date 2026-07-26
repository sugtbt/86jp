#include "86JP.h"
#include "AuctionRegistrationAckFix.h"
#include "HookInterface.h"
#include "XLog.h"

#include <intrin.h>
#include <mutex>

#pragma comment(lib, "user32.lib")

static uintptr_t dnf_base = 0;

void __cdecl ProxyGameLog(int a1, wchar_t* source_path, wchar_t* function_name, int logType, wchar_t* Format, ...)
{
    wchar_t Buffer[512] = { 0 };
    wchar_t* dynamicBuffer = NULL;
    wchar_t* outputBuffer = Buffer;
    int bufferSize = _countof(Buffer);

    va_list ArgList;
    va_start(ArgList, Format);

    int result = _vswprintf_c_l(Buffer, bufferSize, Format, 0, ArgList);

    if (result < 0) {
        va_end(ArgList);
        va_start(ArgList, Format);

        int neededSize = _vscwprintf_l(Format, 0, ArgList) + 1;

        if (neededSize > 0) {
            dynamicBuffer = (wchar_t*)malloc(neededSize * sizeof(wchar_t));
            if (dynamicBuffer) {
                va_end(ArgList);
                va_start(ArgList, Format);
                _vswprintf_c_l(dynamicBuffer, neededSize, Format, 0, ArgList);
                outputBuffer = dynamicBuffer;
            }
        }
    }

    va_end(ArgList);

    if (outputBuffer) {
        AppendFileLogFormatLine(L"GameLog.log", L"[%s] [%d] [%s]", function_name, logType, outputBuffer);
    }

    if (dynamicBuffer) {
        free(dynamicBuffer);
    }
}

int __fastcall Proxy_CipherEncrypt(void* This, void* NotUsed, int packet_type, char* input, int in_size, char* out_put, int* out_size)
{
    *(int*)(input - 13 + 3) = in_size + 13;

    *out_size = in_size;
    memcpy(out_put, input, in_size);
    return 1;
}

static uintptr_t g_Ptr_SendMessageW = 0;
LRESULT WINAPI Proxy_SendMessageW(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)
{
    if (Msg == 0x111 && wParam == 0x19F && lParam == 0)
        return 0;
    auto original = reinterpret_cast<decltype(&Proxy_SendMessageW)>(Hook_GetTrampoline(g_Ptr_SendMessageW));
    return original(hWnd, Msg, wParam, lParam);
}

static uintptr_t g_Ptr_AuctionRegisterItemResult = 0;
using AuctionRegisterItemResultFn = void(__thiscall*)(void* self, int success, int errorCode);
using FindPopupListFn = unsigned char* (__thiscall*)(void* manager, unsigned int popupType);
using AuctionRegistrationCleanupFn = void(__thiscall*)(void* dialog);
using AuctionRegistrationProcFn = void(__thiscall*)(void* dialog);
using AuctionRegistrationCloseFn = void(__thiscall*)(void* dialog);
using FindItemSpaceFn = void* (__cdecl*)(unsigned int itemSpaceId);
using GetItemSpaceQueueCountFn = int(__thiscall*)(void* itemSpace);

static AuctionRegistrationCleanupFn g_AuctionRegistrationSuccessCleanup = nullptr;
static AuctionRegistrationProcFn g_AuctionRegistrationProc = nullptr;
static AuctionRegistrationCloseFn g_AuctionRegistrationClose = nullptr;
static AuctionRegistrationAckFix::DeferredDialogIdentity
    g_DeferredAuctionRegistrationSuccessCleanup;
static bool g_DeferredAuctionRegistrationStaleLogged = false;
static bool g_WaitForPostAcknowledgementTick = false;

static void ClearDeferredAuctionRegistrationSuccessCleanup()
{
    g_DeferredAuctionRegistrationSuccessCleanup = {};
    g_DeferredAuctionRegistrationStaleLogged = false;
    g_WaitForPostAcknowledgementTick = false;
}

static bool ForEachAuctionRegistrationDialog(
    void(*visitor)(void* dialog))
{
    constexpr unsigned int AuctionRegistrationPopupType = 0xE8;
    constexpr uintptr_t FindPopupListOffset = 0x01899890;
    constexpr uintptr_t InterfaceManagerPointerOffset = 0x02C91F7C;
    constexpr uintptr_t AuctionRegistrationVtableOffset = 0x028AA704;

    auto manager = *reinterpret_cast<void**>(
        dnf_base + InterfaceManagerPointerOffset);
    if (manager == nullptr)
        return false;

    auto findPopupList = reinterpret_cast<FindPopupListFn>(
        dnf_base + FindPopupListOffset);
    auto popupList = findPopupList(
        manager,
        AuctionRegistrationPopupType);
    if (popupList == nullptr)
        return false;

    auto begin = *reinterpret_cast<void***>(popupList + 4);
    auto end = *reinterpret_cast<void***>(popupList + 8);
    if (begin == nullptr || end == nullptr || begin > end)
        return false;

    const auto expectedVtable = reinterpret_cast<void*>(
        dnf_base + AuctionRegistrationVtableOffset);
    for (auto current = begin; current != end; ++current)
    {
        auto dialog = *current;
        if (dialog != nullptr
            && *reinterpret_cast<void**>(dialog) == expectedVtable)
        {
            visitor(dialog);
        }
    }
    return true;
}

static void FinalizeFailedAuctionRegistration(void* dialog)
{
    AuctionRegistrationAckFix::FinalizeFailureAcknowledgement(dialog);
}

static bool PatchRelativeCall(
    uintptr_t callSite,
    uintptr_t expectedTarget,
    void* replacement)
{
    auto instruction = reinterpret_cast<unsigned char*>(callSite);
    if (instruction[0] != 0xE8)
        return false;

    const auto currentDisplacement = *reinterpret_cast<int*>(instruction + 1);
    const auto currentTarget = callSite + 5 + currentDisplacement;
    if (currentTarget != expectedTarget)
        return false;

    const auto replacementAddress = reinterpret_cast<uintptr_t>(replacement);
    const auto replacementDisplacement = static_cast<int>(
        replacementAddress - (callSite + 5));

    DWORD oldProtection = 0;
    if (!VirtualProtect(
            instruction,
            5,
            PAGE_EXECUTE_READWRITE,
            &oldProtection))
    {
        return false;
    }

    *reinterpret_cast<int*>(instruction + 1) = replacementDisplacement;
    FlushInstructionCache(GetCurrentProcess(), instruction, 5);

    DWORD ignoredProtection = 0;
    VirtualProtect(
        instruction,
        5,
        oldProtection,
        &ignoredProtection);
    return true;
}

static bool PatchPointer(
    uintptr_t slotAddress,
    void* replacement,
    void** original)
{
    auto slot = reinterpret_cast<void**>(slotAddress);
    DWORD oldProtection = 0;
    if (!VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_READWRITE,
            &oldProtection))
    {
        return false;
    }

    *original = *slot;
    *slot = replacement;

    DWORD ignoredProtection = 0;
    VirtualProtect(
        slot,
        sizeof(void*),
        oldProtection,
        &ignoredProtection);
    return true;
}

static AuctionRegistrationAckFix::DeferredCleanupResult
TryDeferredAuctionRegistrationSuccessCleanup(void* dialog)
{
    using AuctionRegistrationAckFix::DeferredCleanupResult;

    if (g_DeferredAuctionRegistrationSuccessCleanup.dialog != dialog)
        return DeferredCleanupResult::NotDeferred;

    if (AuctionRegistrationAckFix::ConsumePostAcknowledgementTick(
            g_WaitForPostAcknowledgementTick))
    {
        return DeferredCleanupResult::Pending;
    }

    if (AuctionRegistrationAckFix::IsSuccessCleanupComplete(dialog))
    {
        ClearDeferredAuctionRegistrationSuccessCleanup();
        return DeferredCleanupResult::Completed;
    }

    constexpr uintptr_t FindItemSpaceOffset = 0x00D95870;
    constexpr uintptr_t GetItemSpaceQueueCountOffset = 0x00E2F5A0;
    constexpr uintptr_t AuctionRegistrationVtableOffset = 0x028AA704;
    const auto expectedVtable = reinterpret_cast<void*>(
        dnf_base + AuctionRegistrationVtableOffset);
    if (g_DeferredAuctionRegistrationSuccessCleanup.vtable != expectedVtable
        || !AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
            dialog,
            g_DeferredAuctionRegistrationSuccessCleanup))
    {
        if (!g_DeferredAuctionRegistrationStaleLogged)
        {
            AppendFileLogFormatLine(
                L"GameLog.log",
                L"[AuctionPatch] stale deferred cleanup blocked dialog=%p",
                dialog);
            g_DeferredAuctionRegistrationStaleLogged = true;
        }
        return DeferredCleanupResult::Stale;
    }
    g_DeferredAuctionRegistrationStaleLogged = false;

    auto temporaryItemSpace = const_cast<void*>(
        g_DeferredAuctionRegistrationSuccessCleanup.temporaryItemSpace);
    const auto bagItemSpaceId =
        g_DeferredAuctionRegistrationSuccessCleanup.bagItemSpaceId;
    auto findItemSpace = reinterpret_cast<FindItemSpaceFn>(
        dnf_base + FindItemSpaceOffset);
    auto bagItemSpace = findItemSpace(bagItemSpaceId);
    if (bagItemSpace == nullptr || temporaryItemSpace == nullptr)
        return DeferredCleanupResult::Pending;

    auto getQueueCount = reinterpret_cast<GetItemSpaceQueueCountFn>(
        dnf_base + GetItemSpaceQueueCountOffset);
    const auto bagQueueCount = getQueueCount(bagItemSpace);
    const auto temporaryQueueCount = getQueueCount(temporaryItemSpace);
    if (!AuctionRegistrationAckFix::CanRetrySuccessCleanup(
            dialog,
            bagQueueCount,
            temporaryQueueCount))
    {
        return DeferredCleanupResult::Pending;
    }

    g_AuctionRegistrationSuccessCleanup(dialog);
    if (AuctionRegistrationAckFix::NeedsDeferredSuccessCleanup(dialog))
        return DeferredCleanupResult::Pending;

    AppendFileLogFormatLine(
        L"GameLog.log",
        L"[AuctionPatch] deferred success cleanup completed dialog=%p",
        dialog);
    ClearDeferredAuctionRegistrationSuccessCleanup();
    return DeferredCleanupResult::Completed;
}

void __fastcall Proxy_AuctionRegistrationSuccessCleanup(
    void* dialog,
    void*)
{
    g_AuctionRegistrationSuccessCleanup(dialog);
    if (AuctionRegistrationAckFix::ShouldTrackPostAcknowledgementCleanup(
            dialog))
    {
        g_DeferredAuctionRegistrationSuccessCleanup =
            AuctionRegistrationAckFix::CaptureDeferredDialogIdentity(dialog);
        g_DeferredAuctionRegistrationStaleLogged = false;
        g_WaitForPostAcknowledgementTick = true;
        AppendFileLogFormatLine(
            L"GameLog.log",
            L"[AuctionPatch] post-ACK cleanup scheduled dialog=%p selected=%08X pending=%d",
            dialog,
            g_DeferredAuctionRegistrationSuccessCleanup.selectedItem,
            static_cast<const unsigned char*>(dialog)[
                AuctionRegistrationAckFix::SubmitPendingOffset]);
    }
}

void __fastcall Proxy_AuctionRegistrationProc(
    void* dialog,
    void*)
{
    g_AuctionRegistrationProc(dialog);
    TryDeferredAuctionRegistrationSuccessCleanup(dialog);
}

void __fastcall Proxy_AuctionRegistrationClose(
    void* dialog,
    void*)
{
    const auto cleanupResult =
        TryDeferredAuctionRegistrationSuccessCleanup(dialog);
    if (!AuctionRegistrationAckFix::CanCallNativeCloseAfterCleanup(
            cleanupResult))
    {
        return;
    }

    g_AuctionRegistrationClose(dialog);
}

void __fastcall Proxy_AuctionRegisterItemResult(
    void* self,
    void*,
    int success,
    int errorCode)
{
    auto original = reinterpret_cast<AuctionRegisterItemResultFn>(
        Hook_GetTrampoline(g_Ptr_AuctionRegisterItemResult));
    original(self, success, errorCode);

    // A failed request owns no server-side escrow. Releasing only the submit
    // lock lets the dialog retry or use its native close-time item rollback.
    if (success == 0)
        ForEachAuctionRegistrationDialog(FinalizeFailedAuctionRegistration);
}

unsigned int DelayHook(void*)
{
    do
    {
        Sleep(100);
    } while (nullptr == GetModuleHandleW(L"GameGaurd.dll"));

    Sleep(1000);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01C11360), Proxy_CipherEncrypt);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);

    return 0;
}

void PluginEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    DeleteFileW(L"GameLog.log");

    CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)DelayHook, NULL, 0, NULL);

    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9800), ProxyGameLog);

    constexpr uintptr_t AuctionSuccessCleanupOffset = 0x018C0D80;
    constexpr uintptr_t AuctionSuccessCleanupCallSiteOffset = 0x018D5C29;
    constexpr uintptr_t AuctionRegistrationProcVtableSlotOffset = 0x028AA714;
    constexpr uintptr_t AuctionRegistrationCloseVtableSlotOffset = 0x028AA720;
    g_AuctionRegistrationSuccessCleanup =
        reinterpret_cast<AuctionRegistrationCleanupFn>(
            dnf_base + AuctionSuccessCleanupOffset);
    const auto cleanupCallPatched = PatchRelativeCall(
        dnf_base + AuctionSuccessCleanupCallSiteOffset,
        reinterpret_cast<uintptr_t>(g_AuctionRegistrationSuccessCleanup),
        Proxy_AuctionRegistrationSuccessCleanup);
    const auto procPatched = PatchPointer(
        dnf_base + AuctionRegistrationProcVtableSlotOffset,
        Proxy_AuctionRegistrationProc,
        reinterpret_cast<void**>(&g_AuctionRegistrationProc));
    const auto closePatched = PatchPointer(
        dnf_base + AuctionRegistrationCloseVtableSlotOffset,
        Proxy_AuctionRegistrationClose,
        reinterpret_cast<void**>(&g_AuctionRegistrationClose));

    g_Ptr_AuctionRegisterItemResult = dnf_base + 0x018D5BA0;
    BOOLEAN resultHandlerPatched = FALSE;
    if (cleanupCallPatched && procPatched && closePatched)
    {
        resultHandlerPatched = Hook_Inline(
            reinterpret_cast<void*>(g_Ptr_AuctionRegisterItemResult),
            Proxy_AuctionRegisterItemResult);
    }
    if (!cleanupCallPatched
        || !procPatched
        || !closePatched
        || !resultHandlerPatched)
    {
        if (cleanupCallPatched)
        {
            PatchRelativeCall(
                dnf_base + AuctionSuccessCleanupCallSiteOffset,
                reinterpret_cast<uintptr_t>(
                    Proxy_AuctionRegistrationSuccessCleanup),
                g_AuctionRegistrationSuccessCleanup);
        }
        if (procPatched)
        {
            void* ignoredOriginal = nullptr;
            PatchPointer(
                dnf_base + AuctionRegistrationProcVtableSlotOffset,
                reinterpret_cast<void*>(g_AuctionRegistrationProc),
                &ignoredOriginal);
        }
        if (closePatched)
        {
            void* ignoredOriginal = nullptr;
            PatchPointer(
                dnf_base + AuctionRegistrationCloseVtableSlotOffset,
                reinterpret_cast<void*>(g_AuctionRegistrationClose),
                &ignoredOriginal);
        }
        AppendFileLogFormatLine(
            L"GameLog.log",
            L"[AuctionPatch] install failed cleanup=%d proc=%d close=%d result=%d",
            cleanupCallPatched,
            procPatched,
            closePatched,
            resultHandlerPatched);
    }
    else
    {
        AppendFileLogFormatLine(
            L"GameLog.log",
            L"[AuctionPatch] installed");
    }

    auto user32 = GetModuleHandleW(L"user32.dll");
    if (user32)
    {
        g_Ptr_SendMessageW = (uintptr_t)GetProcAddress(user32, "SendMessageW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_SendMessageW), Proxy_SendMessageW);
    }
}

uintptr_t g_Ptr_GetStartupInfoW = 0;
VOID WINAPI Proxy_GetStartupInfoW(_Out_ LPSTARTUPINFOW lpStartupInfo)
{
    auto return_addr = (uintptr_t)_ReturnAddress();
    if (return_addr == dnf_base + 0x04AE71A5)
        PluginEntry();

    auto orifunc = reinterpret_cast<decltype(&Proxy_GetStartupInfoW)>(Hook_GetTrampoline(g_Ptr_GetStartupInfoW));
    orifunc(lpStartupInfo);
}

void JPEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    auto kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32)
    {
        g_Ptr_GetStartupInfoW = (uintptr_t)GetProcAddress(kernel32, "GetStartupInfoW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_GetStartupInfoW), Proxy_GetStartupInfoW);
    }
}
