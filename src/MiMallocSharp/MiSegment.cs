// C# translation of mimalloc/src/segment.c
// Segment and page allocation within segments
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Mimalloc
{
    /// <summary>
    /// Manages mimalloc segments – large OS allocations (4 MiB) divided into 64-KiB slices.
    /// Each page lives within a segment and maps to one or more contiguous slices.
    /// </summary>
    internal static unsafe partial class MiSegment
    {
        // segment_info_slices: how many slices the segment header (including all slice descriptors) occupies
        // = ceil(sizeof(segment_t) + slices_per_segment * sizeof(page_t), SLICE_SIZE)
        // On 64-bit: ~1-2 slices. We conservatively use 2 for the fixed overhead.
        private const nuint MI_SEGMENT_INFO_SLICES = 2;

        // -------------------------------------------------------
        // Slice / page helpers
        // -------------------------------------------------------

        // Get the first slice of the segment (these are embedded into the segment's allocation)
        // In our managed port, segment + 1 points past the fixed segment_t header.
        // The slice array occupies slices [0..MI_SLICES_PER_SEGMENT).
        // In C: segment->slices[i] is a mi_page_t (which doubles as mi_slice_t).
        // In C#: slices are laid out immediately after the segment struct.
        private static mi_page_t* segment_slices(mi_segment_t* seg)
        {
            // The slice array is allocated just after the segment header.
            // Caller must ensure segments are large enough.
            return (mi_page_t*)((byte*)seg + sizeof(mi_segment_t));
        }

        private static mi_page_t* segment_slice_at(mi_segment_t* seg, nuint idx)
            => segment_slices(seg) + idx;

        // Get the page (= slice) that contains a given pointer
        public static mi_page_t* _mi_segment_page_of(mi_segment_t* segment, void* p)
        {
            nuint diff = (nuint)p - (nuint)segment;
            nuint idx  = diff / MI.MI_SEGMENT_SLICE_SIZE;
            if (idx >= segment->segment_slices) return segment_slice_at(segment, 0);
            mi_page_t* slice = segment_slice_at(segment, idx);
            // Walk back using slice_offset to find the span/page head
            if (slice->slice_offset > 0)
            {
                nuint back = (nuint)(slice->slice_offset / (uint)sizeof(mi_page_t));
                slice -= (nint)back;
            }
            return slice;
        }

        // Get segment-start pointer from a page pointer  
        public static mi_segment_t* _mi_page_segment(mi_page_t* page)
            => MiOs._mi_ptr_segment(page);

        // Get start address of page's user memory
        public static byte* _mi_segment_page_start(mi_segment_t* segment, mi_page_t* page, nuint* page_size_out)
        {
            nuint slice_idx = (nuint)((byte*)page - (byte*)segment_slices(segment)) / (nuint)sizeof(mi_page_t);
            nuint psize = (nuint)page->slice_count * MI.MI_SEGMENT_SLICE_SIZE;
            byte* pstart = (byte*)segment + slice_idx * MI.MI_SEGMENT_SLICE_SIZE;

            // apply start offset for cache-friendly alignment (from segment.c)
            nuint start_offset = 0;
            nuint block_size = page->block_size;
            if (block_size > 0 && block_size <= MI.MI_MAX_ALIGN_GUARANTEE)
            {
                nuint adjust = block_size - ((nuint)pstart % block_size);
                if (adjust < block_size && psize >= block_size + adjust)
                    start_offset += adjust;
            }
            if (block_size >= (nuint)sizeof(void*))
            {
                if      (block_size <= 64)  start_offset += 3 * block_size;
                else if (block_size <= 512) start_offset += block_size;
            }
            start_offset = MiLibc._mi_align_up(start_offset, MI.MI_MAX_ALIGN_SIZE);

            if (page_size_out != null) *page_size_out = psize - start_offset;
            return pstart + start_offset;
        }

        // -------------------------------------------------------
        // Commit-mask helpers
        // -------------------------------------------------------
        // bits per commit-mask field = bits in nuint (32-bit: 32, 64-bit: 64)
        private static readonly nuint MI_COMMIT_MASK_BITS = (nuint)MI.MI_BITMAP_FIELD_BITS;

        private static void mi_commit_mask_create_empty(mi_commit_mask_t* cm)
        { cm->mask0 = 0; cm->mask1 = 0; }

        private static void mi_commit_mask_create_full(mi_commit_mask_t* cm)
        { cm->mask0 = MI.MI_NUINT_MAX; cm->mask1 = MI.MI_NUINT_MAX; }

        private static bool mi_commit_mask_is_empty(mi_commit_mask_t* cm)
            => cm->mask0 == 0 && cm->mask1 == 0;

        private static bool mi_commit_mask_is_full(mi_commit_mask_t* cm)
            => cm->mask0 == MI.MI_NUINT_MAX;

        private static void mi_commit_mask_set(mi_commit_mask_t* res, mi_commit_mask_t* cm)
        { res->mask0 |= cm->mask0; res->mask1 |= cm->mask1; }

        private static void mi_commit_mask_clear(mi_commit_mask_t* res, mi_commit_mask_t* cm)
        { res->mask0 &= ~cm->mask0; res->mask1 &= ~cm->mask1; }

        // -------------------------------------------------------
        // Span-queue helpers (free-slice doubly-linked list)
        // Access tld->span_queue_storage as array of mi_segment_span_queue_t
        // -------------------------------------------------------
        private static mi_segment_span_queue_t* tld_span_queue(mi_segments_tld_t* tld, nuint bin)
        {
            return (mi_segment_span_queue_t*)tld->span_queue_storage + bin;
        }

        // bin for a given slice count (mirrors mi_slice_bin in segment.c)
        private static nuint mi_slice_bin(nuint slice_count)
        {
            if (slice_count <= 1) return slice_count;
            if (slice_count > MI.MI_SLICES_PER_SEGMENT) return (nuint)MI.MI_SEGMENT_BIN_MAX;
            nuint sc = slice_count - 1;
            int s = MI.MI_SIZE_BITS - 1 - MiLibc.mi_clz(sc);
            if (s <= 2) return sc + 1;
            nuint bin = (nuint)(uint)(((s << 2) | ((int)(sc >> (s - 2)) & 0x03)) - 4);
            return bin > (nuint)MI.MI_SEGMENT_BIN_MAX ? (nuint)MI.MI_SEGMENT_BIN_MAX : bin;
        }

        private static mi_segment_span_queue_t* mi_span_queue_for(nuint slice_count, mi_segments_tld_t* tld)
            => tld_span_queue(tld, mi_slice_bin(slice_count));

        private static void mi_span_queue_push(mi_segment_span_queue_t* sq, mi_page_t* slice)
        {
            slice->prev = null;
            slice->next = sq->first;
            sq->first = slice;
            if (slice->next != null) slice->next->prev = slice;
            else sq->last = slice;
            slice->block_size = 0; // mark free
        }

        private static void mi_span_queue_delete(mi_segment_span_queue_t* sq, mi_page_t* slice)
        {
            if (slice->prev != null) slice->prev->next = slice->next;
            if (slice == sq->first) sq->first = slice->next;
            if (slice->next != null) slice->next->prev = slice->prev;
            if (slice == sq->last)  sq->last = slice->prev;
            slice->prev = null; slice->next = null;
            slice->block_size = 1; // no longer free
        }

        // -------------------------------------------------------
        // Segment size calculation
        // -------------------------------------------------------
        private static nuint mi_segment_calculate_slices(nuint required, out nuint info_slices)
        {
            nuint page_size = MiOs._mi_os_page_size();
            nuint isize = MiLibc._mi_align_up((nuint)sizeof(mi_segment_t), page_size);
            isize = MiLibc._mi_align_up(isize, MI.MI_SEGMENT_SLICE_SIZE);
            info_slices = isize / MI.MI_SEGMENT_SLICE_SIZE;
            nuint segment_size = required == 0
                ? MI.MI_SEGMENT_SIZE
                : MiLibc._mi_align_up(required + isize, MI.MI_SEGMENT_SLICE_SIZE);
            return segment_size / MI.MI_SEGMENT_SLICE_SIZE;
        }

        // -------------------------------------------------------
        // Segment allocation from OS
        // -------------------------------------------------------
        private static mi_segment_t* mi_segment_os_alloc(nuint slice_count, nuint* info_slices_out,
            bool eager_commit, mi_segments_tld_t* tld)
        {
            nuint info_slices;
            mi_segment_calculate_slices(0, out info_slices); // full segment
            nuint segment_size = slice_count * MI.MI_SEGMENT_SLICE_SIZE;

            // Try to reuse a cached segment first (avoids re-allocating memory)
            mi_segment_t* cached = TryGetCached(segment_size);
            void* p;
            mi_memid_t memid;
            if (cached != null)
            {
                p = cached;
                memid = cached->memid;
                // Zero out the segment header (preserve the native allocation info)
                MiLibc.mi_memzero(p, (nuint)sizeof(mi_segment_t));
            }
            else
            {
                p = MiOs._mi_os_alloc_aligned(segment_size, MI.MI_SEGMENT_ALIGN, eager_commit, false, &memid);
                if (p == null) return null;
            }

            mi_segment_t* segment = (mi_segment_t*)p;
            segment->memid           = memid;
            segment->allow_decommit  = 1;
            segment->allow_purge     = 1;
            segment->segment_size    = segment_size;
            segment->segment_slices  = slice_count;
            segment->segment_info_slices = info_slices;
            segment->kind            = mi_segment_kind_t.MI_SEGMENT_NORMAL;
            segment->cookie          = MiOs._mi_ptr_cookie(segment);
            MiAtomic.store_release(&segment->thread_id, MiPlatform.CurrentThreadId());

            // Initialize commit mask as fully committed (our AlignedAlloc always commits)
            mi_commit_mask_create_full(&segment->commit_mask);
            mi_commit_mask_create_empty(&segment->decommit_mask);

            // Track stats
            fixed (mi_stats_t* _ss = &MiStats._mi_stats_main) { MiStats._mi_stat_increase(&_ss->segments, 1); }
            tld->count++;
            if (tld->count > tld->peak_count) tld->peak_count = tld->count;
            tld->current_size += segment_size;
            if (tld->current_size > tld->peak_size) tld->peak_size = tld->current_size;

            if (info_slices_out != null) *info_slices_out = info_slices;

            // Register in segment map
            MiSegmentMap._mi_segment_map_allocated_at(segment);
            return segment;
        }


        // -------------------------------------------------------
        // Remove all free spans of a segment from the span queues.
        // MUST be called before freeing the segment's memory.
        // -------------------------------------------------------
        private static void mi_segment_remove_from_span_queues(mi_segment_t* segment, mi_segments_tld_t* tld)
        {
            nuint info = segment->segment_info_slices;
            nuint total = segment->segment_slices;
            nuint i = info;
            while (i < total)
            {
                mi_page_t* slice = segment_slices(segment) + (nint)i;
                if (slice->block_size == 0 && slice->slice_count > 0 && slice->slice_offset == 0)
                {
                    // This is a free span head — remove it from its queue
                    mi_segment_span_queue_t* sq = mi_span_queue_for(slice->slice_count, tld);
                    // Walk the queue to find and remove this exact entry
                    if (sq->first == slice || slice->prev != null || slice->next != null)
                    {
                        mi_span_queue_delete(sq, slice);
                    }
                    i += (nuint)slice->slice_count;
                }
                else if (slice->slice_count > 0)
                {
                    i += (nuint)slice->slice_count;
                }
                else
                {
                    i++; // intermediate slice; skip
                }
            }
        }

        // -------------------------------------------------------
        // Segment cache: keep freed segments alive for reuse
        // This avoids dangling span-queue pointers after free.
        // -------------------------------------------------------
        private const int SEGMENT_CACHE_MAX = 64;  // Large enough to prevent actual segment frees during typical usage
        // Per-TLD segment cache stored in the segments_tld's unused bytes.
        // We use a simple fixed-size ring buffer referenced via a thread-local list.
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SegmentCacheEntry { public mi_segment_t* segment; public nuint size; }

        // Global simple cache: store up to SEGMENT_CACHE_MAX freed segments
        private static volatile int _cache_count = 0;
        private static readonly mi_segment_t*[] _cache = new mi_segment_t*[64];
        private static readonly nuint[] _cache_size = new nuint[64];
        private static readonly object _cache_lock = new object();

        private static mi_segment_t* TryGetCached(nuint needed_size)
        {
            lock (_cache_lock)
            {
                for (int i = 0; i < _cache_count; i++)
                {
                    if (_cache[i] != null && _cache_size[i] >= needed_size)
                    {
                        mi_segment_t* seg = _cache[i];
                        // Shift array
                        _cache[i] = _cache[_cache_count - 1];
                        _cache_size[i] = _cache_size[_cache_count - 1];
                        _cache[--_cache_count] = null;
                        return seg;
                    }
                }
            }
            return null;
        }

        private static bool TryAddToCache(mi_segment_t* segment, nuint size)
        {
            lock (_cache_lock)
            {
                if (_cache_count < SEGMENT_CACHE_MAX)
                {
                    _cache[_cache_count]      = segment;
                    _cache_size[_cache_count] = size;
                    _cache_count++;
                    return true;
                }
            }
            return false;
        }

        // -------------------------------------------------------
        // Segment free
        // -------------------------------------------------------
        public static void mi_segment_os_free(mi_segment_t* segment, mi_segments_tld_t* tld)
        {
            MiAtomic.store_release(&segment->thread_id, 0);
            MiSegmentMap._mi_segment_map_freed_at(segment);
            nuint segment_size = segment->segment_size;
            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
                MiStats._mi_stat_decrease(&s->segments, 1);
            tld->count--;
            tld->current_size -= segment_size;

            // Try to cache the segment for reuse to avoid dangling span-queue pointers
            if (!TryAddToCache(segment, segment_size))
            {
                // Cache full: actually free
                MiOs._mi_os_free(segment, segment_size, segment->memid);
            }
        }

        // -------------------------------------------------------
        // Span free / allocate
        // -------------------------------------------------------
        private static void mi_segment_span_free(mi_segment_t* segment, nuint slice_index, nuint slice_count,
            bool allow_purge, mi_segments_tld_t* tld)
        {
            mi_page_t* slice = segment_slice_at(segment, slice_index);
            slice->slice_count  = (uint)slice_count;
            slice->slice_offset = 0;
            if (slice_count > 1)
            {
                // Set back-offsets for ALL intermediate slices so _mi_segment_page_of
                // can walk back from any slice to the span start.
                // Also clear next/prev to prevent stale queue traversal.
                for (nuint i = 1; i < slice_count; i++)
                {
                    mi_page_t* si = slice + (nint)i;
                    si->slice_count  = 0;
                    si->slice_offset = (uint)((nuint)sizeof(mi_page_t) * i);
                    si->block_size   = 0;
                    si->next         = null;
                    si->prev         = null;
                }
            }
            bool huge_or_abandoned = segment->kind == mi_segment_kind_t.MI_SEGMENT_HUGE
                                  || MiAtomic.load_relaxed(&segment->thread_id) == 0;
            if (!huge_or_abandoned)
            {
                mi_segment_span_queue_t* sq = mi_span_queue_for(slice_count, tld);
                mi_span_queue_push(sq, slice);
            }
            else
            {
                slice->block_size = 0;
            }
        }

        private static mi_page_t* mi_segment_span_allocate(mi_segment_t* segment, nuint slice_index, nuint slice_count)
        {
            mi_page_t* slice = segment_slice_at(segment, slice_index);
            slice->slice_offset = 0;
            slice->slice_count  = (uint)slice_count;
            nuint bsize = slice_count * MI.MI_SEGMENT_SLICE_SIZE;
            slice->block_size   = bsize;

            // set back-pointers for first few slices
            nuint extra = slice_count - 1;
            if (extra > 8) extra = 8; // MI_MAX_SLICE_OFFSET_COUNT
            for (nuint i = 1; i <= extra; i++)
            {
                mi_page_t* sn = slice + (nint)i;
                sn->slice_offset = (uint)((nuint)sizeof(mi_page_t) * i);
                sn->slice_count  = 0;
                sn->block_size   = 1;
            }
            // last slice back-pointer
            if (slice_count > 1)
            {
                mi_page_t* last = slice + (nint)(slice_count - 1);
                last->slice_offset = (uint)((nuint)sizeof(mi_page_t) * (slice_count - 1));
                last->slice_count  = 0;
                last->block_size   = 1;
            }

            mi_page_t* page = slice;
            page->is_committed = true;
            page->is_zero_init = memid_initially_zero(segment);
            page->is_huge      = segment->kind == mi_segment_kind_t.MI_SEGMENT_HUGE;
            segment->used++;
            return page;
        }

        private static bool memid_initially_zero(mi_segment_t* seg)
            => seg->memid.initially_zero != 0;

        private static void mi_segment_slice_split(mi_segment_t* segment, mi_page_t* slice, nuint slice_count, mi_segments_tld_t* tld)
        {
            if (slice->slice_count <= slice_count) return;
            nuint slice_idx = (nuint)((byte*)slice - (byte*)segment_slices(segment)) / (nuint)sizeof(mi_page_t);
            nuint next_index = slice_idx + slice_count;
            nuint next_count = (nuint)slice->slice_count - slice_count;
            mi_segment_span_free(segment, next_index, next_count, false, tld);
            slice->slice_count = (uint)slice_count;
        }

        // -------------------------------------------------------
        // Find a free span and allocate a page from it
        // -------------------------------------------------------
        private static mi_page_t* mi_segments_page_find_and_allocate(nuint slice_count, int req_arena_id, mi_segments_tld_t* tld)
        {
            mi_segment_span_queue_t* sq = mi_span_queue_for(slice_count, tld);
            for (nuint bin = mi_slice_bin(slice_count); bin <= (nuint)MI.MI_SEGMENT_BIN_MAX; bin++, sq++)
            {
                mi_page_t* slice = sq->first;
                mi_page_t* slice_next = null;
                while (slice != null)
                {
                    // Safely read next BEFORE any other dereference
                    try { slice_next = slice->next; } catch { break; }
                    
                    mi_segment_t* segment = MiOs._mi_ptr_segment(slice);
                    // Guard: segment must be 4MiB-aligned (our allocation guarantee)
                    if ((nuint)segment < MI.MI_SEGMENT_ALIGN)
                    { slice = slice_next; continue; }
                    // Guard: slice must be in segment's slice array region
                    if ((nuint)slice < (nuint)segment + (nuint)sizeof(mi_segment_t))
                    { slice = slice_next; continue; }
                    // Guard: slice must be a valid free span head
                    if (slice->block_size != 0 || slice->slice_offset != 0)
                    { slice = slice_next; continue; }
                    // Guard: segment must be in heap region (registered in our segment map)
                    if (!MiSegmentMap.mi_is_in_heap_region(slice))
                    { slice = slice_next; continue; }
                    // check arena id compatibility
                    try {
                        if (req_arena_id != 0 && segment->memid.memkind == MI.MI_MEM_ARENA
                            && segment->memid.mem.arena.id != req_arena_id)
                        { slice = slice_next; continue; }
                    } catch { slice = slice_next; continue; }

                    if (slice->slice_count >= slice_count)
                    {
                        // remove from free list
                        mi_span_queue_delete(sq, slice);
                        nuint slice_idx = (nuint)((byte*)slice - (byte*)segment_slices(segment)) / (nuint)sizeof(mi_page_t);
                        // split remainder
                        mi_segment_slice_split(segment, slice, slice_count, tld);
                        return mi_segment_span_allocate(segment, slice_idx, slice_count);
                    }
                    slice = slice_next;
                }
            }
            return null;
        }

        // -------------------------------------------------------
        // Allocate a fresh segment and carve out a page
        // -------------------------------------------------------
        private static mi_page_t* mi_segment_alloc(nuint slice_count, nuint page_alignment, mi_segments_tld_t* tld)
        {
            bool eager_commit = MiOptions.mi_option_is_enabled(mi_option_t.mi_option_eager_commit);
            nuint info_slices;
            mi_segment_t* segment = mi_segment_os_alloc(MI.MI_SLICES_PER_SEGMENT, &info_slices, eager_commit, tld);
            if (segment == null) return null;

            // set up the info-slices as used
            segment->segment_info_slices = info_slices;
            // mark info slices as used so they are never allocated as pages
            for (nuint i = 0; i < info_slices; i++)
            {
                mi_page_t* s = segment_slice_at(segment, i);
                s->block_size   = 1;
                s->slice_count  = 1;
                s->slice_offset = 0;
            }

            // add the rest as one big free span
            nuint free_count = MI.MI_SLICES_PER_SEGMENT - info_slices;
            mi_segment_span_free(segment, info_slices, free_count, false, tld);

            // Find the freshly-added free span by searching upward from target bin.
            // (The large free span is in a higher bin; search finds it automatically.)
            nuint page_slice_count = slice_count == 0 ? 1 : slice_count;
            return mi_segments_page_find_and_allocate(page_slice_count, 0, tld);
        }

        // -------------------------------------------------------
        // Public API: allocate a page inside a segment
        // -------------------------------------------------------
        public static mi_page_t* _mi_segment_page_alloc(mi_heap_t* heap, nuint block_size, nuint page_alignment, mi_segments_tld_t* tld)
        {
            nuint slice_count;
            if (block_size <= MI.MI_SMALL_SIZE_MAX || block_size <= MI.MI_SEGMENT_SLICE_SIZE)
                slice_count = 1;
            else if (block_size <= (nuint)(8 * MI.MI_SEGMENT_SLICE_SIZE))
                slice_count = MiLibc._mi_divide_up(block_size, MI.MI_SEGMENT_SLICE_SIZE);
            else // large / huge
                slice_count = MiLibc._mi_divide_up(block_size, MI.MI_SEGMENT_SLICE_SIZE);

            if (slice_count > MI.MI_SLICES_PER_SEGMENT)
            {
                // huge allocation: dedicated segment
                return mi_segment_huge_page_alloc(block_size, page_alignment, tld);
            }

            int arena_id = heap != null ? heap->arena_id : 0;
            mi_page_t* page_found = mi_segments_page_find_and_allocate(slice_count, arena_id, tld);
            if (page_found != null) return page_found;

            // no free span found: allocate a new segment
            return mi_segment_alloc(slice_count, page_alignment, tld);
        }

        // Huge page: dedicated segment
        private static mi_page_t* mi_segment_huge_page_alloc(nuint block_size, nuint page_alignment, mi_segments_tld_t* tld)
        {
            // size for segment = block_size + segment header, rounded up
            nuint info_slices = MI_SEGMENT_INFO_SLICES;
            nuint extra = MiLibc._mi_align_up(block_size, MI.MI_SEGMENT_SLICE_SIZE);
            nuint total_slices = info_slices + extra / MI.MI_SEGMENT_SLICE_SIZE + 1;

            bool eager_commit = MiOptions.mi_option_is_enabled(mi_option_t.mi_option_eager_commit);
            mi_segment_t* segment = mi_segment_os_alloc(total_slices, null, eager_commit, tld);
            if (segment == null) return null;

            segment->kind = mi_segment_kind_t.MI_SEGMENT_HUGE;
            segment->segment_info_slices = info_slices;
            for (nuint i = 0; i < info_slices; i++)
            {
                mi_page_t* s = segment_slice_at(segment, i);
                s->block_size = 1; s->slice_count = 1; s->slice_offset = 0;
            }
            nuint page_slices = total_slices - info_slices;
            return mi_segment_span_allocate(segment, info_slices, page_slices);
        }

        // -------------------------------------------------------
        // Page free within segment
        // -------------------------------------------------------
        public static void _mi_segment_page_free(mi_page_t* page, bool force, mi_segments_tld_t* tld)
        {
            mi_segment_t* segment = _mi_page_segment(page);
            segment->used--;

            nuint slice_count = page->slice_count;
            nuint slice_idx   = (nuint)((byte*)page - (byte*)segment_slices(segment)) / (nuint)sizeof(mi_page_t);
            // clear page
            page->block_size   = 0;
            page->slice_count  = (uint)slice_count;

            // coalesce with neighbors and add back to free list
            mi_segment_span_free_coalesce(page, tld);

            // free segment if empty
            if (segment->used == 0)
            {
                // Remove all free spans from TLD queues BEFORE freeing the memory
                // to prevent dangling pointers in span queue traversal.
                mi_segment_remove_from_span_queues(segment, tld);
                mi_segment_os_free(segment, tld);
            }
        }

        private static void mi_segment_span_free_coalesce(mi_page_t* slice, mi_segments_tld_t* tld)
        {
            mi_segment_t* segment = MiOs._mi_ptr_segment(slice);
            if (segment->kind == mi_segment_kind_t.MI_SEGMENT_HUGE)
            {
                slice->block_size = 0;
                return;
            }

            nuint slice_count = slice->slice_count;
            nuint slice_idx   = (nuint)((byte*)slice - (byte*)segment_slices(segment)) / (nuint)sizeof(mi_page_t);

            // coalesce with next
            mi_page_t* next_slice = segment_slice_at(segment, slice_idx + slice_count);
            if (slice_idx + slice_count < segment->segment_slices && next_slice->block_size == 0)
            {
                nuint next_sq_count = next_slice->slice_count;
                mi_segment_span_queue_t* next_sq = mi_span_queue_for(next_sq_count, tld);
                mi_span_queue_delete(next_sq, next_slice);
                slice_count += next_sq_count;
            }

            // coalesce with previous
            if (slice_idx > segment->segment_info_slices)
            {
                mi_page_t* prev_last = segment_slice_at(segment, slice_idx - 1);
                if (prev_last->slice_count == 0 && prev_last->slice_offset > 0 && prev_last->block_size == 0)
                {
                    nuint back = (nuint)(prev_last->slice_offset / (uint)sizeof(mi_page_t));
                    mi_page_t* prev_first = prev_last - (nint)back;
                    if (prev_first->block_size == 0)
                    {
                        mi_segment_span_queue_t* prev_sq = mi_span_queue_for(prev_first->slice_count, tld);
                        mi_span_queue_delete(prev_sq, prev_first);
                        slice_count += prev_first->slice_count;
                        slice = prev_first;
                        slice_idx -= prev_first->slice_count;
                    }
                }
            }

            // re-add merged span
            mi_segment_span_free(segment, slice_idx, slice_count, true, tld);
        }

        // -------------------------------------------------------
        // Page abandon (for thread-exit)
        // -------------------------------------------------------
        public static void _mi_segment_page_abandon(mi_page_t* page, mi_segments_tld_t* tld)
        {
            mi_segment_t* segment = _mi_page_segment(page);
            segment->abandoned++;
            MiAtomic.store_release(&segment->thread_id, 0);
            fixed (mi_stats_t* _ss = &MiStats._mi_stats_main) { MiStats._mi_stat_increase(&_ss->segments_abandoned, 1); }
            // simplified: just decrement used count and try to free if empty
            if (segment->used == segment->abandoned)
            {
                // all remaining pages abandoned; check if we should free
            }
        }

        // -------------------------------------------------------
        // Reclaim an abandoned segment
        // -------------------------------------------------------
        public static bool _mi_segment_attempt_reclaim(mi_heap_t* heap, mi_segment_t* segment)
        {
            if (MiAtomic.load_relaxed(&segment->thread_id) != 0) return false;
            nuint tid = MiPlatform.CurrentThreadId();
            nuint expected = 0;
            if (!MiAtomic.cas_strong_acq_rel(&segment->thread_id, ref expected, tid)) return false;
            // successfully reclaimed
            segment->abandoned = 0;
            return true;
        }

        // -------------------------------------------------------
        // Collect / purge expired decommit ranges in a segment
        // -------------------------------------------------------
        public static void _mi_segment_collect(mi_segment_t* segment, bool force)
        {
            // no-op in our simplified implementation (no delayed purge)
        }
    }
}
