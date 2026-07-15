// C# translation of mimalloc/src/segment-map.c
// Maintains a bitmap mapping memory addresses to segment ownership.
// Allows O(1) pointer validation: is this pointer inside a mimalloc segment?
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiSegmentMap
    {
        // -----------------------------------------------------------------------
        // Bitmap layout — depends on pointer width at runtime.
        //
        // Each bit represents one MI_SEGMENT_ALIGN (4 MiB) slot.
        // Each bitmap field is one nuint wide (32-bit on 32-bit, 64-bit on 64-bit).
        //
        //  64-bit: user space ≤ 2^47 (128 TiB on x86-64 / arm64)
        //          slots  = 2^47 / 4 MiB = 33,554,432
        //          fields = 33,554,432 / 64  = 524,288   → 4 MiB bitmap
        //
        //  32-bit: full address space = 2^32 (4 GiB)
        //          slots  = 2^32 / 4 MiB = 1,024
        //          fields = 1,024 / 32   = 32            → 128 B bitmap
        //          (using 512 for a comfortable margin)
        // -----------------------------------------------------------------------
        private static readonly int MAP_FIELD_BITS =    // bits per nuint field
            IntPtr.Size * 8;                            // 32 (32-bit) | 64 (64-bit)

        private static readonly int MAP_FIELDS =        // total nuint fields in bitmap
            IntPtr.Size >= 8 ? 524288 : 512;

        private static readonly ulong MAP_BIT_SPAN =    // bytes covered by one bit
            (ulong)MI.MI_SEGMENT_ALIGN;

        private static readonly nuint* _map;

        static MiSegmentMap()
        {
            // Zeroed bitmap.
            // 64-bit: 524288 × 8 = 4 MiB
            // 32-bit: 512    × 4 = 2 KiB
            _map = (nuint*)MiPlatform.Zalloc((nuint)(MAP_FIELDS * IntPtr.Size));
        }

        // -------------------------------------------------------
        // Index helpers
        // -------------------------------------------------------
        private static bool GetIdx(mi_segment_t* segment, out nuint idx, out nuint bitidx)
        {
            idx = bitidx = 0;
            ulong addr   = (ulong)(nuint)segment;
            ulong bitofs = addr / MAP_BIT_SPAN;
            ulong field  = bitofs / (ulong)MAP_FIELD_BITS;
            if (field >= (ulong)MAP_FIELDS) return false;
            idx    = (nuint)field;
            bitidx = (nuint)(bitofs % (ulong)MAP_FIELD_BITS);
            return true;
        }

        // -------------------------------------------------------
        // Register / deregister a segment
        // -------------------------------------------------------
        public static void _mi_segment_map_allocated_at(mi_segment_t* segment)
        {
            if (!GetIdx(segment, out nuint idx, out nuint bitidx)) return;
            nuint mask = MiAtomic.load_relaxed(_map + idx);
            nuint newmask;
            do { newmask = mask | ((nuint)1 << (int)bitidx); }
            while (!MiAtomic.cas_weak_release(_map + idx, ref mask, newmask));
        }

        public static void _mi_segment_map_freed_at(mi_segment_t* segment)
        {
            if (!GetIdx(segment, out nuint idx, out nuint bitidx)) return;
            nuint mask = MiAtomic.load_relaxed(_map + idx);
            nuint newmask;
            do { newmask = mask & ~((nuint)1 << (int)bitidx); }
            while (!MiAtomic.cas_weak_release(_map + idx, ref mask, newmask));
        }

        // -------------------------------------------------------
        // Is a pointer inside any of our segments?
        // -------------------------------------------------------
        public static bool mi_is_in_heap_region(void* p)
        {
            if (p == null) return false;
            mi_segment_t* segment = MiOs._mi_ptr_segment(p);
            if (!GetIdx(segment, out nuint idx, out nuint bitidx)) return false;
            nuint mask = MiAtomic.load_relaxed(_map + idx);
            return (mask & ((nuint)1 << (int)bitidx)) != 0;
        }

        // -------------------------------------------------------
        // Destroy (called on process exit)
        // -------------------------------------------------------
        public static void _mi_segment_map_unsafe_destroy()
        {
            if (_map != null)
                MiLibc.mi_memzero(_map, (nuint)(MAP_FIELDS * IntPtr.Size));
        }
    }
}
