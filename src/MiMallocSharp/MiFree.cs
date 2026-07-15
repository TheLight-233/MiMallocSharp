// C# translation of mimalloc/src/free.c
// Block free path: local, multi-threaded, delayed
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiFree
    {
        // -------------------------------------------------------
        // Local free (fast path, same-thread)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_free_block_local(mi_page_t* page, mi_block_t* block, bool track_stats, bool check_full)
        {
            // push onto local_free list
            MiPage.mi_block_set_next(page, block, page->local_free);
            page->local_free = block;
            page->used--;
            if (page->used == 0)
                MiPage._mi_page_retire(page);
            else if (check_full && MiPage.mi_page_is_in_full(page))
                MiPage._mi_page_unfull(page);
        }

        // -------------------------------------------------------
        // Generic local free (handles aligned blocks)
        // -------------------------------------------------------
        public static void mi_free_generic_local(mi_page_t* page, mi_segment_t* segment, void* p)
        {
            mi_block_t* block = MiPage.mi_page_has_aligned(page)
                ? MiPage._mi_page_ptr_unalign(page, p)
                : (mi_block_t*)p;
            mi_free_block_local(page, block, true, true);
        }

        // -------------------------------------------------------
        // Multi-threaded free (cross-thread)
        // -------------------------------------------------------
        public static void mi_free_generic_mt(mi_page_t* page, mi_segment_t* segment, void* p)
        {
            mi_block_t* block = MiPage._mi_page_ptr_unalign(page, p); // safe: don't check has_aligned flag
            mi_free_block_mt(page, segment, block);
        }

        public static void _mi_free_generic(mi_segment_t* segment, mi_page_t* page, bool is_local, void* p)
        {
            if (is_local) mi_free_generic_local(page, segment, p);
            else          mi_free_generic_mt(page, segment, p);
        }

        // Push block onto page's thread-free list or the heap's delayed-free list
        private static void mi_free_block_delayed_mt(mi_page_t* page, mi_block_t* block)
        {
            nuint tfreex;
            bool use_delayed;
            nuint tfree = MiAtomic.load_relaxed(&page->xthread_free);
            do
            {
                use_delayed = (MiPage.mi_page_thread_free_flag(page) == mi_delayed_t.MI_USE_DELAYED_FREE);
                if (use_delayed)
                    tfreex = ((tfree & ~(nuint)3) | (nuint)mi_delayed_t.MI_DELAYED_FREEING);
                else
                {
                    MiPage.mi_block_set_next(page, block, MiPage.mi_page_thread_free(page));
                    tfreex = ((tfree & 3) | (nuint)block);
                }
            } while (!MiAtomic.cas_weak_release(&page->xthread_free, ref tfree, tfreex));

            if (use_delayed)
            {
                mi_heap_t* heap = (mi_heap_t*)MiAtomic.load_acquire(&page->xheap);
                if (heap != null)
                {
                    nuint dfree = MiAtomic.load_relaxed((nuint*)&heap->thread_delayed_free);
                    do { MiPage.mi_block_set_nextx(heap, block, (mi_block_t*)dfree, heap->key0, heap->key1); }
                    while (!MiAtomic.cas_strong_acq_rel((nuint*)&heap->thread_delayed_free, ref dfree, (nuint)block));
                }
                // reset flag back to NO_DELAYED_FREE
                tfree = MiAtomic.load_relaxed(&page->xthread_free);
                do { tfreex = (tfree & ~(nuint)3) | (nuint)mi_delayed_t.MI_NO_DELAYED_FREE; }
                while (!MiAtomic.cas_weak_release(&page->xthread_free, ref tfree, tfreex));
            }
        }

        public static void mi_free_block_mt(mi_page_t* page, mi_segment_t* segment, mi_block_t* block)
        {
            // try to reclaim abandoned segment
            if (MiOptions.mi_option_abandoned_reclaim_on_free() != 0
                && MiAtomic.load_relaxed(&segment->thread_id) == 0
                && MiInit.mi_prim_get_default_heap() != MiInit._mi_heap_empty_ptr)
            {
                mi_heap_t* default_heap = MiInit.mi_prim_get_default_heap();
                if (default_heap != null && MiSegment._mi_segment_attempt_reclaim(default_heap, segment))
                {
                    mi_free(block); // now local
                    return;
                }
            }

            if (segment->kind == mi_segment_kind_t.MI_SEGMENT_HUGE)
            {
                // huge pages: reset and push on delayed list
                MiSegment._mi_segment_huge_page_reset_block(segment, page, block);
            }

            mi_free_block_delayed_mt(page, block);
        }

        // -------------------------------------------------------
        // Main mi_free entry point
        // -------------------------------------------------------
        public static void mi_free(void* p)
        {
            if (p == null) return;
            mi_segment_t* segment = MiOs._mi_ptr_segment(p);
            if (segment == null) return; // not in our heap

            bool is_local = MiPlatform.CurrentThreadId() == MiAtomic.load_relaxed(&segment->thread_id);
            mi_page_t* page = MiSegment._mi_segment_page_of(segment, p);

            if (is_local)
            {
                if (page->flags == 0) // not full, no aligned blocks
                {
                    mi_block_t* block = (mi_block_t*)p;
                    mi_free_block_local(page, block, true, false);
                }
                else
                {
                    mi_free_generic_local(page, segment, p);
                }
            }
            else
            {
                mi_free_generic_mt(page, segment, p);
            }
        }

        public static void mi_ufree(void* p, nuint* usable)
        {
            if (usable != null && p != null)
            {
                mi_segment_t* segment = MiOs._mi_ptr_segment(p);
                if (segment != null)
                {
                    mi_page_t* page = MiSegment._mi_segment_page_of(segment, p);
                    *usable = MiPage.mi_page_usable_block_size(page);
                }
            }
            mi_free(p);
        }
    }
}
