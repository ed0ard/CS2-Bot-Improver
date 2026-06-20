using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;

namespace BotControllerApi;

public enum LockKind
{
    All = 0,
    Aim = 1,
    Weapon = 2,
    Jump = 3,
}

public enum LockTarget
{
    None = 0,
    Slot1 = 1,
    Slot2 = 2,
    Slot3 = 3,
    Slot4 = 4,
    Slot5 = 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MovementSnapshot
{
    public float OriginX;
    public float OriginY;
    public float OriginZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public uint EntityFlags;
    public byte MoveType;
    public byte Pad0;
    public byte Pad1;
    public byte Pad2;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public float DuckAmount;
    public float DuckSpeed;
    public float LadderNormalX;
    public float LadderNormalY;
    public float LadderNormalZ;
    public byte Ducked;
    public byte Ducking;
    public byte DesiresDuck;
    public byte ActualMoveType;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ReplayTick
{
    public MovementSnapshot Pre;
    public MovementSnapshot Post;
    public int WeaponDefIndex;
    public uint NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SubtickMove
{
    public float When;
    public uint Button;
    public float Pressed;
    public float AnalogForward;
    public float AnalogLeft;
    public float PitchDelta;
    public float YawDelta;
}

public static class BotController
{
    private const int ExpectedAbiVersion = 12;
    public const int KnifeDef = 9001;
    public const uint RecFormatVersionV4 = 4;
    public const int MovementSnapshotByteSize = 92;
    public const int ReplayTickByteSize = 192;
    private const byte RecCompressionBrotli = 2;
    private const float MaxReplayVelocity = 4096.0f;
    private const int MaxReplayCacheEntries = 64;
    private static int MaxReplayBundleCacheEntries = 4;
    private const ulong AttackButtonMask = (1UL << 0) | (1UL << 11) | (1UL << 25);
    private const uint AttackSubtickButtonMask = (1U << 0) | (1U << 11) | (1U << 25);
    private const string LibraryName = "BotController";
    private static readonly byte[] RecMagic =
    [
        (byte)'C', (byte)'S', (byte)'2', (byte)'B',
        (byte)'M', (byte)'R', (byte)'E', (byte)'C'
    ];
    private static bool? _compatible;
    private static string _status = "not_checked";
    private static string _loadStatus = "not_attempted";
    private static IntPtr _nativeHandle;
    private static LockDelegate? _lock;
    private static UnlockDelegate? _unlock;
    private static UnlockAllDelegate? _unlockAll;
    private static IsLockedDelegate? _isLocked;
    private static GetVersionDelegate? _getVersion;
    private static LoadReplayDelegate? _loadReplay;
    private static StartReplayDelegate? _startReplay;
    private static StartReplayAtDelegate? _startReplayAt;
    private static StartReplayUntilDelegate? _startReplayUntil;
    private static StopReplayDelegate? _stopReplay;
    private static SetBotIdleDelegate? _setBotIdle;
    private static GetReplayCursorDelegate? _getReplayCursor;
    private static GetReplayTotalDelegate? _getReplayTotal;
    private static GetReplayTickDelegate? _getReplayTick;
    private static SwitchBotWeaponDelegate? _switchBotWeapon;
    private static GetBotActiveWeaponDefDelegate? _getBotActiveWeaponDef;
    private static SetBuyPlanDelegate? _setBuyPlan;
    private static SetBuySkipDelegate? _setBuySkip;
    private static ClearBuyPlanDelegate? _clearBuyPlan;
    private static ClearAllBuyPlansDelegate? _clearAllBuyPlans;
    private static GetBuyPlanItemCountDelegate? _getBuyPlanItemCount;
    private static GetHookCallCountDelegate? _getHookCallCount;
    private static GetLastIntDelegate? _getLastResolvedSlot;
    private static GetHookCallCountDelegate? _getFinishMoveCallCount;
    private static GetHookCallCountDelegate? _getPlayerRunCommandCallCount;
    private static GetHookCallCountDelegate? _getPhysicsSimulateCallCount;
    private static GetLastIntDelegate? _getLastPhysicsSlot;
    private static GetHookCallCountDelegate? _getReplayCommitCount;
    private static GetSlotResolveCountDelegate? _getSlotResolveCallCount;
    private static GetSlotResolveCountDelegate? _getSlotResolveFailureCount;
    private static GetLastPointerDelegate? _getLastServices;
    private static GetLastPointerDelegate? _getLastPawn;
    private static GetLastHandleDelegate? _getLastControllerHandle;
    private static GetLastHandleDelegate? _getLastOriginalControllerHandle;
    private static GetLastIntDelegate? _getLastControllerIndex;
    private static GetLastIntDelegate? _getLastOriginalControllerIndex;
    private static GetLastIntDelegate? _getLastOwnerSlot;
    private static readonly object ReplayCacheLock = new();
    private static readonly Dictionary<ReplayCacheKey, ReplayFile> ReplayCache = [];
    private static readonly Queue<ReplayCacheKey> ReplayCacheOrder = [];
    private static readonly Dictionary<ReplayBundleCacheKey, ReplayBundle> ReplayBundleCache = [];
    private static readonly Queue<ReplayBundleCacheKey> ReplayBundleCacheOrder = [];
    public static string LastLoadError { get; private set; } = string.Empty;

    public static string Status
    {
        get
        {
            _ = IsCompatible();
            return _status;
        }
    }

    public static bool SupportsBoundedReplay
        => IsCompatible() && _startReplayAt != null && _startReplayUntil != null;

    public static void ResetCompatibility()
    {
        _compatible = null;
        _status = "not_checked";
        ClearReplayCache();
    }

    public static void ClearReplayCache()
    {
        lock (ReplayCacheLock)
        {
            ReplayCache.Clear();
            ReplayCacheOrder.Clear();
            ReplayBundleCache.Clear();
            ReplayBundleCacheOrder.Clear();
        }
    }

    public static void ConfigureReplayBundleCacheLimit(int maxEntries)
    {
        lock (ReplayCacheLock)
        {
            MaxReplayBundleCacheEntries = Math.Max(1, maxEntries);
            TrimReplayBundleCacheLocked();
        }
    }

    public static bool IsCompatible()
    {
        if (_compatible.HasValue)
        {
            return _compatible.Value;
        }

        try
        {
            if (!EnsureLoaded())
            {
                _compatible = false;
                _status = _loadStatus;
                return false;
            }

            var version = _getVersion!();
            _compatible = version == ExpectedAbiVersion;
            _status = _compatible.Value ? $"abi {version}" : $"abi {version}, expected {ExpectedAbiVersion}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _compatible = false;
            _status = $"{ex.GetType().Name}: {_loadStatus}; {ex.Message}";
        }

        return _compatible.Value;
    }

    public static bool Lock(int slot, LockKind kind)
        => Invoke(() => _lock!(slot, (int)kind, 0) == 0);

    public static bool Lock(int slot, LockTarget target)
        => Invoke(() => _lock!(slot, (int)LockKind.Weapon, (int)target) == 0);

    public static bool Unlock(int slot, LockKind kind)
        => Invoke(() => _unlock!(slot, (int)kind) == 0);

    public static bool UnlockAll(LockKind kind)
        => Invoke(() => _unlockAll!((int)kind) == 0);

    public static bool IsLocked(int slot, LockKind kind)
        => Invoke(() => _isLocked!(slot, (int)kind) != 0);

    public static LockTarget GetWeaponLock(int slot)
        => Invoke(() => (LockTarget)_isLocked!(slot, (int)LockKind.Weapon), LockTarget.None);

    public static bool LoadReplay(int slot, ReplayTick[] ticks, SubtickMove[] subticks)
    {
        if (ticks.Length == 0)
        {
            LastLoadError = "replay has no ticks";
            return false;
        }

        var subBuffer = subticks.Length == 0 ? [new SubtickMove()] : subticks;
        var ok = Invoke(() => _loadReplay!(slot, ticks, ticks.Length, subBuffer, subticks.Length) == 0);
        LastLoadError = ok ? string.Empty : "BotController_LoadReplay failed";
        return ok;
    }

    public static bool LoadReplayFromFile(int slot, string path, int startTick = 0, bool suppressAttackInput = false, string recKey = "")
    {
        try
        {
            EnsureNativeLayout();
            var replay = ReadReplayFile(path, startTick, suppressAttackInput, recKey);
            return LoadReplay(slot, replay.Ticks, replay.Subticks);
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    public static bool PrewarmReplayBundle(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                LastLoadError = $"missing replay bundle {fullPath}";
                return false;
            }

            _ = ReadReplayBundleCached(fullPath, info);
            LastLoadError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            return false;
        }
    }

    public static bool StartReplay(int slot, bool loop = false)
        => StartReplayAt(slot, 0, loop);

    public static bool StartReplayAt(int slot, int startTick, bool loop = false)
        => Invoke(() =>
        {
            if (_lock!(slot, (int)LockKind.All, 0) != 0)
            {
                return false;
            }

            var startIndex = Math.Max(0, startTick);
            var ok = startIndex > 0 && _startReplayAt != null
                ? _startReplayAt(slot, loop ? 1 : 0, startIndex) == 0
                : _startReplay!(slot, loop ? 1 : 0) == 0;
            if (!ok)
            {
                _unlock!(slot, (int)LockKind.All);
            }

            return ok;
        });

    public static bool StartReplayUntil(int slot, int startTick, int holdBeforeTick, bool loop = false)
        => Invoke(() =>
        {
            if (_startReplayUntil == null || holdBeforeTick <= startTick)
            {
                return false;
            }

            if (_lock!(slot, (int)LockKind.All, 0) != 0)
            {
                return false;
            }

            var ok = _startReplayUntil(
                slot,
                loop ? 1 : 0,
                Math.Max(0, startTick),
                holdBeforeTick) == 0;
            if (!ok)
            {
                _unlock!(slot, (int)LockKind.All);
            }

            return ok;
        });

    public static bool StopReplay(int slot)
        => Invoke(() =>
        {
            var ok = _stopReplay!(slot) == 0;
            _unlock!(slot, (int)LockKind.All);
            return ok;
        });

    public static bool SetBotIdle(int slot)
        => Invoke(() => _setBotIdle!(slot) == 0);

    public static int GetReplayCursor(int slot)
        => Invoke(() => _getReplayCursor!(slot), -1);

    public static int GetReplayTotal(int slot)
        => Invoke(() => _getReplayTotal!(slot), 0);

    public static bool TryGetReplayTick(int slot, out ReplayTick tick)
    {
        tick = default;
        if (!IsCompatible())
        {
            return false;
        }

        try
        {
            return _getReplayTick!(slot, ref tick) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _compatible = false;
            _status = $"{ex.GetType().Name}: {_loadStatus}; {ex.Message}";
            return false;
        }
    }

    public static bool SwitchBotWeapon(int slot, int defIndex)
        => defIndex >= 0 && Invoke(() => _switchBotWeapon!(slot, defIndex) == 0);

    public static int GetBotActiveWeaponDef(int slot)
        => _getBotActiveWeaponDef == null ? -1 : Invoke(() => _getBotActiveWeaponDef!(slot), -1);

    public static bool SetBuyPlan(int slot, string aliases)
        => Invoke(() => _setBuyPlan!(slot, aliases ?? string.Empty) == 0);

    public static bool SetBuySkip(int slot)
        => Invoke(() => _setBuySkip!(slot) == 0);

    public static bool ClearBuyPlan(int slot)
        => Invoke(() => _clearBuyPlan!(slot) == 0);

    public static bool ClearAllBuyPlans()
        => Invoke(() => _clearAllBuyPlans!() == 0);

    public static int BuyPlanItemCount(int slot)
        => Invoke(() => _getBuyPlanItemCount!(slot), -1);

    public static ulong GetHookCallCount()
        => _getHookCallCount == null ? 0UL : Invoke(() => _getHookCallCount!(), 0UL);

    public static int GetLastResolvedSlot()
        => _getLastResolvedSlot == null ? -1 : Invoke(() => _getLastResolvedSlot!(), -1);

    public static ulong GetFinishMoveCallCount()
        => _getFinishMoveCallCount == null ? 0UL : Invoke(() => _getFinishMoveCallCount!(), 0UL);

    public static ulong GetPlayerRunCommandCallCount()
        => _getPlayerRunCommandCallCount == null ? 0UL : Invoke(() => _getPlayerRunCommandCallCount!(), 0UL);

    public static ulong GetPhysicsSimulateCallCount()
        => _getPhysicsSimulateCallCount == null ? 0UL : Invoke(() => _getPhysicsSimulateCallCount!(), 0UL);

    public static int GetLastPhysicsSlot()
        => _getLastPhysicsSlot == null ? -1 : Invoke(() => _getLastPhysicsSlot!(), -1);

    public static ulong GetReplayCommitCount()
        => _getReplayCommitCount == null ? 0UL : Invoke(() => _getReplayCommitCount!(), 0UL);

    public static ulong GetSlotResolveCallCount()
        => _getSlotResolveCallCount == null ? 0UL : Invoke(() => _getSlotResolveCallCount!(), 0UL);

    public static ulong GetSlotResolveFailureCount()
        => _getSlotResolveFailureCount == null ? 0UL : Invoke(() => _getSlotResolveFailureCount!(), 0UL);

    public static ulong GetLastServices()
        => _getLastServices == null ? 0UL : Invoke(() => _getLastServices!(), 0UL);

    public static ulong GetLastPawn()
        => _getLastPawn == null ? 0UL : Invoke(() => _getLastPawn!(), 0UL);

    public static uint GetLastControllerHandle()
        => _getLastControllerHandle == null ? 0U : Invoke(() => _getLastControllerHandle!(), 0U);

    public static uint GetLastOriginalControllerHandle()
        => _getLastOriginalControllerHandle == null ? 0U : Invoke(() => _getLastOriginalControllerHandle!(), 0U);

    public static int GetLastControllerIndex()
        => _getLastControllerIndex == null ? -1 : Invoke(() => _getLastControllerIndex!(), -1);

    public static int GetLastOriginalControllerIndex()
        => _getLastOriginalControllerIndex == null ? -1 : Invoke(() => _getLastOriginalControllerIndex!(), -1);

    public static int GetLastOwnerSlot()
        => _getLastOwnerSlot == null ? -1 : Invoke(() => _getLastOwnerSlot!(), -1);

    private static bool Invoke(Func<bool> action)
        => Invoke(action, false);

    private static T Invoke<T>(Func<T> action, T fallback)
    {
        if (!IsCompatible())
        {
            return fallback;
        }

        try
        {
            return action();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _compatible = false;
            _status = $"{ex.GetType().Name}: {_loadStatus}; {ex.Message}";
            return fallback;
        }
    }

    private static bool EnsureLoaded()
    {
        if (_nativeHandle != IntPtr.Zero)
        {
            return true;
        }

        foreach (var candidate in CandidateLibraryPaths(typeof(BotController).Assembly))
        {
            if (!File.Exists(candidate))
            {
                _loadStatus = $"missing {candidate}";
                continue;
            }

            try
            {
                _nativeHandle = NativeLibrary.Load(candidate);
                _lock = LoadExport<LockDelegate>("BotController_Lock");
                _unlock = LoadExport<UnlockDelegate>("BotController_Unlock");
                _unlockAll = LoadExport<UnlockAllDelegate>("BotController_UnlockAll");
                _isLocked = LoadExport<IsLockedDelegate>("BotController_IsLocked");
                _getVersion = LoadExport<GetVersionDelegate>("BotController_GetVersion");
                _loadReplay = LoadExport<LoadReplayDelegate>("BotController_LoadReplay");
                _startReplay = LoadExport<StartReplayDelegate>("BotController_StartReplay");
                _startReplayAt = TryLoadExport<StartReplayAtDelegate>("BotController_StartReplayAt");
                _startReplayUntil = TryLoadExport<StartReplayUntilDelegate>("BotController_StartReplayUntil");
                _stopReplay = LoadExport<StopReplayDelegate>("BotController_StopReplay");
                _setBotIdle = LoadExport<SetBotIdleDelegate>("BotController_SetBotIdle");
                _getReplayCursor = LoadExport<GetReplayCursorDelegate>("BotController_GetReplayCursor");
                _getReplayTotal = LoadExport<GetReplayTotalDelegate>("BotController_GetReplayTotal");
                _getReplayTick = LoadExport<GetReplayTickDelegate>("BotController_GetReplayTick");
                _switchBotWeapon = LoadExport<SwitchBotWeaponDelegate>("BotController_SwitchBotWeapon");
                _getBotActiveWeaponDef = TryLoadExport<GetBotActiveWeaponDefDelegate>("BotController_GetBotActiveWeaponDef");
                _setBuyPlan = LoadExport<SetBuyPlanDelegate>("BotController_SetBuyPlan");
                _setBuySkip = LoadExport<SetBuySkipDelegate>("BotController_SetBuySkip");
                _clearBuyPlan = LoadExport<ClearBuyPlanDelegate>("BotController_ClearBuyPlan");
                _clearAllBuyPlans = LoadExport<ClearAllBuyPlansDelegate>("BotController_ClearAllBuyPlans");
                _getBuyPlanItemCount = LoadExport<GetBuyPlanItemCountDelegate>("BotController_GetBuyPlanItemCount");
                _getHookCallCount = TryLoadExport<GetHookCallCountDelegate>("BotController_GetHookCallCount");
                _getLastResolvedSlot = TryLoadExport<GetLastIntDelegate>("BotController_GetLastResolvedSlot");
                _getFinishMoveCallCount = TryLoadExport<GetHookCallCountDelegate>("BotController_GetFinishMoveCallCount");
                _getPlayerRunCommandCallCount = TryLoadExport<GetHookCallCountDelegate>("BotController_GetPlayerRunCommandCallCount");
                _getPhysicsSimulateCallCount = TryLoadExport<GetHookCallCountDelegate>("BotController_GetPhysicsSimulateCallCount");
                _getLastPhysicsSlot = TryLoadExport<GetLastIntDelegate>("BotController_GetLastPhysicsSlot");
                _getReplayCommitCount = TryLoadExport<GetHookCallCountDelegate>("BotController_GetReplayCommitCount");
                _getSlotResolveCallCount = TryLoadExport<GetSlotResolveCountDelegate>("BotController_GetSlotResolveCallCount");
                _getSlotResolveFailureCount = TryLoadExport<GetSlotResolveCountDelegate>("BotController_GetSlotResolveFailureCount");
                _getLastServices = TryLoadExport<GetLastPointerDelegate>("BotController_GetLastServices");
                _getLastPawn = TryLoadExport<GetLastPointerDelegate>("BotController_GetLastPawn");
                _getLastControllerHandle = TryLoadExport<GetLastHandleDelegate>("BotController_GetLastControllerHandle");
                _getLastOriginalControllerHandle = TryLoadExport<GetLastHandleDelegate>("BotController_GetLastOriginalControllerHandle");
                _getLastControllerIndex = TryLoadExport<GetLastIntDelegate>("BotController_GetLastControllerIndex");
                _getLastOriginalControllerIndex = TryLoadExport<GetLastIntDelegate>("BotController_GetLastOriginalControllerIndex");
                _getLastOwnerSlot = TryLoadExport<GetLastIntDelegate>("BotController_GetLastOwnerSlot");
                _loadStatus = $"loaded {candidate}";
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _loadStatus = $"failed {candidate}: {ex.Message}";
                _nativeHandle = IntPtr.Zero;
                ClearExports();
            }
        }

        return false;
    }

    private static T LoadExport<T>(string name)
        where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_nativeHandle, name));

    private static T? TryLoadExport<T>(string name)
        where T : Delegate
    {
        try
        {
            return LoadExport<T>(name);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static void ClearExports()
    {
        _lock = null;
        _unlock = null;
        _unlockAll = null;
        _isLocked = null;
        _getVersion = null;
        _loadReplay = null;
        _startReplay = null;
        _startReplayAt = null;
        _startReplayUntil = null;
        _stopReplay = null;
        _getReplayCursor = null;
        _getReplayTotal = null;
        _getReplayTick = null;
        _switchBotWeapon = null;
        _getBotActiveWeaponDef = null;
        _setBuyPlan = null;
        _setBuySkip = null;
        _clearBuyPlan = null;
        _clearAllBuyPlans = null;
        _getBuyPlanItemCount = null;
        _getHookCallCount = null;
        _getLastResolvedSlot = null;
        _getFinishMoveCallCount = null;
        _getPlayerRunCommandCallCount = null;
        _getPhysicsSimulateCallCount = null;
        _getLastPhysicsSlot = null;
        _getReplayCommitCount = null;
        _getSlotResolveCallCount = null;
        _getSlotResolveFailureCount = null;
        _getLastServices = null;
        _getLastPawn = null;
        _getLastControllerHandle = null;
        _getLastOriginalControllerHandle = null;
        _getLastControllerIndex = null;
        _getLastOriginalControllerIndex = null;
        _getLastOwnerSlot = null;
    }

    private static ReplayFile ReadReplayFile(string path, int startTick, bool suppressAttackInput, string recKey)
        => SliceReplay(ReadReplayFileCached(path, suppressAttackInput, recKey), startTick);

    private static ReplayFile ReadReplayFileCached(string path, bool suppressAttackInput, string recKey)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        var cacheKey = new ReplayCacheKey(
            fullPath,
            recKey,
            suppressAttackInput,
            info.Length,
            info.LastWriteTimeUtc.Ticks);

        lock (ReplayCacheLock)
        {
            if (ReplayCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var bundle = ReadReplayBundleCached(fullPath, info);
        var entry = SelectReplayBundleEntry(bundle, recKey);
        using var entryStream = new MemoryStream(entry.Payload, writable: false);
        using var entryReader = new BinaryReader(entryStream, Encoding.UTF8, leaveOpen: false);
        var replay = ReadReplayRouteV4(entryReader, entry.Key, out var tickRate);

        SanitizeReplayKinematics(replay.Ticks, tickRate);

        if (suppressAttackInput)
        {
            SuppressAttackInput(replay.Ticks, replay.Subticks);
        }

        lock (ReplayCacheLock)
        {
            ReplayCache[cacheKey] = replay;
            ReplayCacheOrder.Enqueue(cacheKey);
            while (ReplayCache.Count > MaxReplayCacheEntries && ReplayCacheOrder.TryDequeue(out var oldKey))
            {
                ReplayCache.Remove(oldKey);
            }
        }

        return replay;
    }

    private static ReplayBundle ReadReplayBundleCached(string fullPath, FileInfo info)
    {
        var cacheKey = new ReplayBundleCacheKey(
            fullPath,
            info.Length,
            info.LastWriteTimeUtc.Ticks);

        lock (ReplayCacheLock)
        {
            if (ReplayBundleCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        using var stream = File.OpenRead(fullPath);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(RecMagic.Length);
        if (!magic.SequenceEqual(RecMagic))
        {
            throw new InvalidDataException("bad .cs2rec magic");
        }

        var version = reader.ReadUInt32();
        if (version != RecFormatVersionV4)
        {
            throw new InvalidDataException($"unsupported .cs2rec version {version}; expected {RecFormatVersionV4}");
        }

        var bundle = ReadReplayBundleV4(stream, reader);
        lock (ReplayCacheLock)
        {
            ReplayBundleCache[cacheKey] = bundle;
            ReplayBundleCacheOrder.Enqueue(cacheKey);
            TrimReplayBundleCacheLocked();
        }

        return bundle;
    }

    private static void TrimReplayBundleCacheLocked()
    {
        while (ReplayBundleCache.Count > MaxReplayBundleCacheEntries && ReplayBundleCacheOrder.TryDequeue(out var oldKey))
        {
            ReplayBundleCache.Remove(oldKey);
        }
    }

    private static ReplayBundle ReadReplayBundleV4(FileStream stream, BinaryReader outerReader)
    {
        var compression = outerReader.ReadByte();
        if (compression != RecCompressionBrotli)
        {
            throw new InvalidDataException($"unsupported .cs2rec v4 compression {compression}");
        }

        using var brotli = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(brotli, Encoding.UTF8, leaveOpen: false);

        var entryCount = CheckedCount(ReadVarUInt32(reader), "entry_count");
        var entries = new Dictionary<string, byte[]>(entryCount, StringComparer.Ordinal);
        var firstKey = string.Empty;
        for (var i = 0; i < entryCount; i++)
        {
            var key = ReadRecString(reader);
            var length = CheckedCount(ReadVarUInt32(reader), "entry_length");
            var payload = reader.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException("truncated .cs2rec v4 entry");
            }

            if (i == 0)
            {
                firstKey = key;
            }
            entries[key] = payload;
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("empty .cs2rec v4 bundle");
        }

        return new ReplayBundle(entries, firstKey);
    }

    private static ReplayBundleEntry SelectReplayBundleEntry(ReplayBundle bundle, string recKey)
    {
        if (string.IsNullOrWhiteSpace(recKey))
        {
            return new ReplayBundleEntry(bundle.FirstKey, bundle.Entries[bundle.FirstKey]);
        }

        if (!bundle.Entries.TryGetValue(recKey, out var payload))
        {
            throw new InvalidDataException($"missing .cs2rec v4 entry '{recKey}'");
        }

        return new ReplayBundleEntry(recKey, payload);
    }

    private static ReplayFile ReadReplayRouteV4(BinaryReader reader, string recKey, out float tickRate)
    {
        tickRate = reader.ReadSingle();
        _ = reader.ReadUInt32();
        _ = reader.ReadByte();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();

        var tickCount = CheckedCount(ReadVarUInt32(reader), "tick_count");
        var subtickCount = CheckedCount(ReadVarUInt32(reader), "subtick_count");
        var snapshotCount = CheckedCount(ReadVarUInt32(reader), "snapshot_count");
        if (snapshotCount != tickCount + 1)
        {
            throw new InvalidDataException($"snapshot count {snapshotCount} != tick count + 1 ({tickCount + 1}) in {recKey}");
        }

        var transformDownsample = CheckedCount(ReadVarUInt32(reader), "transform_downsample");
        var transformSampleCount = CheckedCount(ReadVarUInt32(reader), "transform_sample_count");
        _ = ReadRecString(reader);
        _ = ReadRecString(reader);

        var samples = new OriginSample[transformSampleCount];
        for (var i = 0; i < transformSampleCount; i++)
        {
            samples[i] = new OriginSample(
                CheckedCount(ReadVarUInt32(reader), "transform_sample_index"),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
        }

        var snapshots = new MovementSnapshot[snapshotCount];
        for (var i = 0; i < snapshotCount; i++)
        {
            snapshots[i].Pitch = reader.ReadSingle();
            snapshots[i].Yaw = reader.ReadSingle();
            snapshots[i].Roll = reader.ReadSingle();
        }

        for (var i = 0; i < snapshotCount; i++)
        {
            snapshots[i].VelX = reader.ReadSingle();
            snapshots[i].VelY = reader.ReadSingle();
            snapshots[i].VelZ = reader.ReadSingle();
        }
        ApplyOriginSamples(snapshots, samples, tickRate);
        if (transformDownsample > 1)
        {
            RebuildVelocitiesFromOrigins(snapshots, tickRate);
        }

        var entityFlags = ReadUIntRle(reader, snapshotCount);
        var moveTypes = ReadUIntRle(reader, snapshotCount);
        var buttons = ReadUInt64Rle(reader, snapshotCount);
        var buttons1 = ReadSparseUInt64Rle(reader, snapshotCount);
        var buttons2 = ReadSparseUInt64Rle(reader, snapshotCount);
        var duckAmounts = ReadFloatRle(reader, snapshotCount);
        var duckSpeeds = ReadFloatRle(reader, snapshotCount);
        var ducked = ReadUIntRle(reader, snapshotCount);
        var ducking = ReadUIntRle(reader, snapshotCount);
        var desiresDuck = ReadUIntRle(reader, snapshotCount);
        var actualMoveTypes = ReadSparseUIntOverrides(reader, snapshotCount, moveTypes);
        var ladderNormals = ReadSparseVector3Rle(reader, snapshotCount);
        for (var i = 0; i < snapshotCount; i++)
        {
            snapshots[i].EntityFlags = entityFlags[i];
            snapshots[i].MoveType = CheckedByte(moveTypes[i], "move_type");
            snapshots[i].Buttons = buttons[i];
            snapshots[i].Buttons1 = buttons1[i];
            snapshots[i].Buttons2 = buttons2[i];
            snapshots[i].DuckAmount = duckAmounts[i];
            snapshots[i].DuckSpeed = duckSpeeds[i];
            snapshots[i].LadderNormalX = ladderNormals[i].X;
            snapshots[i].LadderNormalY = ladderNormals[i].Y;
            snapshots[i].LadderNormalZ = ladderNormals[i].Z;
            snapshots[i].Ducked = CheckedByte(ducked[i], "ducked");
            snapshots[i].Ducking = CheckedByte(ducking[i], "ducking");
            snapshots[i].DesiresDuck = CheckedByte(desiresDuck[i], "desires_duck");
            snapshots[i].ActualMoveType = CheckedByte(actualMoveTypes[i], "actual_move_type");
        }

        var weaponDefs = ReadIntRle(reader, tickCount);
        var subtickCounts = ReadUIntRle(reader, tickCount);

        var ticks = new ReplayTick[tickCount];
        long expectedSubticks = 0;
        for (var i = 0; i < tickCount; i++)
        {
            ticks[i] = new ReplayTick
            {
                Pre = snapshots[i],
                Post = snapshots[i + 1],
                WeaponDefIndex = ToNativeWeaponDefIndex(weaponDefs[i]),
                NumSubtick = subtickCounts[i]
            };
            expectedSubticks += ticks[i].NumSubtick;
        }

        if (expectedSubticks != subtickCount)
        {
            throw new InvalidDataException($"tick subtick sum {expectedSubticks} != header subtick count {subtickCount} in {recKey}");
        }

        var subticks = new SubtickMove[subtickCount];
        for (var i = 0; i < subtickCount; i++)
        {
            subticks[i] = ReadCompactSubtick(reader);
        }

        return new ReplayFile(ticks, subticks);
    }

    private static ReplayFile SliceReplay(ReplayFile replay, int startTick)
    {
        var ticks = replay.Ticks;
        var subticks = replay.Subticks;
        if (startTick <= 0)
        {
            return replay;
        }

        if (startTick >= ticks.Length)
        {
            throw new InvalidDataException($"start tick {startTick} is outside replay tick count {ticks.Length}");
        }

        var skippedSubticks = 0;
        for (var i = 0; i < startTick; i++)
        {
            skippedSubticks += CheckedCount(ticks[i].NumSubtick, "tick subtick count");
        }

        var replaySubticks = 0;
        for (var i = startTick; i < ticks.Length; i++)
        {
            replaySubticks += CheckedCount(ticks[i].NumSubtick, "tick subtick count");
        }

        return new ReplayFile(
            ticks[startTick..],
            replaySubticks == 0 ? [] : subticks[skippedSubticks..(skippedSubticks + replaySubticks)]);
    }

    private static uint ReadVarUInt32(BinaryReader reader)
    {
        uint value = 0;
        var shift = 0;
        while (shift <= 28)
        {
            var b = reader.ReadByte();
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
        }

        throw new InvalidDataException("varuint is too large");
    }

    private static int ReadVarInt32(BinaryReader reader)
    {
        var value = ReadVarUInt32(reader);
        return (int)((value >> 1) ^ (uint)-(int)(value & 1));
    }

    private static int[] ReadIntRle(BinaryReader reader, int expectedCount)
    {
        var values = new int[expectedCount];
        var index = 0;
        while (index < expectedCount)
        {
            var value = ReadVarInt32(reader);
            var run = CheckedCount(ReadVarUInt32(reader), "rle run");
            if (run <= 0 || index + run > expectedCount)
            {
                throw new InvalidDataException($"invalid int rle run {run} at {index}/{expectedCount}");
            }

            Array.Fill(values, value, index, run);
            index += run;
        }
        return values;
    }

    private static uint[] ReadUIntRle(BinaryReader reader, int expectedCount)
    {
        var values = new uint[expectedCount];
        var index = 0;
        while (index < expectedCount)
        {
            var value = ReadVarUInt32(reader);
            var run = CheckedCount(ReadVarUInt32(reader), "rle run");
            if (run <= 0 || index + run > expectedCount)
            {
                throw new InvalidDataException($"invalid uint rle run {run} at {index}/{expectedCount}");
            }

            Array.Fill(values, value, index, run);
            index += run;
        }
        return values;
    }

    private static ulong[] ReadUInt64Rle(BinaryReader reader, int expectedCount)
    {
        var values = new ulong[expectedCount];
        var index = 0;
        while (index < expectedCount)
        {
            var value = reader.ReadUInt64();
            var run = CheckedCount(ReadVarUInt32(reader), "u64 rle run");
            if (run <= 0 || index + run > expectedCount)
            {
                throw new InvalidDataException($"invalid u64 rle run {run} at {index}/{expectedCount}");
            }

            Array.Fill(values, value, index, run);
            index += run;
        }
        return values;
    }

    private static uint[] ReadSparseUIntOverrides(BinaryReader reader, int expectedCount, uint[] defaults)
    {
        if (defaults.Length != expectedCount)
        {
            throw new InvalidDataException($"uint override defaults have {defaults.Length}/{expectedCount} values");
        }

        var values = (uint[])defaults.Clone();
        var runCount = CheckedCount(ReadVarUInt32(reader), "sparse uint override run count");
        for (var i = 0; i < runCount; i++)
        {
            var start = CheckedCount(ReadVarUInt32(reader), "sparse uint override start");
            var run = CheckedCount(ReadVarUInt32(reader), "sparse uint override run");
            var value = ReadVarUInt32(reader);
            if (run <= 0 || start < 0 || start + run > expectedCount)
            {
                throw new InvalidDataException($"invalid sparse uint override run {run} at {start}/{expectedCount}");
            }

            Array.Fill(values, value, start, run);
        }

        return values;
    }

    private static ulong[] ReadSparseUInt64Rle(BinaryReader reader, int expectedCount)
    {
        var values = new ulong[expectedCount];
        var runCount = CheckedCount(ReadVarUInt32(reader), "sparse u64 rle run count");
        for (var i = 0; i < runCount; i++)
        {
            var start = CheckedCount(ReadVarUInt32(reader), "sparse u64 rle start");
            var run = CheckedCount(ReadVarUInt32(reader), "sparse u64 rle run");
            var value = reader.ReadUInt64();
            if (run <= 0 || start < 0 || start + run > expectedCount)
            {
                throw new InvalidDataException($"invalid sparse u64 rle run {run} at {start}/{expectedCount}");
            }

            Array.Fill(values, value, start, run);
        }

        return values;
    }

    private static float[] ReadFloatRle(BinaryReader reader, int expectedCount)
    {
        var values = new float[expectedCount];
        var index = 0;
        var runCount = CheckedCount(ReadVarUInt32(reader), "float rle run count");
        for (var i = 0; i < runCount; i++)
        {
            var value = reader.ReadSingle();
            var run = CheckedCount(ReadVarUInt32(reader), "float rle run");
            if (run <= 0 || index + run > expectedCount)
            {
                throw new InvalidDataException($"invalid float rle run {run} at {index}/{expectedCount}");
            }

            Array.Fill(values, value, index, run);
            index += run;
        }

        if (index != expectedCount)
        {
            throw new InvalidDataException($"float rle decoded {index}/{expectedCount} values");
        }

        return values;
    }

    private static Float3[] ReadSparseVector3Rle(BinaryReader reader, int expectedCount)
    {
        var values = new Float3[expectedCount];
        var runCount = CheckedCount(ReadVarUInt32(reader), "sparse vec3 rle run count");
        for (var i = 0; i < runCount; i++)
        {
            var start = CheckedCount(ReadVarUInt32(reader), "sparse vec3 rle start");
            var run = CheckedCount(ReadVarUInt32(reader), "sparse vec3 rle run");
            var value = new Float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (run <= 0 || start < 0 || start + run > expectedCount)
            {
                throw new InvalidDataException($"invalid sparse vec3 rle run {run} at {start}/{expectedCount}");
            }

            Array.Fill(values, value, start, run);
        }

        return values;
    }

    private static void ApplyOriginSamples(MovementSnapshot[] snapshots, OriginSample[] samples, float tickRate)
    {
        if (snapshots.Length == 0)
        {
            return;
        }

        if (samples.Length == 0)
        {
            throw new InvalidDataException("v4 route has no transform samples");
        }

        Array.Sort(samples, static (left, right) => left.Index.CompareTo(right.Index));
        for (var i = 0; i < samples.Length - 1; i++)
        {
            FillOriginSegment(snapshots, samples[i], samples[i + 1], tickRate);
        }

        var first = samples[0];
        for (var i = 0; i < Math.Clamp(first.Index, 0, snapshots.Length); i++)
        {
            SetOrigin(ref snapshots[i], first.X, first.Y, first.Z);
        }

        var last = samples[^1];
        for (var i = Math.Clamp(last.Index, 0, snapshots.Length - 1); i < snapshots.Length; i++)
        {
            SetOrigin(ref snapshots[i], last.X, last.Y, last.Z);
        }
    }

    private static void FillOriginSegment(MovementSnapshot[] snapshots, OriginSample left, OriginSample right, float tickRate)
    {
        var start = Math.Clamp(left.Index, 0, snapshots.Length - 1);
        var end = Math.Clamp(right.Index, 0, snapshots.Length - 1);
        if (end <= start)
        {
            SetOrigin(ref snapshots[start], left.X, left.Y, left.Z);
            return;
        }

        if (!CanUseVelocityForOriginSegment(snapshots, start, end, tickRate))
        {
            FillOriginSegmentLinear(snapshots, left, right, start, end);
            return;
        }

        var rawX = left.X;
        var rawY = left.Y;
        var rawZ = left.Z;
        for (var i = start; i < end; i++)
        {
            AddVelocityStep(snapshots, i, tickRate, ref rawX, ref rawY, ref rawZ);
        }

        var errorX = right.X - rawX;
        var errorY = right.Y - rawY;
        var errorZ = right.Z - rawZ;
        rawX = left.X;
        rawY = left.Y;
        rawZ = left.Z;
        var span = end - start;
        for (var i = start; i <= end; i++)
        {
            if (i > start)
            {
                AddVelocityStep(snapshots, i - 1, tickRate, ref rawX, ref rawY, ref rawZ);
            }

            var t = (float)(i - start) / span;
            SetOrigin(
                ref snapshots[i],
                rawX + (errorX * t),
                rawY + (errorY * t),
                rawZ + (errorZ * t));
        }
    }

    private static bool CanUseVelocityForOriginSegment(MovementSnapshot[] snapshots, int start, int end, float tickRate)
    {
        if (!float.IsFinite(tickRate) || tickRate <= 0.0f)
        {
            return false;
        }

        for (var i = start; i <= end; i++)
        {
            if (!IsPlausibleVelocity(snapshots[i].VelX, snapshots[i].VelY, snapshots[i].VelZ))
            {
                return false;
            }
        }

        return true;
    }

    private static void FillOriginSegmentLinear(
        MovementSnapshot[] snapshots,
        OriginSample left,
        OriginSample right,
        int start,
        int end)
    {
        var span = end - start;
        for (var i = start; i <= end; i++)
        {
            var t = span > 0 ? Math.Clamp((float)(i - start) / span, 0.0f, 1.0f) : 0.0f;
            SetOrigin(
                ref snapshots[i],
                Lerp(left.X, right.X, t),
                Lerp(left.Y, right.Y, t),
                Lerp(left.Z, right.Z, t));
        }
    }

    private static void AddVelocityStep(MovementSnapshot[] snapshots, int index, float tickRate, ref float x, ref float y, ref float z)
    {
        var next = Math.Min(index + 1, snapshots.Length - 1);
        x += ((snapshots[index].VelX + snapshots[next].VelX) * 0.5f) / tickRate;
        y += ((snapshots[index].VelY + snapshots[next].VelY) * 0.5f) / tickRate;
        z += ((snapshots[index].VelZ + snapshots[next].VelZ) * 0.5f) / tickRate;
    }

    private static void SetOrigin(ref MovementSnapshot snapshot, float x, float y, float z)
    {
        snapshot.OriginX = x;
        snapshot.OriginY = y;
        snapshot.OriginZ = z;
    }

    private static float Lerp(float left, float right, float t)
    {
        return left + ((right - left) * t);
    }

    private static void RebuildVelocitiesFromOrigins(MovementSnapshot[] snapshots, float tickRate)
    {
        if (snapshots.Length == 0 || !float.IsFinite(tickRate) || tickRate <= 0.0f)
        {
            return;
        }

        for (var i = 0; i < snapshots.Length; i++)
        {
            var from = i + 1 < snapshots.Length
                ? snapshots[i]
                : i > 0
                    ? snapshots[i - 1]
                    : snapshots[i];
            var to = i + 1 < snapshots.Length ? snapshots[i + 1] : snapshots[i];
            var velX = (to.OriginX - from.OriginX) * tickRate;
            var velY = (to.OriginY - from.OriginY) * tickRate;
            var velZ = (to.OriginZ - from.OriginZ) * tickRate;

            if (!IsPlausibleVelocity(velX, velY, velZ))
            {
                velX = 0.0f;
                velY = 0.0f;
                velZ = 0.0f;
            }

            snapshots[i].VelX = velX;
            snapshots[i].VelY = velY;
            snapshots[i].VelZ = velZ;
        }
    }

    private static SubtickMove ReadCompactSubtick(BinaryReader reader)
    {
        var optionalFlags = reader.ReadByte();
        var subtick = new SubtickMove
        {
            When = reader.ReadSingle(),
            Button = reader.ReadUInt32(),
            Pressed = 0.0f,
            AnalogForward = 0.0f,
            AnalogLeft = 0.0f,
            PitchDelta = 0.0f,
            YawDelta = 0.0f
        };

        if ((optionalFlags & (1 << 0)) != 0)
        {
            subtick.Pressed = reader.ReadSingle();
        }
        if ((optionalFlags & (1 << 1)) != 0)
        {
            subtick.AnalogForward = reader.ReadSingle();
        }
        if ((optionalFlags & (1 << 2)) != 0)
        {
            subtick.AnalogLeft = reader.ReadSingle();
        }
        if ((optionalFlags & (1 << 3)) != 0)
        {
            subtick.PitchDelta = reader.ReadSingle();
        }
        if ((optionalFlags & (1 << 4)) != 0)
        {
            subtick.YawDelta = reader.ReadSingle();
        }
        if ((optionalFlags & 0xE0) != 0)
        {
            throw new InvalidDataException($"unsupported compact subtick flags 0x{optionalFlags:X2}");
        }

        return subtick;
    }

    private static void SanitizeReplayKinematics(ReplayTick[] ticks, float tickRate)
    {
        if (ticks.Length == 0 || !float.IsFinite(tickRate) || tickRate <= 0.0f)
        {
            return;
        }

        for (var i = 0; i < ticks.Length; i++)
        {
            var tick = ticks[i];
            SanitizeVelocity(ref tick.Pre, tick.Post, tickRate);

            var nextPost = i + 1 < ticks.Length ? ticks[i + 1].Post : tick.Post;
            SanitizeVelocity(ref tick.Post, nextPost, tickRate);
            ticks[i] = tick;
        }
    }

    private static void SanitizeVelocity(ref MovementSnapshot snapshot, MovementSnapshot nextSnapshot, float tickRate)
    {
        if (IsPlausibleVelocity(snapshot.VelX, snapshot.VelY, snapshot.VelZ))
        {
            return;
        }

        var velX = (nextSnapshot.OriginX - snapshot.OriginX) * tickRate;
        var velY = (nextSnapshot.OriginY - snapshot.OriginY) * tickRate;
        var velZ = (nextSnapshot.OriginZ - snapshot.OriginZ) * tickRate;
        if (IsPlausibleVelocity(velX, velY, velZ))
        {
            snapshot.VelX = velX;
            snapshot.VelY = velY;
            snapshot.VelZ = velZ;
            return;
        }

        snapshot.VelX = 0.0f;
        snapshot.VelY = 0.0f;
        snapshot.VelZ = 0.0f;
    }

    private static bool IsPlausibleVelocity(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return false;
        }

        return (x * x) + (y * y) + (z * z) <= MaxReplayVelocity * MaxReplayVelocity;
    }

    private static void SuppressAttackInput(ReplayTick[] ticks, SubtickMove[] subticks)
    {
        var subtickIndex = 0;
        for (var i = 0; i < ticks.Length; i++)
        {
            var shouldSuppressAttack = !IsThrowableUtilityWeaponDef(ticks[i].WeaponDefIndex);
            var tick = ticks[i];
            if (shouldSuppressAttack)
            {
                SuppressAttackInput(ref tick.Pre);
                SuppressAttackInput(ref tick.Post);
            }
            ticks[i] = tick;

            var tickSubticks = CheckedCount(tick.NumSubtick, "tick subtick count");
            for (var sub = 0; sub < tickSubticks && subtickIndex < subticks.Length; sub++, subtickIndex++)
            {
                if (!shouldSuppressAttack)
                {
                    continue;
                }

                var subtick = subticks[subtickIndex];
                subtick.Button &= ~AttackSubtickButtonMask;
                if (subtick.Button == 0)
                {
                    subtick.Pressed = 0.0f;
                }
                subticks[subtickIndex] = subtick;
            }
        }
    }

    private static int ToNativeWeaponDefIndex(int defIndex)
    {
        if (defIndex == 42 || defIndex == 59 || defIndex is >= 500 and < 600 || defIndex == KnifeDef)
        {
            return KnifeDef;
        }

        return defIndex;
    }

    public static bool IsThrowableUtilityWeaponDef(int weaponDefIndex)
        => weaponDefIndex is 43 or 44 or 45 or 46 or 47 or 48;

    private static void SuppressAttackInput(ref MovementSnapshot snapshot)
    {
        snapshot.Buttons &= ~AttackButtonMask;
        snapshot.Buttons1 &= ~AttackButtonMask;
        snapshot.Buttons2 &= ~AttackButtonMask;
    }

    private static int CheckedCount(uint value, string fieldName)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"{fieldName} too large: {value}");
        }
        return (int)value;
    }

    private static byte CheckedByte(uint value, string fieldName)
    {
        if (value > byte.MaxValue)
        {
            throw new InvalidDataException($"{fieldName} too large for byte: {value}");
        }
        return (byte)value;
    }

    private static string ReadRecString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
        {
            throw new EndOfStreamException("truncated string in .cs2rec");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void EnsureNativeLayout()
    {
        var snapshotSize = Marshal.SizeOf<MovementSnapshot>();
        if (snapshotSize != MovementSnapshotByteSize)
        {
            throw new InvalidOperationException($"MovementSnapshot layout is {snapshotSize}, expected {MovementSnapshotByteSize}");
        }

        var tickSize = Marshal.SizeOf<ReplayTick>();
        if (tickSize != ReplayTickByteSize)
        {
            throw new InvalidOperationException($"ReplayTick layout is {tickSize}, expected {ReplayTickByteSize}");
        }
    }

    private static IEnumerable<string> CandidateLibraryPaths(Assembly assembly)
    {
        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "BotController.dll"
            : "BotController.so";
        var platformDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win64"
            : "linuxsteamrt64";

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "BotController", "bin", platformDir, fileName));
            candidates.Add(Path.Combine(assemblyDir, fileName));
        }

        var appBase = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(appBase))
        {
            candidates.Add(Path.Combine(appBase, "..", "..", "..", "..", "..", "BotController", "bin", platformDir, fileName));
        }

