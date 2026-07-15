// Copyright (c) 2018-2024, Microsoft Research, Daan Leijen
// C# translation of mimalloc constants from mimalloc/internal.h and mimalloc.h
using System;

namespace Mimalloc
{
    internal static unsafe class MI
    {
        // -------------------------------------------------------
        // Version
        // -------------------------------------------------------
        public const int MI_MALLOC_VERSION = 212;

        // -------------------------------------------------------
        // Sizes
        // -------------------------------------------------------
        public const nuint MI_KiB = 1024;
        public const nuint MI_MiB = 1024 * MI_KiB;
        public const nuint MI_GiB = 1024 * MI_MiB;

        // Platform word size — evaluated at runtime via IntPtr.Size.
        // DO NOT use const here: const is a compile-time value and would
        // always bake in 4 or 8 depending on the build machine, not the
        // target machine.  static readonly is evaluated once at startup
        // from the actual runtime pointer width.
        public static readonly int MI_INTPTR_SIZE = IntPtr.Size;          // 4 (32-bit) or 8 (64-bit)
        public static readonly int MI_INTPTR_BITS = IntPtr.Size * 8;      // 32 or 64
        public static readonly int MI_SIZE_SIZE   = IntPtr.Size;          // sizeof(nuint)
        public static readonly int MI_SIZE_BITS   = IntPtr.Size * 8;      // bits in nuint

        // nuint.MaxValue equivalent — all bits set, platform-width.
#if NET5_0_OR_GREATER
        public static readonly nuint MI_NUINT_MAX = nuint.MaxValue;
#else
        public static readonly nuint MI_NUINT_MAX = ~(nuint)0;
#endif

        // -------------------------------------------------------
        // Segment / page constants
        // -------------------------------------------------------
        public const nuint MI_SEGMENT_SLICE_SIZE   = 65536;          // 64 KiB slices
        public const nuint MI_SEGMENT_SIZE         = 4 * MI_MiB;     // 4 MiB per segment
        public const nuint MI_SEGMENT_ALIGN        = MI_SEGMENT_SIZE;
        public const int   MI_SLICES_PER_SEGMENT   = (int)(MI_SEGMENT_SIZE / MI_SEGMENT_SLICE_SIZE); // 64

        // Commit mask: one nuint covers MI_SIZE_BITS slice-bits.
        // 64-bit: ceil(64 slices / 64 bits) = 1 field
        // 32-bit: ceil(64 slices / 32 bits) = 2 fields
        // Computed at runtime so the struct (mask0 + mask1) is always large enough.
        public static readonly int MI_COMMIT_MASK_FIELD_COUNT =
            (MI_SLICES_PER_SEGMENT + IntPtr.Size * 8 - 1) / (IntPtr.Size * 8);

        // -------------------------------------------------------
        // Object size classes
        // -------------------------------------------------------
        public const int   MI_MAX_ALIGN_SIZE       = 16;
        public const nuint MI_MAX_ALIGN_GUARANTEE  = 8 * 1024;

        // MI_SMALL_WSIZE_MAX is the number of *words* (not bytes) that fit in
        // the small-object range.  A "word" is sizeof(void*) bytes.
        // The value 128 is the same on 32-bit and 64-bit — the byte size of the
        // small range just differs: 128×4 = 512 B (32-bit) vs 128×8 = 1024 B (64-bit).
        public const int MI_SMALL_WSIZE_MAX = 128;

        // Byte threshold for small / medium objects — depends on pointer size.
        public static readonly nuint MI_SMALL_SIZE_MAX =
            (nuint)(MI_SMALL_WSIZE_MAX * IntPtr.Size);  // 512 B (32-bit) | 1024 B (64-bit)

        // From init.c: MI_MEDIUM_OBJ_WSIZE_MAX = 655359 words
        public const nuint MI_MEDIUM_OBJ_WSIZE_MAX = 655359;
        public static readonly nuint MI_MEDIUM_OBJ_SIZE_MAX =
            (nuint)((ulong)MI_MEDIUM_OBJ_WSIZE_MAX * (ulong)IntPtr.Size);

