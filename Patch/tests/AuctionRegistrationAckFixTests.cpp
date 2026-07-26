#include "../AuctionRegistrationAckFix.h"

#include <array>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstring>

int main()
{
    assert(AuctionRegistrationAckFix::CanCallNativeCloseAfterCleanup(
        AuctionRegistrationAckFix::DeferredCleanupResult::NotDeferred));
    assert(AuctionRegistrationAckFix::CanCallNativeCloseAfterCleanup(
        AuctionRegistrationAckFix::DeferredCleanupResult::Completed));
    assert(!AuctionRegistrationAckFix::CanCallNativeCloseAfterCleanup(
        AuctionRegistrationAckFix::DeferredCleanupResult::Pending));
    assert(!AuctionRegistrationAckFix::CanCallNativeCloseAfterCleanup(
        AuctionRegistrationAckFix::DeferredCleanupResult::Stale));

    std::array<std::uint8_t, AuctionRegistrationAckFix::DialogSize> dialog{};
    dialog.fill(0xCC);

    const std::array<std::uint8_t, sizeof(std::uint32_t)> selectedBefore{
        0x11,
        0x22,
        0x33,
        0x44,
    };
    void* expectedVtable = reinterpret_cast<void*>(0x11223344);
    void* expectedTemporaryItemSpace = reinterpret_cast<void*>(0x55667788);
    const std::uint32_t expectedBagItemSpaceId = 0x99AABBCC;
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::VtableOffset,
        &expectedVtable,
        sizeof(expectedVtable));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::TemporaryItemSpaceOffset,
        &expectedTemporaryItemSpace,
        sizeof(expectedTemporaryItemSpace));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::BagItemSpaceIdOffset,
        &expectedBagItemSpaceId,
        sizeof(expectedBagItemSpaceId));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        selectedBefore.data(),
        selectedBefore.size());
    dialog[AuctionRegistrationAckFix::SubmitPendingOffset] = 1;

    const auto deferredIdentity =
        AuctionRegistrationAckFix::CaptureDeferredDialogIdentity(dialog.data());
    assert(AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));

    void* replacementTemporaryItemSpace =
        reinterpret_cast<void*>(0x12345678);
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::TemporaryItemSpaceOffset,
        &replacementTemporaryItemSpace,
        sizeof(replacementTemporaryItemSpace));
    assert(!AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::TemporaryItemSpaceOffset,
        &expectedTemporaryItemSpace,
        sizeof(expectedTemporaryItemSpace));

    void* replacementVtable = reinterpret_cast<void*>(0x01010101);
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::VtableOffset,
        &replacementVtable,
        sizeof(replacementVtable));
    assert(!AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::VtableOffset,
        &expectedVtable,
        sizeof(expectedVtable));

    const std::uint32_t replacementBagItemSpaceId = 0x01020304;
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::BagItemSpaceIdOffset,
        &replacementBagItemSpaceId,
        sizeof(replacementBagItemSpaceId));
    assert(!AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::BagItemSpaceIdOffset,
        &expectedBagItemSpaceId,
        sizeof(expectedBagItemSpaceId));

    const std::uint32_t replacementSelectedItem = 0xA0B0C0D0;
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        &replacementSelectedItem,
        sizeof(replacementSelectedItem));
    assert(!AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        selectedBefore.data(),
        selectedBefore.size());

    const std::uint32_t clearedSelectedItem = 0;
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        &clearedSelectedItem,
        sizeof(clearedSelectedItem));
    assert(AuctionRegistrationAckFix::MatchesDeferredDialogIdentity(
        dialog.data(),
        deferredIdentity));
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        selectedBefore.data(),
        selectedBefore.size());

    assert(AuctionRegistrationAckFix::NeedsDeferredSuccessCleanup(dialog.data()));
    assert(!AuctionRegistrationAckFix::IsSuccessCleanupComplete(dialog.data()));
    assert(dialog[AuctionRegistrationAckFix::SubmitPendingOffset] == 1);
    assert(!AuctionRegistrationAckFix::CanRetrySuccessCleanup(dialog.data(), 2, 0));
    assert(!AuctionRegistrationAckFix::CanRetrySuccessCleanup(dialog.data(), 0, 1));
    assert(AuctionRegistrationAckFix::CanRetrySuccessCleanup(dialog.data(), 1, 0));
    assert(AuctionRegistrationAckFix::ShouldBlockCloseForDeferredSuccessCleanup(
        dialog.data(),
        dialog.data()));

    AuctionRegistrationAckFix::FinalizeFailureAcknowledgement(dialog.data());
    assert(dialog[AuctionRegistrationAckFix::SubmitPendingOffset] == 0);
    assert(!AuctionRegistrationAckFix::IsSuccessCleanupComplete(dialog.data()));
    assert(!AuctionRegistrationAckFix::ShouldBlockCloseForDeferredSuccessCleanup(
        dialog.data(),
        nullptr));
    assert(std::memcmp(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        selectedBefore.data(),
        selectedBefore.size()) == 0);

    const std::uint32_t noSelectedItem = 0;
    std::memcpy(
        dialog.data() + AuctionRegistrationAckFix::SelectedItemOffset,
        &noSelectedItem,
        sizeof(noSelectedItem));
    assert(AuctionRegistrationAckFix::IsSuccessCleanupComplete(dialog.data()));
    assert(!AuctionRegistrationAckFix::NeedsDeferredSuccessCleanup(dialog.data()));
    assert(AuctionRegistrationAckFix::ShouldTrackPostAcknowledgementCleanup(
        dialog.data()));
    assert(!AuctionRegistrationAckFix::ShouldTrackPostAcknowledgementCleanup(
        nullptr));
    bool waitForPostAcknowledgementTick = true;
    assert(AuctionRegistrationAckFix::ConsumePostAcknowledgementTick(
        waitForPostAcknowledgementTick));
    assert(!waitForPostAcknowledgementTick);
    assert(!AuctionRegistrationAckFix::ConsumePostAcknowledgementTick(
        waitForPostAcknowledgementTick));
    return 0;
}
