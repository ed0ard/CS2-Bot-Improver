// Provider-side implementation of IBotControllerApi.

namespace BotControllerApi
{
    public sealed class BotControllerApiImpl : IBotControllerApi
    {
        public int AbiVersion => BotController.AbiVersion;

        // ---- locks ----
        public bool Lock(int slot, LockKind kind) => BotController.Lock(slot, kind);
        public bool Lock(int slot, LockTarget target) => BotController.Lock(slot, target);
        public bool Unlock(int slot, LockKind kind) => BotController.Unlock(slot, kind);
        public bool UnlockAll(LockKind kind) => BotController.UnlockAll(kind);
        public bool IsLocked(int slot, LockKind kind) => BotController.IsLocked(slot, kind);
        public LockTarget GetWeaponLock(int slot) => BotController.GetWeaponLock(slot);

        // ---- recording ----
        public bool StartRecord(int slot) => BotController.StartRecord(slot);
        public bool StopRecord(int slot) => BotController.StopRecord(slot);
        public int RecordedTickCount(int slot) => BotController.RecordedTickCount(slot);
        public (ReplayTick[] ticks, SubtickMove[] subs) GetRecordedMotion(int slot)
            => BotController.GetRecordedMotion(slot);

        // ---- replay ----
        public bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subs)
            => BotController.LoadReplay(slot, ticks, subs);
        public bool TransferRecordingToReplay(int srcSlot, int dstSlot)
            => BotController.TransferRecordingToReplay(srcSlot, dstSlot);
        // Registers the authoritative native pawn pointer for replay.
        public bool SetReplayPawn(int slot, nint pawn) => BotController.SetReplayPawn(slot, pawn);
        public bool StartReplay(int slot, bool loop = false) => BotController.StartReplay(slot, loop);
        public bool StopReplay(int slot) => BotController.StopReplay(slot);
        public int ReplayCursor(int slot) => BotController.ReplayCursor(slot);
        public int ReplayTotal(int slot) => BotController.ReplayTotal(slot);
        public bool IsReplaying(int slot) => BotController.IsReplaying(slot);
        public bool TryGetReplayTick(int slot, out ReplayTick tick)
            => BotController.TryGetReplayTick(slot, out tick);

        // ---- weapons ----
        public bool SwitchBotWeapon(int slot, int defIndex)
            => BotController.SwitchBotWeapon(slot, defIndex);
        public int BotActiveWeaponDef(int slot) => BotController.BotActiveWeaponDef(slot);
        // Creates an independently cancellable native usercmd injection
        public long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0)
            => BotController.InjectUsercmd(slot, buttonMask, durationMs);
        // Cancels one native usercmd injection by its token
        public bool CancelUsercmdInjection(int slot, long injectionId)
            => BotController.CancelUsercmdInjection(slot, injectionId);

        // ---- profile ----
        public bool GetBotProfile(int slot, out BotProfileData profile)
            => BotController.GetBotProfile(slot, out profile);

        // ---- buy plans ----
        public bool SetBuyPlan(int slot, string aliases) => BotController.SetBuyPlan(slot, aliases);
        public bool SetBuySkip(int slot) => BotController.SetBuySkip(slot);
        public bool ClearBuyPlan(int slot) => BotController.ClearBuyPlan(slot);
        public bool ClearAllBuyPlans() => BotController.ClearAllBuyPlans();
        public int BuyPlanItemCount(int slot) => BotController.BuyPlanItemCount(slot);

        // ---- voice ----
        public bool CanSendVoice() => BotController.CanSendVoice();
        public int GetVoiceStatus() => BotController.GetVoiceStatus();
        public int SendVoiceFrame(
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
            => BotController.SendVoiceFrame(
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
