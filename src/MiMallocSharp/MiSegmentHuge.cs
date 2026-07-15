// Extension to MiSegment.cs: huge-page free / reset helpers
using System;

namespace Mimalloc
{
    internal static unsafe partial class MiSegment
    {
        // Called from mi_free_block_mt for huge pages
        public static void _mi_segment_huge_page_reset_block(mi_segment_t* segment, mi_page_t* page, mi_block_t* block)
        {
            // Just zero the block header to avoid data leaks (mimalloc resets huge page content)
            MiLibc.mi_memzero(block, (nuint)sizeof(mi_block_t));
        }
    }
}
