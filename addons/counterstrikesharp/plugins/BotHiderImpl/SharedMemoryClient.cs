using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BotHiderApi;

namespace BotHiderImpl;

// Reads BotHider's shared-memory data region and posts write commands
// src/slot_shm.h
public sealed class SharedMemoryClient : IBotHiderApi, IDisposable
{
    private const string MappingName = "CS2BotHider_Slots";
    private const string PosixMappingPath = "/dev/shm/CS2BotHider_Slots";
    private const uint Magic = 0x44494842; // 'BHID'
    private const uint Version = 1;
    private const int MaxSlots = 64;
    private const int NameLen = 32;
    private const int CmdCount = 64;
    private const int AvatarMaxBytes = 16 * 1024;
    private const int TotalSize = 1_064_960;
    private static readonly byte[] AvatarPngSignature =
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Data region offsets
    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffMaxSlots = 8;
    private const int OffSlotState = 16;
    private const int OffSyntheticSid = 80;
    private const int OffPersonaName = 592;
    // Extra data region
    private const int OffCurrentPing = 5720;  // int32[64]
    private const int OffCrosshair = 5976;  // char[64][64]
    private const int CrosshairLen = 64;
    // Signature/hook status region
    private const int OffSigCount = 10072;  // uint32
    private const int OffSigEntries = 10080;  // SigEntry[8]
    private const int SigNameLen = 32;
    private const int SigEntrySize = 40;  // char[32] + uint64
    private const int MaxSigs = 8;
    // Scoreboard flair region
    private const int OffScoreboardFlair = 10400;  // uint32[64]
    // Base identity and native slot incarnation region
    private const int OffBaseSyntheticSid = 10656;  // uint64[64]
    private const int OffBasePersonaName = 11168;  // char[64][32]
    private const int OffIncarnation = 13216;  // uint64[64]
    // Custom avatar request and native application state
    private const int OffAvatarSequence = 13728;  // uint32[64]
    private const int OffAvatarLength = 13984;  // uint32[64]
    private const int OffAvatarIncarnation = 14240;  // uint64[64]
    private const int OffAvatarApplied = 14752;  // byte[64]
    private const int OffAvatarAppliedSid = 14816;  // uint64[64]
    private const int OffAvatarData = 16384;  // byte[64][16 KiB]

    // Command region offsets
    private const int OffWriteIdx = 2640;
    private const int OffReadIdx = 2644;
    private const int OffCmds = 2648;
    private const int CmdSize = 48;

    // Command opcodes
    private const byte CmdSetSteamId = 1;
    private const byte CmdSetPersona = 2;
    private const byte CmdSetDisguise = 3;
    private const byte CmdSetNameSource = 7;

    // Sentinel slot for global commands
    private const byte SlotAll = 255;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private readonly object _writeLock = new();

    private readonly Action<int, string>? _onVisibleName;

    private readonly Action<int, ulong>? _onVisibleSid;
    private readonly Action<int, uint>? _onScoreboardFlair;
    private readonly Action<int, string>? _onCrosshairCode;
    private readonly string?[] _personaNameOverrides = new string?[MaxSlots];
    private readonly ulong[] _personaNameOverrideIncarnations = new ulong[MaxSlots];
    private readonly uint[] _scoreboardFlairs = new uint[MaxSlots];
    private readonly bool[] _scoreboardFlairAssigned = new bool[MaxSlots];
    private readonly ulong[] _scoreboardFlairIncarnations = new ulong[MaxSlots];

    public SharedMemoryClient(Action<int, string>? onVisibleName = null,
                              Action<int, ulong>? onVisibleSid = null,
                              Action<int, uint>? onScoreboardFlair = null,
                              Action<int, string>? onCrosshairCode = null)
    {
        _onVisibleName = onVisibleName;
        _onVisibleSid = onVisibleSid;
        _onScoreboardFlair = onScoreboardFlair;
        _onCrosshairCode = onCrosshairCode;
    }

