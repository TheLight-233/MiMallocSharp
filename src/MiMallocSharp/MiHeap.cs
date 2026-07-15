// C# translation of mimalloc/src/heap.c (collection and visit helpers)
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiHeap
    {
        // mi_collect_t enum values
        internal const int MI_NORMAL  = 0;
        internal const int MI_FORCE   = 1;
        internal const int MI_ABANDON = 2;

        // -------------------------------------------------------
        // Visit all pages in a heap
        // -------------------------------------------------------
        public delegate bool HeapPageVisitorFun(mi_heap_t* heap, mi_page_queue_t* pq, mi_page_t* page, void* arg1, void* arg2);

        public static bool mi_heap_visit_pages(mi_heap_t* heap, HeapPageVisitorFun fn, void* arg1, void* arg2)
        {
            if (heap == null || heap->page_count == 0) return true;
            for (nuint i = 0; i <= (nuint)MI.MI_BIN_FULL; i++)
            {
                mi_page_queue_t* pq = MiPage.heap_page_queue(heap, i);
                mi_page_t* page = pq->first;
                while (page != null)
                {
                    mi_page_t* next = page->next;
                    if (!fn(heap, pq, page, arg1, arg2)) return false;
                    page = next;
                }
            }
            return true;
        }

        // -------------------------------------------------------
        // Collect pages
        // -------------------------------------------------------
        private static bool mi_heap_page_collect(mi_heap_t* heap, mi_page_queue_t* pq, mi_page_t* page, void* arg_collect, void* arg2)
        {
            int collect = *(int*)arg_collect;
            MiPage._mi_page_free_collect(page, collect >= MI_FORCE);
            if (collect == MI_FORCE)
            {
                mi_segment_t* segment = MiSegment._mi_page_segment(page);
                MiSegment._mi_segment_collect(segment, true);
            }
            if (MiPage.mi_page_all_free(page))
                MiPage._mi_page_free(page, pq, collect >= MI_FORCE);
            else if (collect == MI_ABANDON)
                MiPage._mi_page_abandon(page, pq);
            return true;
        }

        private static bool mi_heap_page_never_delayed_free(mi_heap_t* heap, mi_page_queue_t* pq, mi_page_t* page, void* arg1, void* arg2)
        {
            MiPage._mi_page_use_delayed_free(page, mi_delayed_t.MI_NEVER_DELAYED_FREE, false);
            return true;
        }

        public static void mi_heap_collect_ex(mi_heap_t* heap, int collect)
        {
            if (heap == null || !MiInit.mi_heap_is_initialized(heap)) return;
            bool force = collect >= MI_FORCE;

            if (collect == MI_ABANDON)
                mi_heap_visit_pages(heap, mi_heap_page_never_delayed_free, null, null);

            MiPage._mi_heap_delayed_free_all(heap);
            MiPage._mi_heap_collect_retired(heap, force);

            int c = collect;
            mi_heap_visit_pages(heap, mi_heap_page_collect, &c, null);
        }

        // -------------------------------------------------------
        // Destroy all pages (used by mi_heap_destroy)
        // -------------------------------------------------------
        private static bool mi_heap_page_destroy(mi_heap_t* heap, mi_page_queue_t* pq, mi_page_t* page, void* a1, void* a2)
        {
            MiPage._mi_page_use_delayed_free(page, mi_delayed_t.MI_NEVER_DELAYED_FREE, false);
            page->used = 0;
            page->next = null; page->prev = null;
            MiSegment._mi_segment_page_free(page, false, &heap->tld->segments);
            return true;
        }

        public static void mi_heap_visit_pages_destroy(mi_heap_t* heap)
        {
            mi_heap_visit_pages(heap, mi_heap_page_destroy, null, null);
            // reset pages_free_direct
            mi_page_t** direct = MiPage.heap_pages_free_direct(heap);
            for (int i = 0; i < MI.MI_PAGES_DIRECT; i++)
                direct[i] = MiInit._mi_page_empty;
            heap->page_count = 0;
        }

        // -------------------------------------------------------
        // Heap absorb (for heap_delete)
        // -------------------------------------------------------
        public static bool mi_heap_is_backing(mi_heap_t* heap)
            => heap != null && heap->tld != null && heap->tld->heap_backing == heap;

        // -------------------------------------------------------
        // Usable size helper (from alloc.c)
        // -------------------------------------------------------
        public static nuint mi_usable_size(void* p)
        {
            if (p == null) return 0;
            mi_segment_t* segment = MiOs._mi_ptr_segment(p);
            if (segment == null) return 0;
            mi_page_t* page = MiSegment._mi_segment_page_of(segment, p);
            return MiPage.mi_page_usable_block_size(page);
        }

        public static nuint mi_good_size(nuint size)
        {
            if (size <= MI.MI_MEDIUM_OBJ_SIZE_MAX)
            {
                nuint bin = MiPage._mi_bin(size + (nuint)MI.MI_PADDING_SIZE);
                return MiPage.heap_page_queue(MiInit.mi_prim_get_default_heap(), bin)->block_size;
            }
            return MiLibc._mi_align_up(size + (nuint)MI.MI_PADDING_SIZE, MiOs._mi_os_page_size());
        }
    }
}
