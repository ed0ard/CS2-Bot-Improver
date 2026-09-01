using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BotAI;

// 1.8.9: rel32 displacement fields inside the Linux patch byte arrays are computed at
// load time from the RESOLVED addresses of both members of a cave pair, instead of
// being hardcoded against one specific libserver.so build. The cave<->site distance
// spans function boundaries and changes on every game update; a sig-matched pair with
// a stale hardcoded displacement jumps into the wrong bytes — the source of both the
// UpdateLookAround segfault (fixed by 1.8.8's atomicity) and the silent bot-behavior
// corruption 1.8.8 could not catch. The constants that remain below are intra-function
// deltas, which survive rebuilds far better than cross-function distances.
internal static class LinuxDisplacementFixups
{
    // Site-relative cave-exit deltas that cannot be derived from the bytes at the site.
    private const int EnterApproachBody_JzExitDelta  = -0x7CF; // near the top of the partner's function
    private const int EnterApproachBody_JmpExitDelta = -0x78D;
    private const int LoopEntry_JzExitDelta          = -0x4F;  // loop head
    private const int LoopEntry_JmpExitDelta         = +0x12;  // 7 bytes past the 11 relocated site bytes

    /// <summary>
    /// Rewrites the displacement fields of <paramref name="patch"/> in place.
    /// Returns false when the fixup cannot be computed (pair member unresolved,
    /// rel32 out of range) — the caller must then treat the entry as unappliable.
    /// Entries without displacement fields return true untouched.
    /// </summary>
    public static bool Apply(string name, List<byte> patch, IReadOnlyDictionary<string, nint> sites, ILogger log)
    {
        switch (name)
        {
            // Partners: replace the original branch with `jmp rel32` into the cave.
            case "Vision_AlwaysEnterApproachBody":
            case "Vision_AlwaysWatchApproachPoints":
            case "Vision_AlwaysWatchApproachPoints_LoopEntry":
            {
                if (!TryGetPair(name, $"{name}_Cave", sites, log, out nint s, out nint c)) return false;
                return WriteRel32(patch, 1, c - (s + 5), name, log);
            }

            case "Vision_AlwaysEnterApproachBody_Cave":
            {
                if (!TryGetPair("Vision_AlwaysEnterApproachBody", name, sites, log, out nint s, out nint c)) return false;
                return WriteRel32(patch, 7,  s + EnterApproachBody_JzExitDelta  - (c + 11), name, log)
                    && WriteRel32(patch, 12, s + EnterApproachBody_JmpExitDelta - (c + 16), name, log);
            }

            case "Vision_AlwaysWatchApproachPoints_Cave":
            {
                if (!TryGetPair("Vision_AlwaysWatchApproachPoints", name, sites, log, out nint s, out nint c)) return false;
                // jz exit: the original `0F 84` branch target read from the still-unpatched
                // site; jmp exit: the fall-through past the original 6-byte instruction.
                int origRel32 = Marshal.ReadInt32(s + 2);
                return WriteRel32(patch, 7,  s + 6 + origRel32 - (c + 11), name, log)
                    && WriteRel32(patch, 12, s + 6             - (c + 16), name, log);
            }

            case "Vision_AlwaysWatchApproachPoints_LoopEntry_Cave":
            {
                if (!TryGetPair("Vision_AlwaysWatchApproachPoints_LoopEntry", name, sites, log, out nint s, out nint c)) return false;
                return WriteRel32(patch, 9,  s + LoopEntry_JzExitDelta  - (c + 13), name, log)
                    && WriteRel32(patch, 28, s + LoopEntry_JmpExitDelta - (c + 32), name, log);
            }

            case "OnBombPlanted_AllBotsLearnSite":
            {
                if (!sites.TryGetValue(name, out nint s)) return false;
                // jz rel32 (6 bytes) -> jmp rel32 (5 bytes): same target, so the
                // displacement grows by exactly the 1 byte the instruction shrank.
                int origRel32 = Marshal.ReadInt32(s + 2);
                return WriteRel32(patch, 1, origRel32 + 1, name, log);
            }

            default:
                return true; // no displacement fields in this entry
        }
    }

    private static bool TryGetPair(string partnerName, string caveName,
        IReadOnlyDictionary<string, nint> sites, ILogger log, out nint s, out nint c)
    {
        c = 0;
        if (!sites.TryGetValue(partnerName, out s) || !sites.TryGetValue(caveName, out c))
        {
            log.LogWarning($"Displacement fixup for pair '{partnerName}'/'{caveName}' unavailable: member unresolved.");
            return false;
        }
        return true;
    }

    private static bool WriteRel32(List<byte> patch, int index, long value, string name, ILogger log)
    {
        if (value < int.MinValue || value > int.MaxValue || index + 4 > patch.Count)
        {
            log.LogError($"'{name}': computed rel32 0x{value:X} out of range at patch[{index}].");
            return false;
        }
        var b = BitConverter.GetBytes((int)value);
        for (int i = 0; i < 4; i++) patch[index + i] = b[i];
        return true;
    }
}