    // Try to open the existing mapping. Returns false if BotHider isn't loaded yet
    public bool TryConnect()
    {
        if (_view != null) return true;
        try
        {
            _mmf = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite)
                : MemoryMappedFile.CreateFromFile(PosixMappingPath, FileMode.Open, null,
                    TotalSize, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, TotalSize,
                MemoryMappedFileAccess.ReadWrite);
            if (_view.ReadUInt32(OffMagic) != Magic ||
                _view.ReadUInt32(OffVersion) != Version ||
                _view.ReadUInt32(OffMaxSlots) != MaxSlots)
            {
                Dispose();
                return false;
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (Exception) { Dispose(); return false; }
    }

    private bool Valid(int slot)
    {
        if (_view == null) TryConnect();
        return _view != null && slot >= 0 && slot < MaxSlots;
    }

    // Returns whether the shared memory bridge is connected
    public bool IsConnected()
    {
        if (_view != null) return true;
        return TryConnect();
    }

    // IBotHiderApi: read side

    public bool IsManagedBot(int slot) =>
        Valid(slot) && _view!.ReadByte(OffSlotState + slot) != 0;

    public ulong GetBotSteamId(int slot) =>
        Valid(slot) ? _view!.ReadUInt64(OffSyntheticSid + slot * 8) : 0UL;

    // Returns the native persona SteamID before C# presentation overrides
    public ulong GetBaseBotSteamId(int slot) =>
        Valid(slot) ? _view!.ReadUInt64(OffBaseSyntheticSid + slot * 8) : 0UL;

    // Returns the native lifetime identity for the current managed slot
    public ulong GetSlotIncarnation(int slot) =>
        Valid(slot) ? _view!.ReadUInt64(OffIncarnation + slot * 8) : 0UL;

    public int[] GetManagedSlots()
    {
        if (_view == null) TryConnect();
        if (_view == null) return Array.Empty<int>();
        var list = new List<int>();
        var managed = new bool[MaxSlots];
        for (int s = 0; s < MaxSlots; s++)
        {
            if (_view.ReadByte(OffSlotState + s) == 0) continue;
            managed[s] = true;
            list.Add(s);
        }
        ClearReleasedScoreboardFlairs(managed);
        return list.ToArray();
    }

    public string GetPersonaName(int slot)
    {
        if (!Valid(slot)) return string.Empty;
        ulong incarnation = GetSlotIncarnation(slot);
        if (incarnation != 0 &&
            _personaNameOverrideIncarnations[slot] == incarnation &&
            !string.IsNullOrEmpty(_personaNameOverrides[slot]))
        {
            return _personaNameOverrides[slot]!;
        }
        _personaNameOverrides[slot] = null;
        _personaNameOverrideIncarnations[slot] = incarnation;
        return ReadFixedUtf8(slot, OffPersonaName, NameLen);
    }

    // Returns the native persona name before C# presentation overrides
    public string GetBasePersonaName(int slot)
        => ReadFixedUtf8(slot, OffBasePersonaName, NameLen);

    // Reads one fixed-size NUL-terminated UTF-8 field
    private string ReadFixedUtf8(int slot, int baseOffset, int fieldLength)
    {
        if (!Valid(slot)) return string.Empty;
        var buf = new byte[fieldLength];
        _view!.ReadArray(baseOffset + slot * fieldLength, buf, 0, fieldLength);
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = fieldLength;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    public int GetPing(int slot) =>
        Valid(slot) ? _view!.ReadInt32(OffCurrentPing + slot * 4) : 0;

    public string GetCrosshairCode(int slot)
        => ReadFixedUtf8(slot, OffCrosshair, CrosshairLen);

    // Returns whether native has applied an avatar to the current SteamID
    public bool HasBotAvatar(int slot)
    {
        if (!IsManagedBot(slot) || _view!.ReadByte(OffAvatarApplied + slot) == 0)
            return false;
        ulong appliedSid = _view.ReadUInt64(OffAvatarAppliedSid + slot * sizeof(ulong));
        return appliedSid != 0 && appliedSid == GetBotSteamId(slot);
    }

    // Returns the configured PNG size for the current slot incarnation
    public int GetConfiguredAvatarSize(int slot)
    {
        if (!IsManagedBot(slot) ||
            !TryReadAvatarMetadata(slot, out _, out uint length, out ulong incarnation) ||
            incarnation == 0 || incarnation != GetSlotIncarnation(slot))
        {
            return 0;
        }
        return checked((int)length);
    }

    // Write crosshair code to shared memory, empty or "0" to clear
    public bool SetCrosshairCode(int slot, string code)
    {
        if (!Valid(slot)) return false;
        if (code == "0") code = string.Empty;
        if (!TryEncodeFixedUtf8(code, CrosshairLen, out var buf)) return false;
        _view!.WriteArray(OffCrosshair + slot * CrosshairLen, buf, 0, CrosshairLen);
        _onCrosshairCode?.Invoke(slot, code);
        return true;
    }

    // Returns the assigned or newly randomized scoreboard flair
    public uint GetScoreboardFlair(int slot)
    {
        if (!IsManagedBot(slot)) return 0U;
        ulong incarnation = GetSlotIncarnation(slot);
        if (!_scoreboardFlairAssigned[slot] ||
            _scoreboardFlairIncarnations[slot] != incarnation)
        {
            _scoreboardFlairs[slot] = _view!.ReadUInt32(OffScoreboardFlair + slot * 4);
            _scoreboardFlairAssigned[slot] = true;
            _scoreboardFlairIncarnations[slot] = incarnation;
        }
        return _scoreboardFlairs[slot];
    }

    // Read the resolved hook/signature status table
    public (string Name, ulong Addr)[] GetSignatures()
    {
        if (_view == null) TryConnect();
        if (_view == null) return Array.Empty<(string, ulong)>();
        uint count = _view.ReadUInt32(OffSigCount);
        if (count > MaxSigs) count = MaxSigs;
        var list = new List<(string, ulong)>((int)count);
        var buf = new byte[SigNameLen];
        for (int i = 0; i < count; i++)
        {
            int baseOff = OffSigEntries + i * SigEntrySize;
            _view.ReadArray(baseOff, buf, 0, SigNameLen);
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = SigNameLen;
            string name = Encoding.UTF8.GetString(buf, 0, len);
            ulong addr = _view.ReadUInt64(baseOff + SigNameLen);
            list.Add((name, addr));
        }
        return list.ToArray();
    }

    // IBotHiderApi: write side

    public bool SetBotSteamId(int slot, ulong steamId64)
    {
        if (!Valid(slot)) return false;
        bool ok = PostCommand(CmdSetSteamId, slot, steamId64, null);
        if (ok) _onVisibleSid?.Invoke(slot, steamId64);
        return ok;
    }

    public bool SetPersonaName(int slot, string name)
    {
        if (!Valid(slot)) return false;
        string normalizedName = BuildBoundedVisiblePersonaName(name);
        if (normalizedName.Length == 0) return false;
        bool ok = PostCommand(CmdSetPersona, slot, 0UL, normalizedName);
        if (ok)
        {
            ulong incarnation = GetSlotIncarnation(slot);
            if (incarnation != 0)
            {
                _personaNameOverrides[slot] = normalizedName;
                _personaNameOverrideIncarnations[slot] = incarnation;
            }
            _onVisibleName?.Invoke(slot, normalizedName);
        }
        return ok;
    }

    // Builds a visible name bounded by the shared-memory UTF-8 field
    private static string BuildBoundedVisiblePersonaName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var visibleElements = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(source);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!IsInvisiblePersonaElement(element))
                visibleElements.Add(element);
        }

