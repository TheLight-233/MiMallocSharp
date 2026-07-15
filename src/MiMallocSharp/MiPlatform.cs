// Platform abstraction layer for mimalloc C# port.
// Cross-platform: Windows + Linux + macOS + Unity (IL2CPP / Mono).
//
// ── Memory allocation ──────────────────────────────────────────────────────
//   .NET 6+ :  NativeMemory.AlignedAlloc  (zero overhead, cache-line aligned)
//   older   :  Marshal.AllocHGlobal + manual alignment header
//
// ── Platform detection ─────────────────────────────────────────────────────
//   NO #if _WINDOWS / _UNIX  — those are NOT valid C# preprocessor symbols.
//   We use RuntimeInformation.IsOSPlatform() at runtime for everything
//   that is truly platform-specific (P/Invoke into kernel32 / libc).
//
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mimalloc
{
    internal static unsafe class MiPlatform
    {
        // ===================================================================
        // Platform detection (evaluated once at static init, then cached)
        // ===================================================================
        private static readonly bool _isWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool _isLinux =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private static readonly bool _isMacOS =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // ===================================================================
        // P/Invoke declarations
        //   Windows  → kernel32.dll
        //   Unix     → libc  (Linux: libc.so.6 / macOS: libSystem.dylib)
        //
        // IMPORTANT: DllImport only kicks in when the method is actually called.
        // On Windows the Unix symbols are never resolved and vice-versa,
        // so having both sets declared in one class is perfectly safe.
        // ===================================================================

        // --- Windows kernel32 --------------------------------------------
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualProtect(
            void* lpAddress, nuint dwSize,
            uint  flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualFree(
            void* lpAddress, nuint dwSize, uint dwFreeType);

        [DllImport("kernel32")]
        private static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_INFO
        {
            public uint   dwOemId;
            public uint   dwPageSize;
            public void*  lpMinimumApplicationAddress;
            public void*  lpMaximumApplicationAddress;
            public nuint  dwActiveProcessorMask;
            public uint   dwNumberOfProcessors;
            public uint   dwProcessorType;
            public uint   dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        private const uint PAGE_NOACCESS  = 0x01;
        private const uint PAGE_READWRITE = 0x04;
        private const uint MEM_DECOMMIT   = 0x4000;

        // --- Unix libc ---------------------------------------------------
        // "libc" resolves to libc.so.6 on Linux and libSystem.dylib on macOS
        // via .NET's built-in DLL name mapping.  No separate entries needed.
        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(void* addr, nuint len, int prot);

        [DllImport("libc", SetLastError = true)]
        private static extern int madvise(void* addr, nuint length, int advice);

        private const int PROT_NONE     = 0x00;
        private const int PROT_READ     = 0x01;
        private const int PROT_WRITE    = 0x02;
        private const int MADV_DONTNEED = 4;
        private const int MADV_FREE     = 8; // Linux ≥ 4.5; macOS also supports it

        // ===================================================================
        // Aligned allocation
        // ===================================================================

        /// <summary>
        /// Allocate <paramref name="size"/> bytes with at least
        /// <paramref name="alignment"/> alignment (must be power-of-two).
        /// Returns null on OOM.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AlignedAlloc(nuint size, nuint alignment)
        {
            if (size == 0) return null;
            if (alignment < (nuint)sizeof(void*))
                alignment = (nuint)sizeof(void*);

#if NET6_0_OR_GREATER
            try   { return NativeMemory.AlignedAlloc(size, alignment); }
            catch { return null; }
#else
            // netstandard2.1 / older Unity Mono:
            // Over-allocate, then align manually and stash the raw pointer
            // 8 bytes before the returned aligned address.
            nuint extra = alignment + (nuint)sizeof(void*);
            nuint total = checked(size + extra);
            IntPtr raw  = Marshal.AllocHGlobal((IntPtr)(long)total);
            if (raw == IntPtr.Zero) return null;
            nuint addr    = (nuint)(ulong)raw + (nuint)sizeof(void*);
            nuint mask    = alignment - 1;
            nuint aligned = (addr + mask) & ~mask;
            *((IntPtr*)((byte*)aligned - sizeof(IntPtr))) = raw;
            return (void*)aligned;
#endif
        }

        /// <summary>Free memory from <see cref="AlignedAlloc"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AlignedFree(void* p)
        {
            if (p == null) return;
#if NET6_0_OR_GREATER
            NativeMemory.AlignedFree(p);
#else
            IntPtr raw = *((IntPtr*)((byte*)p - sizeof(IntPtr)));
            Marshal.FreeHGlobal(raw);
#endif
        }

        /// <summary>Allocate and zero-initialise with alignment.</summary>
        public static void* AlignedZalloc(nuint size, nuint alignment)
        {
            void* p = AlignedAlloc(size, alignment);
            if (p != null) MiLibc.mi_memzero(p, size);
            return p;
        }

        // ===================================================================
        // Unaligned alloc (for small metadata structures)
        // ===================================================================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Alloc(nuint size)
        {
            if (size == 0) return null;
#if NET6_0_OR_GREATER
            try   { return NativeMemory.Alloc(size); }
            catch { return null; }
#else
            IntPtr p = Marshal.AllocHGlobal((IntPtr)(long)size);
            return p == IntPtr.Zero ? null : (void*)p;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Free(void* p)
        {
            if (p == null) return;
#if NET6_0_OR_GREATER
            NativeMemory.Free(p);
#else
            Marshal.FreeHGlobal((IntPtr)p);
#endif
        }

        public static void* Zalloc(nuint size)
        {
            void* p = Alloc(size);
            if (p != null) MiLibc.mi_memzero(p, size);
            return p;
        }

        // ===================================================================
        // OS page size & allocation granularity
        // Queried once via P/Invoke; cached thereafter.
        //   Windows  : 4096 / 65536
        //   Linux    : 4096 / 4096
        //   macOS    : 4096 / 4096
        // ===================================================================
        private static nuint _pageSize;
        private static nuint _allocGranularity;

        private static void InitPageInfo()
        {
            if (_pageSize != 0) return;
            if (_isWindows)
            {
                GetSystemInfo(out SYSTEM_INFO si);
                _pageSize         = si.dwPageSize;
                _allocGranularity = si.dwAllocationGranularity;
            }
            else
            {
                // Linux / macOS: PAGE_SIZE is always 4096 on x86-64 and ARM64.
                // A fully portable solution would P/Invoke sysconf(_SC_PAGESIZE),
                // but 4096 is correct for every platform Unity supports.
                _pageSize         = 4096;
                _allocGranularity = 4096;
            }
        }

        public static nuint GetPageSize()
        {
            if (_pageSize == 0) InitPageInfo();
            return _pageSize;
        }

        public static nuint GetAllocGranularity()
        {
            if (_allocGranularity == 0) InitPageInfo();
            return _allocGranularity;
        }

        // ===================================================================
        // Memory protection  (used by MI_GUARDED guard-page feature)
        //
        // Both platforms wired up with correct P/Invoke.
        // The runtime only loads the DLL / resolves the symbol the first time
        // the method is actually called — so having both Windows and Unix
        // declarations in one class is safe.
        // ===================================================================

        /// <summary>Mark an address range NOACCESS (guard page).</summary>
        public static bool Protect(void* addr, nuint size)
        {
            if (_isWindows)
            {
                try { return VirtualProtect(addr, size, PAGE_NOACCESS, out _); }
                catch { return false; }
            }
            if (_isLinux || _isMacOS)
            {
                try { return mprotect(addr, size, PROT_NONE) == 0; }
                catch { return false; }
            }
            return false; // unsupported platform (e.g. WebGL)
        }

        /// <summary>Restore READ+WRITE access.</summary>
        public static bool Unprotect(void* addr, nuint size)
        {
            if (_isWindows)
            {
                try { return VirtualProtect(addr, size, PAGE_READWRITE, out _); }
                catch { return false; }
            }
            if (_isLinux || _isMacOS)
            {
                try { return mprotect(addr, size, PROT_READ | PROT_WRITE) == 0; }
                catch { return false; }
            }
            return false;
        }

        // ===================================================================
        // Memory decommit / purge  (reduces RSS without freeing virtual address)
        // ===================================================================
        public static bool Commit(void* addr, nuint size)
        {
            // AlignedAlloc always returns committed memory — nothing to do.
            return true;
        }

        public static bool Decommit(void* addr, nuint size)
        {
            if (_isWindows)
            {
                // VirtualFree(MEM_DECOMMIT): releases physical pages, keeps VA.
                try { return VirtualFree(addr, size, MEM_DECOMMIT); }
                catch { return false; }
            }
            if (_isLinux)
            {
                try { return madvise(addr, size, MADV_DONTNEED) == 0; }
                catch { return false; }
            }
            if (_isMacOS)
            {
                // MADV_FREE is lazy (defers until memory pressure); preferred on macOS.
                try { return madvise(addr, size, MADV_FREE) == 0; }
                catch
                {
                    try { return madvise(addr, size, MADV_DONTNEED) == 0; }
                    catch { return false; }
                }
            }
            return true; // no-op on other platforms (WebGL, etc.)
        }

        public static bool Purge(void* addr, nuint size, bool allow_reset)
            => allow_reset && Decommit(addr, size);

        // ===================================================================
        // Thread ID
        // ===================================================================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint CurrentThreadId()
        {
#if NET6_0_OR_GREATER
            return (nuint)(ulong)Environment.CurrentManagedThreadId;
#else
            return (nuint)(ulong)System.Threading.Thread.CurrentThread.ManagedThreadId;
#endif
        }

        // ===================================================================
        // High-resolution clock  (used for PRNG seed)
        // ===================================================================
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ClockNow()
            => (ulong)System.Diagnostics.Stopwatch.GetTimestamp();

        // ===================================================================
        // Cryptographic random bytes
        // ===================================================================
        public static bool RandomBytes(byte* buf, nuint size)
        {
            try
            {
                var span = new Span<byte>(buf, (int)size);
                System.Security.Cryptography.RandomNumberGenerator.Fill(span);
                return true;
            }
            catch { return false; }
        }

        // ===================================================================
        // Environment variable
        // ===================================================================
        public static bool GetEnv(string name, byte* result, nuint result_size)
        {
            string? val = Environment.GetEnvironmentVariable("mimalloc_" + name)
                       ?? Environment.GetEnvironmentVariable("MIMALLOC_" + name.ToUpperInvariant());
            if (val == null) return false;
            nuint i = 0;
            foreach (char c in val)
            {
                if (i + 1 >= result_size) break;
                result[i++] = (byte)c;
            }
            result[i] = 0;
            return true;
        }

        // ===================================================================
        // Stderr output
        // ===================================================================
        public static void OutStderr(string? msg)
        {
            if (msg != null) MiLog.Warning(msg);
        }
    }
}
