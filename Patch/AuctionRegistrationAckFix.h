#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace AuctionRegistrationAckFix
{
    constexpr std::size_t VtableOffset = 0;
    constexpr std::size_t TemporaryItemSpaceOffset = 0xFC;
    constexpr std::size_t BagItemSpaceIdOffset = 0x104;
    constexpr std::size_t SelectedItemOffset = 0x134;
    constexpr std::size_t SubmitPendingOffset = 0x168;
    constexpr std::size_t DialogSize = SubmitPendingOffset + 1;

    enum class DeferredCleanupResult
    {
        NotDeferred,
        Completed,
        Pending,
        Stale,
    };

    inline bool CanCallNativeCloseAfterCleanup(
        DeferredCleanupResult result)
    {
        return result == DeferredCleanupResult::NotDeferred
            || result == DeferredCleanupResult::Completed;
    }

    struct DeferredDialogIdentity
    {
        const void* dialog = nullptr;
        const void* vtable = nullptr;
        const void* temporaryItemSpace = nullptr;
        std::uint32_t bagItemSpaceId = 0;
        std::uint32_t selectedItem = 0;
    };

    template <typename T>
    inline T ReadField(const void* dialog, std::size_t offset)
    {
        T value{};
        std::memcpy(
            &value,
            static_cast<const std::uint8_t*>(dialog) + offset,
            sizeof(value));
        return value;
    }

    inline DeferredDialogIdentity CaptureDeferredDialogIdentity(
        const void* dialog)
    {
        if (dialog == nullptr)
            return {};

        return {
            dialog,
            ReadField<const void*>(dialog, VtableOffset),
            ReadField<const void*>(dialog, TemporaryItemSpaceOffset),
            ReadField<std::uint32_t>(dialog, BagItemSpaceIdOffset),
            ReadField<std::uint32_t>(dialog, SelectedItemOffset),
        };
    }

    inline bool MatchesDeferredDialogIdentity(
        const void* dialog,
        const DeferredDialogIdentity& identity)
    {
        if (dialog == nullptr || dialog != identity.dialog)
            return false;

        const auto currentSelectedItem = ReadField<std::uint32_t>(
            dialog,
            SelectedItemOffset);
        return ReadField<const void*>(dialog, VtableOffset) == identity.vtable
            && ReadField<const void*>(
                dialog,
                TemporaryItemSpaceOffset) == identity.temporaryItemSpace
            && ReadField<std::uint32_t>(
                dialog,
                BagItemSpaceIdOffset) == identity.bagItemSpaceId
            && (currentSelectedItem == identity.selectedItem
                || currentSelectedItem == 0);
    }

    inline bool IsSuccessCleanupComplete(const void* dialog)
    {
        if (dialog == nullptr)
            return false;

        const auto bytes = static_cast<const std::uint8_t*>(dialog);
        return ReadField<std::uint32_t>(
                   dialog,
                   SelectedItemOffset) == 0
            && bytes[SubmitPendingOffset] == 0;
    }

    inline bool NeedsDeferredSuccessCleanup(const void* dialog)
    {
        return dialog != nullptr && !IsSuccessCleanupComplete(dialog);
    }

    inline bool ShouldTrackPostAcknowledgementCleanup(const void* dialog)
    {
        // On a local loopback connection the successful ACK can be handled
        // reentrantly before the submit function writes its pending byte.
        // Keep one deferred identity even when native cleanup looks complete
        // so the next UI tick observes the post-submit state.
        return dialog != nullptr;
    }

    inline bool ConsumePostAcknowledgementTick(bool& waitForTick)
    {
        if (!waitForTick)
            return false;

        waitForTick = false;
        return true;
    }

    inline bool CanRetrySuccessCleanup(
        const void* dialog,
        int bagQueueCount,
        int temporaryItemSpaceQueueCount)
    {
        return NeedsDeferredSuccessCleanup(dialog)
            && bagQueueCount >= 0
            && bagQueueCount <= 1
            && temporaryItemSpaceQueueCount == 0;
    }

    inline bool ShouldBlockCloseForDeferredSuccessCleanup(
        const void* dialog,
        const void* deferredDialog)
    {
        return dialog != nullptr
            && dialog == deferredDialog
            && NeedsDeferredSuccessCleanup(dialog);
    }

    inline void FinalizeFailureAcknowledgement(void* dialog)
    {
        if (dialog == nullptr)
            return;

        auto bytes = static_cast<std::uint8_t*>(dialog);
        bytes[SubmitPendingOffset] = 0;
    }
}
