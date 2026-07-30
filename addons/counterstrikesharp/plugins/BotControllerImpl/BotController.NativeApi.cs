// P/Invoke wrapper for BotController.dll (ABI 16), check IsCompatible() before use
// Main-thread only.

using System.Runtime.InteropServices;

namespace BotControllerApi
{
    // Thin static binding over the native exports. No orchestration here.
    public static class BotController
    {
        private const int ExpectedAbiVersion = 16;

        // Sentinel weapon def meaning "any knife"
        public const int KnifeDef = 9001;

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Lock(int slot, int kind, int arg);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_Unlock(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_UnlockAll(int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_IsLocked(int slot, int kind);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetVersion();

        // Imports the native usercmd injection export
        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern long BotController_InjectUsercmd(
            int slot, ulong buttonMask, int durationMs);

        // Imports the native usercmd injection cancellation export
        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CancelUsercmdInjection(
            int slot, long injectionId);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopRecord(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedTickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetRecordedSubtickCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedTicks(
            int slot, [Out] ReplayTick[] ticks, int maxTicks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CopyRecordedSubticks(
            int slot, [Out] SubtickMove[] subs, int maxSubticks);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_LoadReplayExtended(
            int slot, [In] ReplayTick[] ticks, int tickCount,
            [In] SubtickMove[] subs, int subCount,
            [In] ReplayCommandFrame[] commands, int commandCount,
            [In] ReplayMovementExtra[] movementExtras, int movementExtraCount);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_TransferRecordingToReplay(int srcSlot, int dstSlot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StartReplay(int slot, int loop);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetReplayPawn(int slot, ulong pawnPtr);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_StopReplay(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayCursor(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTotal(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetReplayTick(int slot, out ReplayTick tick);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SwitchBotWeapon(int slot, int defIndex);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBotActiveWeaponDef(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetProfile(int slot, out BotProfileData profile);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuyPlan(int slot,
            [MarshalAs(UnmanagedType.LPStr)] string aliases);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SetBuySkip(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearBuyPlan(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_ClearAllBuyPlans();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetBuyPlanItemCount(int slot);

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_CanSendVoice();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_GetVoiceStatus();

        [DllImport("BotController", CallingConvention = CallingConvention.Cdecl)]
        private static extern int BotController_SendVoiceFrame(
            int recipientSlot,
            int senderClient,
            ulong senderXuid,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] byte[] audio,
            int audioBytes,
            int sampleRate,
            float voiceLevel,
            int sequenceBytes,
            int sectionNumber,
            int uncompressedSampleOffset,
            uint numPackets,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 12)] uint[] packetOffsets,
            int packetOffsetCount,
            int tick,
            int audibleMask);

        // Native ABI must match what this wrapper expects.
        public static bool IsCompatible() => BotController_GetVersion() == ExpectedAbiVersion;

        // Native C-ABI version the loaded DLL reports.
        public static int AbiVersion => BotController_GetVersion();

        // Creates an independently cancellable native usercmd injection
        public static long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0)
            => BotController_InjectUsercmd(slot, buttonMask, durationMs);

        // Cancels one native usercmd injection by its token
        public static bool CancelUsercmdInjection(int slot, long injectionId)
            => BotController_CancelUsercmdInjection(slot, injectionId) == 0;

        // ---- locks ----

        // All / Aim / Jump
        public static bool Lock(int slot, LockKind kind)
            => BotController_Lock(slot, (int)kind, 0) == 0;

        // Weapon: arg is the engine slot to lock onto
        public static bool Lock(int slot, LockTarget target)
            => BotController_Lock(slot, (int)LockKind.Weapon, (int)target) == 0;

        public static bool Unlock(int slot, LockKind kind)
            => BotController_Unlock(slot, (int)kind) == 0;

        public static bool UnlockAll(LockKind kind)
            => BotController_UnlockAll((int)kind) == 0;

        // For All/Aim/Jump returns true if locked; for Weapon use GetWeaponLock.
        public static bool IsLocked(int slot, LockKind kind)
            => BotController_IsLocked(slot, (int)kind) != 0;

        // Weapon-only query: returns the locked weapon slot, or None.
        public static LockTarget GetWeaponLock(int slot)
            => (LockTarget)BotController_IsLocked(slot, (int)LockKind.Weapon);

        // ---- recording ----

        public static bool StartRecord(int slot) => BotController_StartRecord(slot) == 0;

        public static bool StopRecord(int slot) => BotController_StopRecord(slot) == 0;

        public static int RecordedTickCount(int slot) => BotController_GetRecordedTickCount(slot);

        // Pull a slot's recorded ticks + subticks out of native memory.
        public static (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
        {
            int nt = BotController_GetRecordedTickCount(slot);
            if (nt <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());

            var ticks = new ReplayTick[nt];
            int gotT = BotController_CopyRecordedTicks(slot, ticks, nt);
            if (gotT <= 0) return (Array.Empty<ReplayTick>(), Array.Empty<SubtickMove>());
            if (gotT != nt) Array.Resize(ref ticks, gotT);

            int ns = BotController_GetRecordedSubtickCount(slot);
            SubtickMove[] subs;
            if (ns <= 0)
                subs = Array.Empty<SubtickMove>();
            else
            {
                subs = new SubtickMove[ns];
                int gotS = BotController_CopyRecordedSubticks(slot, subs, ns);
                if (gotS <= 0) subs = Array.Empty<SubtickMove>();
                else if (gotS != ns) Array.Resize(ref subs, gotS);
            }
            return (ticks, subs);
        }

        // ---- replay ----

        // Load ticks + subticks into a slot's replay buffer (native copies in).
        public static bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => LoadReplayExtended(
                slot, ticks, subs,
                Array.Empty<ReplayCommandFrame>(),
                Array.Empty<ReplayMovementExtra>());

        // Load replay buffers with optional per-tick command and movement data
        public static bool LoadReplayExtended(
            int slot,
            ReplayTick[] ticks,
            SubtickMove[] subs,
            ReplayCommandFrame[] commands,
            ReplayMovementExtra[] movementExtras)
            => ticks is { Length: > 0 }
               && BotController_LoadReplayExtended(
                   slot,
                   ticks,
                   ticks.Length,
                   subs ?? Array.Empty<SubtickMove>(),
                   subs?.Length ?? 0,
                   commands ?? Array.Empty<ReplayCommandFrame>(),
                   commands?.Length ?? 0,
                   movementExtras ?? Array.Empty<ReplayMovementExtra>(),
                   movementExtras?.Length ?? 0) == 0;

        // Move a slot's just-recorded buffers straight into another slot's
        // replay buffer, no managed round-trip.
        public static bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => BotController_TransferRecordingToReplay(srcSlot, dstSlot) == 0;

        public static bool StartReplay(int slot, bool loop = false)
            => BotController_StartReplay(slot, loop ? 1 : 0) == 0;

        // Registers the current native pawn before replay starts.
        public static bool SetReplayPawn(int slot, nint pawn)
            => pawn != 0 &&
               BotController_SetReplayPawn(slot, unchecked((ulong)pawn)) == 0;

        public static bool StopReplay(int slot) => BotController_StopReplay(slot) == 0;

        public static int ReplayCursor(int slot) => BotController_GetReplayCursor(slot);

        public static int ReplayTotal(int slot) => BotController_GetReplayTotal(slot);

        public static bool IsReplaying(int slot) => BotController_GetReplayCursor(slot) >= 0;

        // The tick currently being replayed on this slot, for driving weapon/fire
        // C#-side. Returns false if the slot isn't replaying.
        public static bool TryGetReplayTick(int slot, out ReplayTick tick)
            => BotController_GetReplayTick(slot, out tick) == 0;

        // Switch a bot to the weapon with this def index.
        public static bool SwitchBotWeapon(int slot, int defIndex)
            => BotController_SwitchBotWeapon(slot, defIndex) == 0;

        // Def index of the bot's current active weapon, same normalization as the
        // recorded WeaponDefIndex. <0 if unresolved.
        public static int BotActiveWeaponDef(int slot)
            => BotController_GetBotActiveWeaponDef(slot);

        // ---- profile ----

        // Read the BotProfile of the bot on this slot. Returns false if the slot
        // has no live bot (it must have ticked at least once) or null profile.
        public static bool GetBotProfile(int slot, out BotProfileData profile)
            => BotController_GetProfile(slot, out profile) == 0;

        // ---- buy plans ----

        // Force a bot's per-round buy.
        public static bool SetBuyPlan(int slot, string aliases)
            => BotController_SetBuyPlan(slot, aliases ?? "") == 0;

        // Force a bot to buy nothing each round.
        public static bool SetBuySkip(int slot)
            => BotController_SetBuySkip(slot) == 0;

        // Remove a bot's buy plan (back to vanilla AI buying).
        public static bool ClearBuyPlan(int slot)
            => BotController_ClearBuyPlan(slot) == 0;

        public static bool ClearAllBuyPlans()
            => BotController_ClearAllBuyPlans() == 0;

        // Plan item count: -1 none, 0 skip/empty, >0 alias count.
        public static int BuyPlanItemCount(int slot)
            => BotController_GetBuyPlanItemCount(slot);

        // ---- voice ----

        // Returns true when the native plugin can send voice net messages.
        public static bool CanSendVoice() => BotController_CanSendVoice() != 0;

        // Returns 0 when voice sending is ready, otherwise a negative setup code.
        public static int GetVoiceStatus() => BotController_GetVoiceStatus();

        // Sends one encoded Opus voice frame to a recipient player slot.
        public static int SendVoiceFrame(
            int recipientSlot,
            int senderClient,
            ulong senderXuid,
            byte[] audio,
            int audioBytes,
            int sampleRate,
            float voiceLevel,
            int sequenceBytes,
            int sectionNumber,
            int uncompressedSampleOffset,
            uint numPackets,
            uint[] packetOffsets,
            int packetOffsetCount,
            int tick,
            int audibleMask)
        {
            audio ??= Array.Empty<byte>();
            packetOffsets ??= Array.Empty<uint>();
            if (audioBytes < 0 || audioBytes > audio.Length ||
                packetOffsetCount < 0 || packetOffsetCount > packetOffsets.Length)
                return -2;

            return BotController_SendVoiceFrame(
                recipientSlot,
                senderClient,
                senderXuid,
                audio,
                audioBytes,
                sampleRate,
                voiceLevel,
                sequenceBytes,
                sectionNumber,
                uncompressedSampleOffset,
                numPackets,
                packetOffsets,
                packetOffsetCount,
                tick,
                audibleMask);
        }
    }
}