        // -------------------------------------------------------
        // Page queues / bins
        // -------------------------------------------------------
        public const int MI_BIN_HUGE  = 73;
        public const int MI_BIN_FULL  = 74;

        // pages_free_direct: one slot per word-size index up to MI_SMALL_WSIZE_MAX,
        // plus 2 sentinels.  128 + 2 = 130 on both 32-bit and 64-bit.
        public const int MI_PAGES_DIRECT = MI_SMALL_WSIZE_MAX + 2; // = 130

        // -------------------------------------------------------
        // Block encoding / padding (disabled in release)
        // -------------------------------------------------------
        public const int MI_PADDING       = 0;
        public const int MI_PADDING_SIZE  = 0;
        public const int MI_ENCODE_FREELIST = 0;

        // -------------------------------------------------------
        // Debug levels (disabled in release)
        // -------------------------------------------------------
        public const int MI_DEBUG = 0;

        // -------------------------------------------------------
        // Thread-free tag bits
        // -------------------------------------------------------
        public const nuint MI_USE_DELAYED_FREE    = 0;
        public const nuint MI_DELAYED_FREEING     = 1;
        public const nuint MI_NO_DELAYED_FREE     = 2;
        public const nuint MI_NEVER_DELAYED_FREE  = 3;

        // -------------------------------------------------------
        // Segment kinds
        // -------------------------------------------------------
        public const int MI_SEGMENT_NORMAL = 0;
        public const int MI_SEGMENT_HUGE   = 1;

        // -------------------------------------------------------
        // Memory kinds (mi_memkind_t)
        // -------------------------------------------------------
        public const byte MI_MEM_NONE    = 0;
        public const byte MI_MEM_EXTERNAL= 1;
        public const byte MI_MEM_STATIC  = 2;
        public const byte MI_MEM_OS      = 3;
        public const byte MI_MEM_OS_HUGE = 4;
        public const byte MI_MEM_OS_REMAP= 5;
        public const byte MI_MEM_ARENA   = 6;

        // -------------------------------------------------------
        // Misc
        // -------------------------------------------------------
        public static nuint MI_MAX_ALLOC_SIZE => unchecked((nuint)(ulong.MaxValue / 2));
        public const int   MI_MAX_ALIGN_GUARANTEE_LOG2 = 13;

        // Large-object threshold: last regular bin × word size
        public static readonly nuint MI_LARGE_OBJ_SIZE_MAX =
            (nuint)(524288UL * (ulong)IntPtr.Size);  // 2 MiB (32-bit) | 4 MiB (64-bit)

        // Bitmap field width in bits = width of nuint in bits
        public static readonly int MI_BITMAP_FIELD_BITS = IntPtr.Size * 8;  // 32 or 64

        // All-ones bitmap field
        public static nuint MI_BITMAP_FIELD_FULL => ~(nuint)0;

        // Segment-map address coverage:
        //   64-bit: 2^47 (128 TiB user space on x86-64 / arm64)
        //   32-bit: 2^32 (full 4 GiB)
        public static nuint MI_SEGMENT_MAP_MAX_ADDRESS =>
            IntPtr.Size >= 8
                ? unchecked((nuint)(48UL * 1024UL * (ulong)MI_GiB))
                : unchecked((nuint)uint.MaxValue);

        // Arena
        public const int   MI_MAX_ARENAS       = 112;
        public const nuint MI_ARENA_BLOCK_SIZE = MI_SEGMENT_ALIGN;

        // Thread-local segment span queues
        public const int MI_SEGMENT_BIN_MAX = 35;

        // Chacha random
        public const int MI_CHACHA_ROUNDS = 20;

        // Options
        public const int MI_OPTION_COUNT = 32;

        // max error/warning count
        public const long MI_MAX_ERROR_COUNT   = 16;
        public const long MI_MAX_WARNING_COUNT = 16;

        // Segment header size in slices
        public const int MI_SEGMENT_INFO_SLICES_DEFAULT = 1;
    }
}