        int first = 0;
        while (first < visibleElements.Count &&
               IsWhitespacePersonaElement(visibleElements[first]))
        {
            first++;
        }

        int last = visibleElements.Count;
        while (last > first && IsWhitespacePersonaElement(visibleElements[last - 1]))
            last--;

        var boundedElements = new List<string>();
        int utf8Bytes = 0;
        for (int index = first; index < last; index++)
        {
            string element = visibleElements[index];
            int elementBytes = Encoding.UTF8.GetByteCount(element);
            if (utf8Bytes + elementBytes > BotHiderContract.MaxPlayerNameUtf8Bytes)
                break;
            boundedElements.Add(element);
            utf8Bytes += elementBytes;
        }

        while (boundedElements.Count > 0 &&
               IsWhitespacePersonaElement(boundedElements[^1]))
        {
            boundedElements.RemoveAt(boundedElements.Count - 1);
        }
        return string.Concat(boundedElements);
    }

    // Returns whether a text element contains only invisible runes
    private static bool IsInvisiblePersonaElement(string element)
    {
        foreach (Rune rune in element.EnumerateRunes())
        {
            if (!IsInvisiblePersonaRune(rune))
                return false;
        }
        return true;
    }

    // Returns whether a text element contains only whitespace and invisible runes
    private static bool IsWhitespacePersonaElement(string element)
    {
        bool hasWhitespace = false;
        foreach (Rune rune in element.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                hasWhitespace = true;
                continue;
            }
            if (!IsInvisiblePersonaRune(rune))
                return false;
        }
        return hasWhitespace;
    }

    // Returns whether a rune should be removed from a presentation name
    private static bool IsInvisiblePersonaRune(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or
            UnicodeCategory.Surrogate or
            UnicodeCategory.OtherNotAssigned or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    // Overrides the C#-side scoreboard flair for a managed bot
    public bool SetScoreboardFlair(int slot, uint itemDefIndex)
    {
        if (!IsManagedBot(slot) || itemDefIndex > ushort.MaxValue) return false;
        ulong incarnation = GetSlotIncarnation(slot);
        if (incarnation == 0) return false;
        _scoreboardFlairs[slot] = itemDefIndex;
        _scoreboardFlairAssigned[slot] = true;
        _scoreboardFlairIncarnations[slot] = incarnation;
        _onScoreboardFlair?.Invoke(slot, itemDefIndex);
        return true;
    }

    // Global disguise toggle
    public bool SetDisguise(bool enabled)
    {
        if (_view == null) TryConnect();
        if (_view == null) return false;
        return PostCommand(CmdSetDisguise, SlotAll, enabled ? 1UL : 0UL, null);
    }

    // Global display-name source toggle (1=bot_info name, 0=botprofile name)
    public bool SetNameSource(bool useBotInfo)
    {
        if (_view == null) TryConnect();
        if (_view == null) return false;
        return PostCommand(CmdSetNameSource, SlotAll, useBotInfo ? 1UL : 0UL, null);
    }

    private bool PostCommand(byte type, int slot, ulong sid, string? name)
    {
        if (_view == null) return false;
        if (!TryEncodeFixedUtf8(name ?? string.Empty, NameLen, out var nameBuf))
            return false;
        lock (_writeLock)
        {
            uint w = _view.ReadUInt32(OffWriteIdx);
            uint r = _view.ReadUInt32(OffReadIdx);
            if (unchecked(w - r) >= CmdCount)
                return false;
            int baseOff = OffCmds + (int)(w % CmdCount) * CmdSize;

            _view.Write(baseOff + 0, type);
            _view.Write(baseOff + 1, (byte)slot);
            _view.Write(baseOff + 8, sid);
            _view.WriteArray(baseOff + 16, nameBuf, 0, NameLen);

            Interlocked.MemoryBarrier();
            _view.Write(OffWriteIdx, w + 1);
        }
        return true;
    }

    // Reads and queues one validated PNG avatar file
    public bool SetBotAvatar(int slot, string pngPath)
        => TrySetBotAvatar(slot, pngPath, out _);

    // Reads and queues one PNG avatar file with a validation error
    public bool TrySetBotAvatar(int slot, string pngPath, out string error)
    {
        error = string.Empty;
        if (pngPath == "0")
        {
            if (ClearAvatarRequest(slot)) return true;
            error = "shared memory is unavailable";
            return false;
        }
        if (!IsManagedBot(slot))
        {
            error = "slot is not a BotHider-managed bot";
            return false;
        }
        if (string.IsNullOrWhiteSpace(pngPath))
        {
            error = "avatar path is empty";
            return false;
        }

        byte[] bytes;
        try
        {
            string fullPath = Path.GetFullPath(pngPath);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                error = "avatar PNG does not exist";
                return false;
            }
            if (file.Length == 0)
            {
                error = "avatar PNG is empty";
                return false;
            }
            if (file.Length > AvatarMaxBytes)
            {
                error = "avatar PNG must be 16 KiB or smaller";
                return false;
            }
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex)
        {
            error = $"failed to read avatar PNG: {ex.Message}";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "avatar PNG is empty";
            return false;
        }
        if (bytes.Length > AvatarMaxBytes)
        {
            error = "avatar PNG must be 16 KiB or smaller";
            return false;
        }
        if (!bytes.AsSpan().StartsWith(AvatarPngSignature))
        {
            error = "avatar file is not a PNG";
            return false;
        }

        ulong incarnation = GetSlotIncarnation(slot);
        if (incarnation == 0)
        {
            error = "managed slot has no active incarnation";
            return false;
        }
        if (!WriteAvatarRequest(slot, incarnation, bytes))
        {
            error = "shared memory is unavailable";
            return false;
        }
        return true;
    }

    // Queues removal of the custom avatar for one slot
    internal bool ClearAvatarRequest(int slot)
    {
        if (!Valid(slot)) return false;
        return WriteAvatarRequest(slot, 0UL, ReadOnlySpan<byte>.Empty);
    }

    // Writes one avatar request using a per-slot seqlock
    private bool WriteAvatarRequest(int slot, ulong incarnation, ReadOnlySpan<byte> bytes)
    {
        if (!Valid(slot) || bytes.Length > AvatarMaxBytes) return false;
        lock (_writeLock)
        {
            int sequenceOffset = OffAvatarSequence + slot * sizeof(uint);
            uint current = _view!.ReadUInt32(sequenceOffset);
            uint writing = unchecked((current & 1U) == 0 ? current + 1U : current + 2U);
            _view.Write(sequenceOffset, writing);
            Interlocked.MemoryBarrier();

            _view.Write(OffAvatarIncarnation + slot * sizeof(ulong), incarnation);
            _view.Write(OffAvatarLength + slot * sizeof(uint), (uint)bytes.Length);
            byte[] buffer = new byte[AvatarMaxBytes];
            bytes.CopyTo(buffer);
            _view.WriteArray(OffAvatarData + slot * AvatarMaxBytes,
                             buffer, 0, AvatarMaxBytes);

            Interlocked.MemoryBarrier();
            _view.Write(sequenceOffset, unchecked(writing + 1U));
        }
        return true;
    }

    // Reads stable avatar request metadata without copying PNG data
    private bool TryReadAvatarMetadata(int slot, out uint sequence,
                                       out uint length, out ulong incarnation)
    {
        sequence = 0;
        length = 0;
        incarnation = 0;
        if (!Valid(slot)) return false;

        int sequenceOffset = OffAvatarSequence + slot * sizeof(uint);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint before = _view!.ReadUInt32(sequenceOffset);
            if ((before & 1U) != 0) continue;
            Interlocked.MemoryBarrier();
            uint candidateLength = _view.ReadUInt32(
                OffAvatarLength + slot * sizeof(uint));
            ulong candidateIncarnation = _view.ReadUInt64(
                OffAvatarIncarnation + slot * sizeof(ulong));
            Interlocked.MemoryBarrier();
            uint after = _view.ReadUInt32(sequenceOffset);
            if (before != after || (after & 1U) != 0) continue;
            if (candidateLength > AvatarMaxBytes) return false;

            sequence = after;
            length = candidateLength;
            incarnation = candidateIncarnation;
            return true;
        }
        return false;
    }

    // Encodes a fixed-size UTF-8 field without truncating a character
    private static bool TryEncodeFixedUtf8(string value, int fieldLength, out byte[] buffer)
    {
        buffer = new byte[fieldLength];
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount >= fieldLength)
            return false;
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        return true;
    }

    // Drops local flair state when C++ releases a slot
    private void ClearReleasedScoreboardFlairs(bool[] managed)
    {
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            ulong incarnation = managed[slot] ? GetSlotIncarnation(slot) : 0UL;
            if (managed[slot] &&
                _scoreboardFlairIncarnations[slot] == incarnation)
            {
                continue;
            }
            _scoreboardFlairs[slot] = 0U;
            _scoreboardFlairAssigned[slot] = false;
            _scoreboardFlairIncarnations[slot] = incarnation;
        }
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
    }
}
