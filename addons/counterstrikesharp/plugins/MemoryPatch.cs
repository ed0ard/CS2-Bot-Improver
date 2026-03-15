using System.Runtime.InteropServices;

namespace Common;

public static partial class MemoryPatch
{
    [LibraryImport("libc", EntryPoint = "mprotect")]
    public static partial int MProtect(nint address, int len, int protect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool VirtualProtect(nint address, int dwSize, int newProtect, int* oldProtect);

    public unsafe static bool SetMemAccess(nint addr, int size)
    {
        if (addr == nint.Zero) throw new ArgumentNullException(nameof(addr));
        const int PAGESIZE = 4096;
        nint LALIGN(nint a) => a & ~(PAGESIZE - 1);
        int LDIFF(nint a) => (int)(a % PAGESIZE);

        int* oldProtect = stackalloc int[1];

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? MProtect(LALIGN(addr), size + LDIFF(addr), 7) == 0            // PROT_READ|WRITE|EXEC
            : VirtualProtect(addr, size, 0x40 /* PAGE_EXECUTE_READWRITE */, oldProtect);
    }
}