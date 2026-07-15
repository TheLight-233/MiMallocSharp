// C# translation of mimalloc/src/page.c and page-queue.c
// Page allocation, free-list management, and page queues
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiPage
    {
        // -------------------------------------------------------
        // Bin calculation (from page-queue.c mi_bin())
        // -------------------------------------------------------
        public static nuint _mi_bin(nuint size)
        {
            nuint wsize = MiLibc._mi_wsize_from_size(size);
            if (wsize <= 8) return wsize == 0 ? 1 : wsize;
            if (wsize > MI.MI_MEDIUM_OBJ_WSIZE_MAX) return (nuint)MI.MI_BIN_HUGE;
            wsize--;
            int b = MI.MI_SIZE_BITS - 1 - MiLibc.mi_clz(wsize);
            nuint bin = (nuint)(uint)(((b << 2) | ((int)(wsize >> (b - 2)) & 0x03)) - 3);
            return bin > (nuint)MI.MI_BIN_HUGE ? (nuint)MI.MI_BIN_HUGE : bin;
        }

        // -------------------------------------------------------
        // Page queue accessors (stored in mi_heap_t.pages_storage)
        // -------------------------------------------------------
        public static mi_page_queue_t* heap_page_queue(mi_heap_t* heap, nuint bin)
        {
            // pages_storage is fixed byte array; size = 75 * 24 bytes per queue
            return (mi_page_queue_t*)heap->pages_storage + bin;
        }

        public static mi_page_queue_t* mi_heap_page_queue_of(mi_heap_t* heap, mi_page_t* page)
        {
            nuint bin;
            if (page->in_full) bin = (nuint)MI.MI_BIN_FULL;
            else if (page->is_huge) bin = (nuint)MI.MI_BIN_HUGE;
            else bin = _mi_bin(page->block_size);
            return heap_page_queue(heap, bin);
        }

        public static mi_page_queue_t* mi_page_queue_of(mi_page_t* page)
        {
            mi_heap_t* heap = mi_page_heap(page);
            if (heap == null) return null; // stale page (already freed) - caller must handle
            return mi_heap_page_queue_of(heap, page);
        }

        // -------------------------------------------------------
        // Pages-free-direct (fast small lookup)
        // -------------------------------------------------------
        public static mi_page_t** heap_pages_free_direct(mi_heap_t* heap)
            => (mi_page_t**)heap->pages_free_direct_storage;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_page_t* _mi_heap_get_free_small_page(mi_heap_t* heap, nuint size)
        {
            nuint idx = MiLibc._mi_wsize_from_size(size);
            if (idx >= (nuint)MI.MI_PAGES_DIRECT) idx = (nuint)MI.MI_PAGES_DIRECT - 1;
            mi_page_t** direct = heap_pages_free_direct(heap);
            return direct[idx];
        }

        private static void mi_heap_queue_first_update(mi_heap_t* heap, mi_page_queue_t* pq)
        {
            nuint size = pq->block_size;
            if (size > MI.MI_SMALL_SIZE_MAX) return;
            mi_page_t* page = pq->first != null ? pq->first : mi_page_empty();
            nuint idx = MiLibc._mi_wsize_from_size(size);
            mi_page_t** direct = heap_pages_free_direct(heap);
            if (direct[idx] == page) return;
            nuint start = idx <= 1 ? 0 : idx;
            for (nuint sz = start; sz <= idx; sz++)
                direct[sz] = page;
        }

        // Sentinel empty page
        private static mi_page_t* mi_page_empty()
        {
            // Return the globally empty page pointer from MiInit
            return MiInit._mi_page_empty;
        }

        // -------------------------------------------------------
        // Heap / xheap (atomic heap pointer stored in page->xheap)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_heap_t* mi_page_heap(mi_page_t* page)
            => (mi_heap_t*)MiAtomic.load_acquire(&page->xheap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_page_set_heap(mi_page_t* page, mi_heap_t* heap)
            => MiAtomic.store_release(&page->xheap, (nuint)heap);

        // -------------------------------------------------------
        // Thread-free list helpers
        // -------------------------------------------------------
        // xthread_free layout: bits[1:0] = mi_delayed_t tag, bits[N:2] = pointer >> 2
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static mi_delayed_t mi_tf_delayed(nuint tf) => (mi_delayed_t)(tf & 3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static mi_block_t* mi_tf_block(nuint tf) => (mi_block_t*)(tf & ~(nuint)3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint mi_tf_set_delayed(nuint tf, mi_delayed_t d)
            => (tf & ~(nuint)3) | (nuint)d;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint mi_tf_set_block(nuint tf, mi_block_t* block)
            => (nuint)block | (tf & 3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_block_t* mi_page_thread_free(mi_page_t* page)
            => mi_tf_block(MiAtomic.load_relaxed(&page->xthread_free));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_delayed_t mi_page_thread_free_flag(mi_page_t* page)
            => mi_tf_delayed(MiAtomic.load_relaxed(&page->xthread_free));

        // -------------------------------------------------------
        // Free-list block encoding (no XOR in release mode)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_block_t* mi_block_next(mi_page_t* page, mi_block_t* block)
            => (mi_block_t*)block->next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_block_set_next(mi_page_t* page, mi_block_t* block, mi_block_t* next)
            => block->next = (nuint)next;

        // With heap key encoding (for thread-delayed list)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static mi_block_t* mi_block_nextx(mi_heap_t* heap, mi_block_t* block, nuint key0, nuint key1)
        {
            nuint encoded = block->next;
            return (mi_block_t*)(encoded ^ key0 ^ key1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_block_set_nextx(mi_heap_t* heap, mi_block_t* block, mi_block_t* next, nuint key0, nuint key1)
            => block->next = (nuint)next ^ key0 ^ key1;

        // -------------------------------------------------------
        // Page property helpers
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_page_block_size(mi_page_t* page) => page->block_size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_page_usable_block_size(mi_page_t* page) => page->block_size; // no padding in release

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_page_all_free(mi_page_t* page)
            => page->used == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_page_immediate_available(mi_page_t* page)
            => page->free != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_page_is_in_full(mi_page_t* page)
            => page->in_full;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_page_set_in_full(mi_page_t* page, bool v)
            => page->in_full = v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_page_has_aligned(mi_page_t* page) => page->has_aligned;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_page_set_has_aligned(mi_page_t* page, bool v) => page->has_aligned = v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_page_is_huge(mi_page_t* page) => page->is_huge;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte* mi_page_start(mi_page_t* page) => page->page_start;

        // -------------------------------------------------------
        // Queue operations (from page-queue.c)
        // -------------------------------------------------------
        public static void mi_page_queue_remove(mi_page_queue_t* queue, mi_page_t* page)
        {
            mi_heap_t* heap = mi_page_heap(page);
            if (page->prev != null) page->prev->next = page->next;
            if (page->next != null) page->next->prev = page->prev;
            if (page == queue->last)  queue->last  = page->prev;
            if (page == queue->first)
            {
                queue->first = page->next;
                mi_heap_queue_first_update(heap, queue);
            }
            heap->page_count--;
            page->next = null; page->prev = null;
            mi_page_set_in_full(page, false);
        }

        public static void mi_page_queue_push(mi_heap_t* heap, mi_page_queue_t* queue, mi_page_t* page)
        {
            mi_page_set_in_full(page, mi_page_queue_is_full(queue));
            page->next = queue->first;
            page->prev = null;
            if (queue->first != null)
            {
                queue->first->prev = page;
                queue->first = page;
            }
            else
            {
                queue->first = queue->last = page;
            }
            mi_heap_queue_first_update(heap, queue);
            heap->page_count++;
        }

        private static bool mi_page_queue_is_huge(mi_page_queue_t* pq)
            => pq->block_size == (MI.MI_MEDIUM_OBJ_SIZE_MAX + (nuint)sizeof(nuint));

        private static bool mi_page_queue_is_full(mi_page_queue_t* pq)
            => pq->block_size == (MI.MI_MEDIUM_OBJ_SIZE_MAX + 2 * (nuint)sizeof(nuint));

        private static bool mi_page_queue_is_special(mi_page_queue_t* pq)
            => pq->block_size > MI.MI_MEDIUM_OBJ_SIZE_MAX;

        public static void mi_page_queue_enqueue_from(mi_page_queue_t* to, mi_page_queue_t* from, mi_page_t* page)
        {
            mi_heap_t* heap = mi_page_heap(page);
            // remove from 'from'
            if (page->prev != null) page->prev->next = page->next;
            if (page->next != null) page->next->prev = page->prev;
            if (page == from->last)  from->last  = page->prev;
            if (page == from->first)
            {
                from->first = page->next;
                mi_heap_queue_first_update(heap, from);
            }
            // insert at end of 'to'
            page->prev = to->last; page->next = null;
            if (to->last != null) { to->last->next = page; to->last = page; }
            else { to->first = page; to->last = page; mi_heap_queue_first_update(heap, to); }
            mi_page_set_in_full(page, mi_page_queue_is_full(to));
        }

        // -------------------------------------------------------
        // Page initialization
        // -------------------------------------------------------
        public static void mi_page_init(mi_heap_t* heap, mi_page_t* page, nuint size, mi_tld_t* tld)
        {
            mi_segment_t* segment = MiSegment._mi_page_segment(page);
            // IMPORTANT: set block_size FIRST so _mi_segment_page_start can use it
            page->block_size       = size == 0 ? 1 : size;
            nuint page_size;
            byte* start = MiSegment._mi_segment_page_start(segment, page, &page_size);
            page->page_start       = start;
            page->free             = null;
            page->local_free       = null;
            page->used             = 0;
            page->free_is_zero     = page->is_zero_init;
            MiAtomic.store_release(&page->xheap, (nuint)heap);
            MiAtomic.store_release(&page->xthread_free, (nuint)mi_delayed_t.MI_USE_DELAYED_FREE);
            page->key0 = heap->key0;
            page->key1 = heap->key1;
            // compute capacity
            if (size == 0 || page_size == 0)
            {
                page->capacity = 0; page->reserved = 0;
            }
            else
            {
                nuint cap = page_size / size;
                if (cap > 0xFFFF) cap = 0xFFFF;
                page->capacity = (ushort)cap;
                page->reserved = (ushort)cap;
            }
            // block_size_shift
            if (size != 0 && MiLibc._mi_is_power_of_two(size))
            {
                int shift = MiLibc.mi_ctz(size);
                page->block_size_shift = (byte)(shift < 256 ? shift : 0);
            }
            else page->block_size_shift = 0;

            mi_page_extend_free(heap, page);
        }

        // Extend the free list by linking all blocks in the committed portion
        private static void mi_page_extend_free(mi_heap_t* heap, mi_page_t* page)
        {
            if (page->capacity == 0 || page->block_size == 0) return;
            nuint bsize = page->block_size;
            byte* start = page->page_start;
            nuint cap = page->capacity;

            // link all blocks into free list
            mi_block_t* prev = null;
            for (nuint i = 0; i < cap; i++)
            {
                mi_block_t* block = (mi_block_t*)(start + i * bsize);
                mi_block_set_next(page, block, prev);
                prev = block;
            }
            page->free = prev; // list head (points to last-linked = first block)
            // actually build forward list:
            mi_page_build_free_list(page);
        }

        private static void mi_page_build_free_list(mi_page_t* page)
        {
            if (page->block_size == 0 || page->capacity == 0) return;
            nuint bsize = page->block_size;
            byte* start = page->page_start;
            nuint cap = page->capacity;
            // forward linked list: block[i]->next = block[i+1], last->next = null
            for (nuint i = 0; i + 1 < cap; i++)
            {
                mi_block_t* cur  = (mi_block_t*)(start + i * bsize);
                mi_block_t* next = (mi_block_t*)(start + (i + 1) * bsize);
                mi_block_set_next(page, cur, next);
            }
            mi_block_t* last = (mi_block_t*)(start + (cap - 1) * bsize);
            mi_block_set_next(page, last, null);
            page->free = (mi_block_t*)start;
        }

        // -------------------------------------------------------
        // Thread-free collect
        // -------------------------------------------------------
        private static void _mi_page_thread_free_collect(mi_page_t* page)
        {
            mi_block_t* head;
            nuint tfreex;
            nuint tfree = MiAtomic.load_acquire(&page->xthread_free);
            do
            {
                head   = mi_tf_block(tfree);
                tfreex = mi_tf_set_block(tfree, null);
            } while (!MiAtomic.cas_weak_release(&page->xthread_free, ref tfree, tfreex));

            if (head == null) return;

            // find tail and count
            nuint max_count = page->capacity;
            nuint count = 1;
            mi_block_t* tail = head;
            mi_block_t* next;
            while ((next = mi_block_next(page, tail)) != null && count <= max_count)
            { count++; tail = next; }

            if (count > max_count)
            {
                MiLibc._mi_error_message(14, "corrupted thread-free list\n");
                return;
            }
            mi_block_set_next(page, tail, page->local_free);
            page->local_free = head;
            page->used -= (uint)count;
        }

        public static void _mi_page_free_collect(mi_page_t* page, bool force)
        {
            if (force || mi_page_thread_free(page) != null)
                _mi_page_thread_free_collect(page);
            if (page->local_free != null)
            {
                if (page->free == null) { page->free = page->local_free; page->local_free = null; page->free_is_zero = false; }
                else if (force)
                {
                    mi_block_t* tail = page->local_free;
                    mi_block_t* nx;
                    while ((nx = mi_block_next(page, tail)) != null) tail = nx;
                    mi_block_set_next(page, tail, page->free);
                    page->free = page->local_free;
                    page->local_free = null;
                    page->free_is_zero = false;
                }
            }
        }

        // -------------------------------------------------------
        // Delayed-free control
        // -------------------------------------------------------
        public static void _mi_page_use_delayed_free(mi_page_t* page, mi_delayed_t delay, bool override_never)
        {
            while (!_mi_page_try_use_delayed_free(page, delay, override_never))
                MiAtomic.yield();
        }

        public static bool _mi_page_try_use_delayed_free(mi_page_t* page, mi_delayed_t delay, bool override_never)
        {
            nuint tfree = MiAtomic.load_acquire(&page->xthread_free);
            nuint tfreex;
            int yield_count = 0;
            do
            {
                mi_delayed_t old_delay = mi_tf_delayed(tfree);
                tfreex = mi_tf_set_delayed(tfree, delay);
                if (old_delay == mi_delayed_t.MI_DELAYED_FREEING)
                {
                    if (yield_count >= 4) return false;
                    yield_count++;
                    MiAtomic.yield();
                    tfree = MiAtomic.load_acquire(&page->xthread_free);
                    continue;
                }
                if (delay == old_delay) return true;
                if (!override_never && old_delay == mi_delayed_t.MI_NEVER_DELAYED_FREE) return true;
            } while (!MiAtomic.cas_weak_release(&page->xthread_free, ref tfree, tfreex));
            return true;
        }

        // -------------------------------------------------------
        // Unalign a block pointer (find original block start)
        // -------------------------------------------------------
        public static mi_block_t* _mi_page_ptr_unalign(mi_page_t* page, void* p)
        {
            nuint diff = (nuint)p - (nuint)page->page_start;
            nuint adjust;
            if (page->block_size_shift != 0)
                adjust = diff & (((nuint)1 << page->block_size_shift) - 1);
            else
                adjust = diff % page->block_size;
            return (mi_block_t*)((nuint)p - adjust);
        }

        // -------------------------------------------------------
        // Usable size
        // -------------------------------------------------------
        public static nuint _mi_usable_size(void* p, mi_page_t* page)
            => mi_page_usable_block_size(page);

        // -------------------------------------------------------
        // Page retire / free / abandon
        // -------------------------------------------------------
        public static void _mi_page_retire(mi_page_t* page)
        {
            mi_page_set_has_aligned(page, false);
            mi_page_queue_t* pq = mi_page_queue_of(page);
            if (!mi_page_queue_is_special(pq))
            {
                // don't retire immediately if it's the only page in the queue
                if (pq->last == page && pq->first == page)
                {
                    page->retire_expire = (byte)(page->block_size <= MI.MI_SMALL_SIZE_MAX ? 16 : 4);
                    mi_heap_t* heap = mi_page_heap(page);
                    nuint index = (nuint)((byte*)pq - (byte*)heap_page_queue(heap, 0)) / (nuint)(uint)sizeof(mi_page_queue_t);
                    if (index < heap->page_retired_min) heap->page_retired_min = index;
                    if (index > heap->page_retired_max) heap->page_retired_max = index;
                    return;
                }
            }
            _mi_page_free(page, pq, false);
        }

        public static void _mi_page_free(mi_page_t* page, mi_page_queue_t* pq, bool force)
        {
            mi_page_set_has_aligned(page, false);
            mi_heap_t* heap = mi_page_heap(page);
            mi_page_queue_remove(pq, page);
            mi_page_set_heap(page, null);
            MiSegment._mi_segment_page_free(page, force, &heap->tld->segments);
        }

        public static void _mi_page_unfull(mi_page_t* page)
        {
            if (!mi_page_is_in_full(page)) return;
            mi_heap_t* heap = mi_page_heap(page);
            mi_page_queue_t* pqfull = heap_page_queue(heap, (nuint)MI.MI_BIN_FULL);
            mi_page_set_in_full(page, false);
            mi_page_queue_t* pq = mi_heap_page_queue_of(heap, page);
            mi_page_set_in_full(page, true);
            mi_page_queue_enqueue_from(pq, pqfull, page);
        }

        public static void _mi_page_abandon(mi_page_t* page, mi_page_queue_t* pq)
        {
            mi_heap_t* heap = mi_page_heap(page);
            mi_page_queue_remove(pq, page);
            mi_page_set_heap(page, null);
            MiSegment._mi_segment_page_abandon(page, &heap->tld->segments);
        }

        // -------------------------------------------------------
        // Collect retired pages on a heap
        // -------------------------------------------------------
        public static void _mi_heap_collect_retired(mi_heap_t* heap, bool force)
        {
            nuint min = heap->page_retired_min;
            nuint max = heap->page_retired_max;
            if (min > max) return;
            heap->page_retired_min = (nuint)MI.MI_BIN_FULL;
            heap->page_retired_max = 0;
            for (nuint bin = min; bin <= max; bin++)
            {
                mi_page_queue_t* pq = heap_page_queue(heap, bin);
                mi_page_t* page = pq->first;
                if (page == null || !mi_page_all_free(page)) continue;
                if (page->retire_expire == 0 || force)
                    _mi_page_free(page, pq, force);
                else
                {
                    page->retire_expire--;
                    if (bin < heap->page_retired_min) heap->page_retired_min = bin;
                    if (bin > heap->page_retired_max) heap->page_retired_max = bin;
                }
            }
        }

        // -------------------------------------------------------
        // Find free page or allocate fresh one (core of malloc_generic)
        // -------------------------------------------------------
        public static mi_page_t* _mi_malloc_generic(mi_heap_t* heap, nuint size, bool zero, nuint huge_alignment, nuint* usable)
        {
            // handle size == 0 or overflow
            if (size == 0) size = (nuint)sizeof(void*);
            if (size > MI.MI_MAX_ALLOC_SIZE) return null;

            // find right bin
            nuint bin   = (huge_alignment > 0 || size > MI.MI_MEDIUM_OBJ_SIZE_MAX)
                        ? (nuint)MI.MI_BIN_HUGE : _mi_bin(size);
            mi_page_queue_t* pq = heap_page_queue(heap, bin);

            // search existing pages in the queue
            mi_page_t* page = pq->first;
            while (page != null)
            {
                _mi_page_free_collect(page, false);
                if (mi_page_immediate_available(page))
                {
                    if (usable != null) *usable = mi_page_usable_block_size(page);
                    return page;
                }
                page = page->next;
            }

            // no free page found: allocate fresh
            // Use the bin's block_size (not the raw requested size) to initialize the page correctly
            nuint alloc_size = (pq != null && !mi_page_queue_is_huge(pq) && !mi_page_queue_is_full(pq))
                             ? pq->block_size : size;
            page = mi_page_fresh_alloc(heap, pq, alloc_size, huge_alignment);
            if (page == null) return null;
            if (usable != null) *usable = mi_page_usable_block_size(page);
            return page;
        }

        private static mi_page_t* mi_page_fresh_alloc(mi_heap_t* heap, mi_page_queue_t* pq, nuint block_size, nuint page_alignment)
        {
            mi_page_t* page = MiSegment._mi_segment_page_alloc(heap, block_size, page_alignment, &heap->tld->segments);
            if (page == null) return null;

            nuint full_block_size = pq != null && !mi_page_is_huge(page) ? block_size : page->block_size;
            mi_page_init(heap, page, full_block_size, heap->tld);

            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
                MiStats._mi_stat_increase(&s->pages, 1);

            if (pq != null) mi_page_queue_push(heap, pq, page);
            return page;
        }

        // -------------------------------------------------------
        // Validate pointer → page (used by realloc / usable_size)
        // -------------------------------------------------------
        public static mi_page_t* mi_validate_ptr_page(void* p, string msg)
        {
            if (p == null) return null;
            mi_segment_t* segment = MiOs._mi_ptr_segment(p);
            if (segment == null) return null;
            return MiSegment._mi_segment_page_of(segment, p);
        }

        // -------------------------------------------------------
        // Delayed free helpers for heap
        // -------------------------------------------------------
        public static void _mi_heap_delayed_free_all(mi_heap_t* heap)
        {
            while (!_mi_heap_delayed_free_partial(heap))
                MiAtomic.yield();
        }

        public static bool _mi_heap_delayed_free_partial(mi_heap_t* heap)
        {
            // atomically take over the delayed free list
            nuint block_n = MiAtomic.load_relaxed((nuint*)&heap->thread_delayed_free);
            mi_block_t* block = (mi_block_t*)block_n;
            while (block != null)
            {
                nuint expected = (nuint)block;
                if (MiAtomic.cas_strong_acq_rel((nuint*)&heap->thread_delayed_free, ref expected, 0)) break;
                block = (mi_block_t*)expected;
            }

            bool all_freed = true;
            while (block != null)
            {
                mi_block_t* next = mi_block_nextx(heap, block, heap->key0, heap->key1);
                if (!_mi_free_delayed_block(block))
                {
                    all_freed = false;
                    // re-insert
                    nuint dfree = MiAtomic.load_relaxed((nuint*)&heap->thread_delayed_free);
                    do { mi_block_set_nextx(heap, block, (mi_block_t*)dfree, heap->key0, heap->key1); }
                    while (!MiAtomic.cas_strong_acq_rel((nuint*)&heap->thread_delayed_free, ref dfree, (nuint)block));
                }
                block = next;
            }
            return all_freed;
        }

        public static bool _mi_free_delayed_block(mi_block_t* block)
        {
            mi_segment_t* segment = MiOs._mi_ptr_segment(block);
            mi_page_t* page = MiSegment._mi_segment_page_of(segment, block);
            if (!_mi_page_try_use_delayed_free(page, mi_delayed_t.MI_USE_DELAYED_FREE, false)) return false;
            _mi_page_free_collect(page, false);
            MiFree.mi_free_block_local(page, block, false, true);
            return true;
        }

        public static nuint _mi_page_queue_append(mi_heap_t* heap, mi_page_queue_t* pq, mi_page_queue_t* append)
        {
            if (append->first == null) return 0;
            nuint count = 0;
            for (mi_page_t* p = append->first; p != null; p = p->next)
            {
                MiAtomic.store_release(&p->xheap, (nuint)heap);
                _mi_page_use_delayed_free(p, mi_delayed_t.MI_USE_DELAYED_FREE, false);
                count++;
            }
            if (pq->last == null)
            {
                pq->first = append->first; pq->last = append->last;
                mi_heap_queue_first_update(heap, pq);
            }
            else
            {
                pq->last->next = append->first;
                append->first->prev = pq->last;
                pq->last = append->last;
            }
            return count;
        }
    }
}