        var cwd = Directory.GetCurrentDirectory();
        if (!string.IsNullOrEmpty(cwd))
        {
            candidates.Add(Path.Combine(cwd, "csgo", "addons", "BotController", "bin", platformDir, fileName));
            candidates.Add(Path.Combine(cwd, "..", "..", "csgo", "addons", "BotController", "bin", platformDir, fileName));
            candidates.Add(Path.Combine(cwd, "..", "..", "..", "csgo", "addons", "BotController", "bin", platformDir, fileName));
            candidates.Add(Path.Combine(cwd, "addons", "BotController", "bin", platformDir, fileName));
            candidates.Add(Path.Combine(cwd, "BotController", "bin", platformDir, fileName));
        }

        foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            yield return candidate;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LockDelegate(int slot, int kind, int arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnlockDelegate(int slot, int kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnlockAllDelegate(int kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IsLockedDelegate(int slot, int kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LoadReplayDelegate(
        int slot,
        [In] ReplayTick[] ticks,
        int tickCount,
        [In] SubtickMove[] subticks,
        int subCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartReplayDelegate(int slot, int loop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartReplayAtDelegate(int slot, int loop, int startIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartReplayUntilDelegate(int slot, int loop, int startIndex, int holdBeforeIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StopReplayDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetBotIdleDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetReplayCursorDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetReplayTotalDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetReplayTickDelegate(int slot, ref ReplayTick tick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SwitchBotWeaponDelegate(int slot, int defIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBotActiveWeaponDefDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetBuyPlanDelegate(int slot, [MarshalAs(UnmanagedType.LPStr)] string aliases);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetBuySkipDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClearBuyPlanDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClearAllBuyPlansDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBuyPlanItemCountDelegate(int slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong GetHookCallCountDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLastIntDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong GetSlotResolveCountDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong GetLastPointerDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetLastHandleDelegate();

    private readonly record struct ReplayFile(ReplayTick[] Ticks, SubtickMove[] Subticks);
    private readonly record struct ReplayCacheKey(
        string Path,
        string RecKey,
        bool SuppressAttackInput,
        long Length,
        long LastWriteUtcTicks);
    private readonly record struct ReplayBundleCacheKey(
        string Path,
        long Length,
        long LastWriteUtcTicks);
    private readonly record struct ReplayBundle(Dictionary<string, byte[]> Entries, string FirstKey);
    private readonly record struct ReplayBundleEntry(string Key, byte[] Payload);
    private readonly record struct OriginSample(int Index, float X, float Y, float Z);
    private readonly record struct Float3(float X, float Y, float Z);
}
