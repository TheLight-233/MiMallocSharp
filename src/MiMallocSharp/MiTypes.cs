// Copyright (c) 2018-2024, Microsoft Research, Daan Leijen
// C# translation of mimalloc/internal.h data structures
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mimalloc
{
    // -------------------------------------------------------
    // mi_block_t: a free-list block
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_block_t
    {
        public nuint next; // encoded pointer to next free block (or 0)
    }

    // -------------------------------------------------------
    // mi_page_flags_t (packed byte)
    //   bit 0 = in_full
    //   bit 1 = has_aligned
    // -------------------------------------------------------
    // Accessed directly as a byte with helper methods in MiPage

    // -------------------------------------------------------
    // mi_delayed_t (enum equivalent)
    // -------------------------------------------------------
    internal enum mi_delayed_t : uint
    {
        MI_USE_DELAYED_FREE   = 0, // push on thread_delayed_free
        MI_DELAYED_FREEING    = 1, // being freed by a thread
        MI_NO_DELAYED_FREE    = 2, // optimal: push on page->thread_free
        MI_NEVER_DELAYED_FREE = 3  // page is full (no free)
    }

    // -------------------------------------------------------
    // mi_page_t: a page within a segment
    // Layout must be kept in sync with alloc fast-path assumptions
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_page_t
    {
        // Segment slice info (first 2 fields for slice management)
        public uint    slice_count;       // slices in this page (0 if this is just a slice in another page)
        public uint    slice_offset;      // distance from segment slices base to this page

        // Bitfield byte: bit0=is_committed, bit1=is_zero_init
        public byte    is_bits;
        private byte   _pad0;

        // In-use by malloc fast path
        public ushort  capacity;          // blocks committed
        public ushort  reserved;          // blocks reserved in memory
        public byte    flags;             // bit0=in_full, bit1=has_aligned
        // Bitfield byte: bit0=free_is_zero, bits1-7=retire_expire
        public byte    free_retire;

        public mi_block_t* free;          // list of available free blocks
        public mi_block_t* local_free;    // list of deferred free blocks (this thread)
        public uint    used;              // blocks in use
        public byte    block_size_shift;  // if != 0: (1 << block_size_shift) == block_size
        public byte    heap_tag;          // owning heap tag
        private ushort _pad1;
        public nuint   block_size;        // size_t: size of each block (always > 0)
        public byte*   page_start;        // start of the page area

        // Two random keys for free-list encoding (only if MI_ENCODE_FREELIST)
        public nuint   key0;
        public nuint   key1;

        // Atomic fields
        public nuint   xthread_free;      // _Atomic(mi_thread_free_t)
        public nuint   xheap;             // _Atomic(uintptr_t) -- pointer to heap

        public mi_page_t* next;           // next page in queue
        public mi_page_t* prev;           // prev page in queue

        // 64-bit: 1 word padding to make sizeof(mi_page_t) == 96 bytes
        public nuint   _pad2;

        // ---- Bitfield accessors ----
        public bool is_committed   { get => (is_bits & 0x01) != 0; set { if (value) is_bits |= 0x01; else is_bits &= 0xFE; } }
        public bool is_zero_init   { get => (is_bits & 0x02) != 0; set { if (value) is_bits |= 0x02; else is_bits &= 0xFD; } }
        public bool in_full        { get => (flags & 0x01) != 0;   set { if (value) flags |= 0x01; else flags &= 0xFE; } }
        public bool has_aligned    { get => (flags & 0x02) != 0;   set { if (value) flags |= 0x02; else flags &= 0xFD; } }
        public bool is_huge        { get => (flags & 0x04) != 0;   set { if (value) flags |= 0x04; else flags &= 0xFB; } }
        public bool free_is_zero   { get => (free_retire & 0x01) != 0; set { if (value) free_retire |= 0x01; else free_retire &= 0xFE; } }
        public byte retire_expire  { get => (byte)(free_retire >> 1); set { free_retire = (byte)((free_retire & 0x01) | (value << 1)); } }
    }

    // -------------------------------------------------------
    // mi_page_queue_t: doubly-linked list of pages of same size class
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_page_queue_t
    {
        public mi_page_t* first;
        public mi_page_t* last;
        public nuint      block_size; // size class for this queue
    }

    // -------------------------------------------------------
    // mi_commit_mask_t: bitmap of committed slices
    // On 64-bit: 1 size_t field covers 64 slices (= MI_SLICES_PER_SEGMENT)
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_commit_mask_t
    {
        public nuint mask0; // bits for slices 0..63
        // On 32-bit a second field would be needed; we always include it for safety
        public nuint mask1; // currently unused on 64-bit
    }

    // -------------------------------------------------------
    // mi_memid_t: identifies how memory was allocated
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_memid_os_info_t
    {
        public void*  @base;
        public nuint  alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_memid_arena_info_t
    {
        public nuint  block_index;
        public int    id;             // arena id
        public byte   is_exclusive;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct mi_memid_mem_t
    {
        [FieldOffset(0)] public mi_memid_os_info_t    os;
        [FieldOffset(0)] public mi_memid_arena_info_t arena;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_memid_t
    {
        public mi_memid_mem_t mem;
        public byte  is_pinned;            // bool
        public byte  initially_committed;  // bool
        public byte  initially_zero;       // bool
        public byte  memkind;              // mi_memkind_t
        private uint _pad;
    }

    // -------------------------------------------------------
    // mi_segment_kind_t
    // -------------------------------------------------------
    internal enum mi_segment_kind_t : int
    {
        MI_SEGMENT_NORMAL = 0,
        MI_SEGMENT_HUGE   = 1,
    }

    // -------------------------------------------------------
    // mi_segment_t: a large OS allocation containing pages
    // Note: in C, slices[] is embedded in the struct. In C#
    // we access slices via pointer arithmetic after the header.
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_segment_t
    {
        // Constant fields (set on allocation, never change)
        public mi_memid_t        memid;             // how was this segment allocated?
        public byte              allow_decommit;    // bool
        public byte              allow_purge;       // bool
        private ushort           _pad0;
        public nuint             segment_size;      // total size (usually 4 MiB)

        // Slice commit tracking
        public mi_commit_mask_t  commit_mask;
        public mi_commit_mask_t  decommit_mask;
        public byte              commit_is_delay;   // bool
        public byte              memid_is_arena;    // bool
        public byte              memid_os;          // bool
        private byte             _pad1;

        // Zero-initialized from here
        public mi_segment_t*     next;
        public mi_segment_t*     abandoned_next;
        public mi_segment_t*     abandoned_os_next;
        public mi_segment_t*     abandoned_os_prev;

        public nuint             abandoned;         // count of abandoned pages
        public nuint             abandoned_visits;
        public nuint             used;              // pages in use
        public nuint             cookie;
        public nuint             segment_slices;    // number of slices in this segment
        public nuint             segment_info_slices; // slices used by segment header

        public mi_segment_kind_t kind;
        private uint             _pad2;
        public nuint             thread_id;         // _Atomic(uintptr_t) -- owning thread

        public mi_subproc_t*     subproc;           // owning sub-process

        // NOTE: in C, slices[MI_SLICES_PER_SEGMENT+1] follow here.
        // In C# we access them via mi_segment_get_slice() below.
    }

    // -------------------------------------------------------
    // mi_subproc_t: per-sub-process state
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_subproc_t
    {
        public nuint          abandoned_count;         // _Atomic(size_t)
        public nuint          abandoned_os_list_count; // _Atomic(size_t)
        public mi_lock_t      abandoned_os_lock;
        public mi_segment_t*  abandoned_os_list;
        public mi_segment_t*  abandoned_os_list_tail;
        public mi_memid_t     memid;
    }

    // -------------------------------------------------------
    // mi_lock_t: a simple spin-lock
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_lock_t
    {
        public int locked; // 0=free, 1=locked  (_Atomic int)
    }

    // -------------------------------------------------------
    // mi_random_ctx_t: ChaCha20 PRNG state
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_random_ctx_t
    {
        public fixed uint input[16];
        public fixed uint output[16];
        public int        output_available;
        public byte       weak; // bool
        private byte      _p0;
        private ushort    _p1;
    }

    // -------------------------------------------------------
    // Statistics types
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_stat_count_t
    {
        public long current;
        public long peak;
        public long total;
        public long _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_stat_counter_t
    {
        public long total;
        public long _pad;
    }

    // Full stats struct (mirrors mi_stats_t in mimalloc/internal.h)
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_stats_t
    {
        public mi_stat_count_t   pages;
        public mi_stat_count_t   reserved;
        public mi_stat_count_t   committed;
        public mi_stat_count_t   reset;
        public mi_stat_count_t   purged;
        public mi_stat_count_t   page_committed;
        public mi_stat_count_t   segments;
        public mi_stat_count_t   segments_abandoned;
        public mi_stat_count_t   segments_cache;
        public mi_stat_count_t   pages_abandoned;
        public mi_stat_count_t   pages_extended;
        public mi_stat_count_t   mmap_calls;
        public mi_stat_count_t   commit_calls;
        public mi_stat_count_t   reset_calls;
        public mi_stat_count_t   purge_calls;
        public mi_stat_count_t   page_no_retire;
        public mi_stat_count_t   searches;
        public mi_stat_count_t   normal;
        public mi_stat_count_t   huge;
        public mi_stat_count_t   malloc_normal;
        public mi_stat_count_t   malloc_large;
        public mi_stat_count_t   malloc_huge;
        public mi_stat_count_t   threads;

        public mi_stat_counter_t malloc_normal_count;
        public mi_stat_counter_t malloc_large_count;
        public mi_stat_counter_t malloc_huge_count;
        public mi_stat_counter_t malloc_guarded_count;
        public mi_stat_counter_t arena_count;
        public mi_stat_counter_t arena_crossover_count;
        public mi_stat_counter_t arena_rollback_count;
        public mi_stat_counter_t guarded_alloc_count;

        // malloc_bins[74]: one count per bin
        public fixed long malloc_bins_storage[74 * 4]; // 74 mi_stat_count_t

        // malloc_requested (2 stat_counts)
        public mi_stat_count_t malloc_requested;
        public mi_stat_count_t malloc_wasted;
    }

    // -------------------------------------------------------
    // mi_os_mem_config_t
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct mi_os_mem_config_t
    {
        public nuint page_size;
        public nuint large_page_size;
        public nuint alloc_granularity;
        public nuint physical_memory;
        public nuint virtual_address_bits;
        public byte  has_overcommit;
        public byte  can_partial_free;
        public byte  has_virtual_reserve;
        private byte _pad;
    }

    // -------------------------------------------------------
    // mi_segment_span_queue_t: queue of free segment spans
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_segment_span_queue_t
    {
        public mi_page_t* first;
        public mi_page_t* last;
        public nuint      slice_count;
    }

    // -------------------------------------------------------
    // mi_segments_tld_t: thread-local segment data
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_segments_tld_t
    {
        // span queues (35 bins, from segment.c/init.c)
        public fixed byte span_queue_storage[MI.MI_SEGMENT_BIN_MAX * 24]; // 35 * sizeof(mi_segment_span_queue_t)
        public nuint   count;              // current segment count
        public nuint   peak_count;
        public nuint   current_size;
        public nuint   peak_size;
        public mi_stats_t* stats;
        public mi_os_tld_t* os;
        public mi_subproc_t* subproc;
    }

    // -------------------------------------------------------
    // mi_os_tld_t: thread-local OS data
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_os_tld_t
    {
        public nuint       region_idx;
        public mi_stats_t* stats;
    }

    // -------------------------------------------------------
    // mi_tld_t: all thread-local data
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_tld_t
    {
        public ulong             heartbeat;
        public byte              recurse;       // bool: true if in deferred call
        private byte             _p0;
        private ushort           _p1;
        private uint             _p2;
        public mi_heap_t*        heap_backing;  // backing heap of this thread
        public mi_heap_t*        heaps;         // list of heaps in this thread
        public mi_segments_tld_t segments;
        public mi_os_tld_t       os;
        public mi_stats_t        stats;
    }

    // -------------------------------------------------------
    // mi_heap_t: per-thread allocator
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_heap_t
    {
        public mi_tld_t*         tld;
        public nuint             thread_delayed_free;  // _Atomic(mi_block_t*) head of delayed free list
        public nuint             thread_id;            // owning thread
        public int               arena_id;             // mi_arena_id_t
        public nuint             cookie;
        public nuint             key0;
        public nuint             key1;
        public mi_random_ctx_t   random;
        public nuint             page_count;
        public nuint             page_retired_min;
        public nuint             page_retired_max;
        public mi_heap_t*        next;
        public byte              no_reclaim;
        public byte              tag;
        private ushort           _pad0;
        private uint             _pad1;

        // pages_free_direct: 66 entries, each a mi_page_t*
        public fixed ulong pages_free_direct_storage[130]; // MI_PAGES_DIRECT=130 pointers (as ulong for fixed array)

        // pages[75]: mi_page_queue_t for each bin 0..MI_BIN_FULL
        // Each mi_page_queue_t = 3 * 8 = 24 bytes  =>  75 * 24 = 1800 bytes
        public fixed byte pages_storage[75 * 24];
    }

    // -------------------------------------------------------
    // mi_thread_data_t: combines heap + tld for a thread
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_thread_data_t
    {
        public mi_heap_t heap;  // NOTE: must be first
        public mi_tld_t  tld;
        public mi_memid_t memid;
    }

    // -------------------------------------------------------
    // Bitmap types (from bitmap.h)
    // -------------------------------------------------------
    // mi_bitmap_t is just nuint* (pointer to atomic size_t fields)
    // mi_bitmap_index_t is nuint (field_index * BITS + bit_index)

    // -------------------------------------------------------
    // mi_arena_t: an arena of preallocated memory
    // -------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_arena_t
    {
        public int       id;               // arena id
        public byte      is_exclusive;     // bool
        public byte      _pad0;
        public ushort    _pad1;
        public nuint     slice_count;      // number of slices in the arena
        public nuint     info_slices;      // number of slices used by arena info
        public int       numa_node;
        public byte      allow_decommit;   // bool
        public byte      is_large;         // bool
        public byte      _pad2;
        public byte      _pad3;
        public nuint     blocks_committed; // _Atomic: count of committed blocks
        public nuint     blocks_dirty;     // _Atomic: dirty (OS was written to but reset)
        public nuint     blocks_abandoned; // _Atomic: abandoned page bitmap fields
        public void*     start;            // start of the arena memory
        public mi_memid_t memid;
        public nuint*    blocks_inuse;     // bitmap: in-use blocks
        public nuint*    blocks_purge;     // bitmap: needs purge
        public nuint*    blocks_abandoned_ptr; // bitmap pointer
    }

    // -------------------------------------------------------
    // mi_option_desc_t (from options.c)
    // -------------------------------------------------------
    internal enum mi_init_t : int
    {
        UNINIT      = 0,
        DEFAULTED   = 1,
        INITIALIZED = 2,
    }

    // option enum
    internal enum mi_option_t : int
    {
        mi_option_show_errors = 0,
        mi_option_show_stats,
        mi_option_verbose,
        mi_option_eager_commit,
        mi_option_arena_eager_commit,
        mi_option_purge_decommits,
        mi_option_allow_large_os_pages,
        mi_option_reserve_huge_os_pages,
        mi_option_reserve_huge_os_pages_at,
        mi_option_reserve_os_memory,
        mi_option_deprecated_segment_cache,
        mi_option_deprecated_page_reset,
        mi_option_abandoned_page_purge,
        mi_option_deprecated_segment_reset,
        mi_option_eager_commit_delay,
        mi_option_purge_delay,
        mi_option_use_numa_nodes,
        mi_option_disallow_os_alloc,
        mi_option_os_tag,
        mi_option_max_errors,
        mi_option_max_warnings,
        mi_option_max_segment_reclaim,
        mi_option_destroy_on_exit,
        mi_option_arena_reserve,
        mi_option_arena_purge_mult,
        mi_option_disallow_arena_alloc,
        mi_option_retry_on_oom,
        mi_option_visit_abandoned,
        mi_option_guarded_min,
        mi_option_guarded_max,
        mi_option_guarded_precise,
        mi_option_guarded_sample_rate,
        _mi_option_last
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_option_desc_t
    {
        public long        value;
        public mi_init_t   init;
        public mi_option_t option;
        public byte*       name;
        public byte*       legacy_name;
    }

    // -------------------------------------------------------
    // Segmap part (from segment-map.c)
    // -------------------------------------------------------
    // MI_SEGMENT_MAP_PART_ENTRIES = (MI_INTPTR_SIZE * MI_KiB - 128) / MI_INTPTR_SIZE = (8192-128)/8 = 1008
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mi_segmap_part_t
    {
        public mi_memid_t memid;
        // map array of 1008 atomic nuint entries follows after this struct in allocated memory
        // We access them via: (nuint*)((byte*)part + sizeof(mi_segmap_part_t))
    }
}
