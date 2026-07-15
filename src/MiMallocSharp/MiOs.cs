// C# translation of mimalloc/src/os.c
// OS memory management layer
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiOs
    {
        // -------------------------------------------------------
        // OS memory config (mirrors mi_os_mem_config in os.c)
        // -------------------------------------------------------
        private static mi_os_mem_config_t _config;
        private static bool _configInitialized = false;

        private static void EnsureConfig()
        {
            if (_configInitialized) return;
            _configInitialized = true;
            _config.page_size           = MiPlatform.GetPageSize();
            _config.large_page_size     = 0;           // not supported in managed
            _config.alloc_granularity   = MiPlatform.GetAllocGranularity();
            // 64-bit: assume 32 GiB; 32-bit: cap at ~4 GiB (full address space)
            _config.physical_memory     = IntPtr.Size >= 8
                ? unchecked((nuint)(32UL * 1024 * 1024 * 1024))
                : unchecked((nuint)(4UL  * 1024 * 1024 * 1024 - 1));
            _config.virtual_address_bits= (byte)(IntPtr.Size >= 8 ? 48 : 32);
            _config.has_overcommit      = 1;
            _config.can_partial_free    = 0;
            _config.has_virtual_reserve = 0;
        }

        public static void _mi_os_init() => EnsureConfig();

        // -------------------------------------------------------
        // Page / alignment info
        // -------------------------------------------------------
        public static nuint _mi_os_page_size()
        {
            EnsureConfig();
            return _config.page_size;
        }

        public static nuint _mi_os_large_page_size()
        {
            EnsureConfig();
            return _config.large_page_size != 0 ? _config.large_page_size : _config.page_size;
        }

        public static bool _mi_os_has_overcommit() { EnsureConfig(); return _config.has_overcommit != 0; }
        public static bool _mi_os_has_virtual_reserve() { EnsureConfig(); return _config.has_virtual_reserve != 0; }

        public static nuint _mi_os_good_alloc_size(nuint size)
        {
            nuint align_size;
            if      (size < 512 * MI.MI_KiB)  align_size = _mi_os_page_size();
            else if (size < 2  * MI.MI_MiB)   align_size = 64  * MI.MI_KiB;
            else if (size < 8  * MI.MI_MiB)   align_size = 256 * MI.MI_KiB;
            else if (size < 32 * MI.MI_MiB)   align_size = 1   * MI.MI_MiB;
            else                               align_size = 4   * MI.MI_MiB;
            if (size >= MI.MI_NUINT_MAX - align_size) return size;
            return MiLibc._mi_align_up(size, align_size);
        }

        // -------------------------------------------------------
        // memid helpers (mirrors _mi_memid_* in os.c / internal.h)
        // -------------------------------------------------------
        public static mi_memid_t _mi_memid_none()
        {
            mi_memid_t id = default;
            id.memkind = MI.MI_MEM_NONE;
            return id;
        }

        public static mi_memid_t _mi_memid_create_os(void* p, nuint size, bool committed, bool zero, bool is_large)
        {
            mi_memid_t id = default;
            id.mem.os.@base      = p;
            id.mem.os.alignment  = 0;
            id.memkind           = MI.MI_MEM_OS;
            id.initially_committed = committed ? (byte)1 : (byte)0;
            id.initially_zero      = zero      ? (byte)1 : (byte)0;
            id.is_pinned           = is_large  ? (byte)1 : (byte)0;
            return id;
        }

        // -------------------------------------------------------
        // Core allocate / free
        // -------------------------------------------------------
        public static void* _mi_os_alloc(nuint size, mi_memid_t* memid)
        {
            *memid = _mi_memid_none();
            if (size == 0) return null;
            nuint aligned_size = _mi_os_good_alloc_size(size);
            void* p = MiPlatform.AlignedAlloc(aligned_size, _mi_os_page_size());
            if (p == null) return null;
            *memid = _mi_memid_create_os(p, aligned_size, true, false, false);
            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
            {
                MiStats._mi_stat_increase(&s->reserved, aligned_size);
                MiStats._mi_stat_increase(&s->committed, aligned_size);
            }
            return p;
        }

        public static void* _mi_os_zalloc(nuint size, mi_memid_t* memid)
        {
            void* p = _mi_os_alloc(size, memid);
            if (p != null && memid->initially_zero == 0)
            {
                MiLibc.mi_memzero(p, size);
                memid->initially_zero = 1;
            }
            return p;
        }

        public static void* _mi_os_alloc_aligned(nuint size, nuint alignment, bool commit, bool allow_large, mi_memid_t* memid)
        {
            *memid = _mi_memid_none();
            if (size == 0) return null;
            if (alignment < _mi_os_page_size()) alignment = _mi_os_page_size();
            nuint aligned_size = _mi_os_good_alloc_size(size);
            void* p = MiPlatform.AlignedAlloc(aligned_size, alignment);
            if (p == null) return null;
            *memid = _mi_memid_create_os(p, aligned_size, commit, false, false);
            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
            {
                MiStats._mi_stat_increase(&s->reserved, aligned_size);
                if (commit) MiStats._mi_stat_increase(&s->committed, aligned_size);
            }
            return p;
        }

        public static void* _mi_os_alloc_aligned_at_offset(nuint size, nuint alignment, nuint offset, bool commit, bool allow_large, mi_memid_t* memid)
        {
            if (offset == 0) return _mi_os_alloc_aligned(size, alignment, commit, allow_large, memid);
            nuint extra = MiLibc._mi_align_up(offset, alignment) - offset;
            nuint oversize = size + extra;
            void* start = _mi_os_alloc_aligned(oversize, alignment, commit, allow_large, memid);
            if (start == null) return null;
            void* p = (byte*)start + extra;
            return p;
        }

        public static void _mi_os_free(void* p, nuint size, mi_memid_t memid)
        {
            if (p == null || size == 0) return;
            nuint aligned_size = _mi_os_good_alloc_size(size);
            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
            {
                MiStats._mi_stat_decrease(&s->reserved, aligned_size);
                MiStats._mi_stat_decrease(&s->committed, aligned_size);
            }
            if (memid.memkind == MI.MI_MEM_OS || memid.memkind == MI.MI_MEM_OS_HUGE)
                MiPlatform.AlignedFree(memid.mem.os.@base);
        }

        // -------------------------------------------------------
        // Commit / Decommit / Purge / Protect
        // -------------------------------------------------------
        public static bool _mi_os_commit(void* addr, nuint size, bool* is_zero)
        {
            if (is_zero != null) *is_zero = false;
            return MiPlatform.Commit(addr, size);
        }

        public static bool _mi_os_commit_ex(void* addr, nuint size, bool* is_zero, nuint stat_size)
            => _mi_os_commit(addr, size, is_zero);

        public static bool _mi_os_decommit(void* addr, nuint size)
            => MiPlatform.Decommit(addr, size);

        public static bool _mi_os_reset(void* addr, nuint size) => true; // no-op

        public static bool _mi_os_purge(void* p, nuint size)
            => _mi_os_purge_ex(p, size, true, size);

        public static bool _mi_os_purge_ex(void* p, nuint size, bool allow_reset, nuint stat_size)
        {
            if (MiOptions.mi_option_get(mi_option_t.mi_option_purge_delay) < 0) return false;
            // In our managed impl, we don't actually decommit -- just accept it
            return false;
        }

        public static bool _mi_os_protect(void* addr, nuint size)
            => MiPlatform.Protect(addr, size);

        public static bool _mi_os_unprotect(void* addr, nuint size)
            => MiPlatform.Unprotect(addr, size);

        // -------------------------------------------------------
        // NUMA (simplified - single node)
        // -------------------------------------------------------
        public static int _mi_os_numa_node() => 0;
        public static int _mi_os_numa_node_count() => 1;

        // -------------------------------------------------------
        // Huge OS pages (not supported in managed implementation)
        // -------------------------------------------------------
        public static void* _mi_os_alloc_huge_os_pages(nuint pages, int numa_node, long max_msecs,
            nuint* pages_reserved, nuint* psize, mi_memid_t* memid)
        {
            *memid = _mi_memid_none();
            if (psize != null) *psize = 0;
            if (pages_reserved != null) *pages_reserved = 0;
            return null; // not supported
        }

        // -------------------------------------------------------
        // Pointer arithmetic helpers (mirror C macros)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_segment_t* _mi_ptr_segment(void* p)
        {
            // round down to segment alignment (MI_SEGMENT_ALIGN = 4 MiB)
            return (mi_segment_t*)((nuint)p & ~(MI.MI_SEGMENT_ALIGN - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint _mi_ptr_cookie(mi_segment_t* segment)
        {
            // simple cookie: segment pointer XOR-shifted
            return (nuint)segment ^ ((nuint)segment >> 13);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* mi_align_up_ptr(void* p, nuint alignment)
            => (void*)MiLibc._mi_align_up((nuint)p, alignment);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* mi_align_down_ptr(void* p, nuint alignment)
            => (void*)MiLibc._mi_align_down((nuint)p, alignment);
    }
}
