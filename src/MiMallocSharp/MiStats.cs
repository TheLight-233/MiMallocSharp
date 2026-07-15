// C# translation of mimalloc/src/stats.c
using System;
using System.Threading;

namespace Mimalloc
{
    internal static unsafe class MiStats
    {
        // Global statistics - allocated in NativeMemory for pointer stability
        private static readonly mi_stats_t* _stats_ptr;
        public static ref mi_stats_t _mi_stats_main => ref *_stats_ptr;

        static MiStats()
        {
            _stats_ptr = (mi_stats_t*)MiPlatform.Zalloc((nuint)sizeof(mi_stats_t));
        }

        public static mi_stats_t* stats_ptr() => _stats_ptr;

        // Error/warning counters (from options.c globals)
        public static long error_count   = 0;
        public static long warning_count = 0;

        // -------------------------------------------------------
        // Core stat update (mirrors mi_stat_update in stats.c)
        // -------------------------------------------------------
        private static bool mi_is_in_main(void* stat)
        {
            return (byte*)stat >= (byte*)_stats_ptr && (byte*)stat < (byte*)_stats_ptr + sizeof(mi_stats_t);
        }

        private static void mi_stat_update(mi_stat_count_t* stat, long amount)
        {
            if (amount == 0) return;
            if (mi_is_in_main(stat))
            {
                // atomic update for abandoned-page stats
                long current = MiAtomic.addi64_relaxed(ref stat->current, amount);
                MiAtomic.maxi64_relaxed(ref stat->peak, current + amount);
                if (amount > 0) MiAtomic.addi64_relaxed(ref stat->total, amount);
            }
            else
            {
                // thread-local update
                stat->current += amount;
                if (stat->current > stat->peak) stat->peak = stat->current;
                if (amount > 0) stat->total += amount;
            }
        }

        public static void _mi_stat_counter_increase(mi_stat_counter_t* stat, nuint amount)
        {
            if (mi_is_in_main(stat))
                MiAtomic.addi64_relaxed(ref stat->total, (long)amount);
            else
                stat->total += (long)amount;
        }

        public static void _mi_stat_increase(mi_stat_count_t* stat, nuint amount)
            => mi_stat_update(stat, (long)amount);

        public static void _mi_stat_decrease(mi_stat_count_t* stat, nuint amount)
            => mi_stat_update(stat, -(long)amount);

        private static void mi_stat_adjust(mi_stat_count_t* stat, long amount)
        {
            if (amount == 0) return;
            if (mi_is_in_main(stat))
            {
                MiAtomic.addi64_relaxed(ref stat->current, amount);
                MiAtomic.addi64_relaxed(ref stat->total, amount);
            }
            else
            {
                stat->current += amount;
                stat->total   += amount;
            }
        }

        public static void _mi_stat_adjust_decrease(mi_stat_count_t* stat, nuint amount)
            => mi_stat_adjust(stat, -(long)amount);

        public static void _mi_stat_adjust_increase(mi_stat_count_t* stat, nuint amount)
            => mi_stat_adjust(stat, (long)amount);

        // -------------------------------------------------------
        // Merge thread-local stats into global (called on thread exit)
        // -------------------------------------------------------
        private static void mi_stat_add(mi_stat_count_t* dst, mi_stat_count_t* src, long unit)
        {
            if (dst == src) return;
            if (src->total > 0) MiAtomic.addi64_relaxed(ref dst->total, src->total * unit);
            MiAtomic.addi64_relaxed(ref dst->current, src->current * unit);
            MiAtomic.maxi64_relaxed(ref dst->peak, src->peak * unit);
        }

        private static void mi_stat_counter_add(mi_stat_counter_t* dst, mi_stat_counter_t* src, long unit)
        {
            if (dst == src) return;
            MiAtomic.addi64_relaxed(ref dst->total, src->total * unit);
        }

        public static void _mi_stats_done(mi_stats_t* stats)
        {
            // Merge thread-local stats into global
            mi_stats_t* main = _stats_ptr;
            {
                if (stats == main) return;
                mi_stat_add(&main->pages, &stats->pages, 1);
                mi_stat_add(&main->reserved, &stats->reserved, 1);
                mi_stat_add(&main->committed, &stats->committed, 1);
                mi_stat_add(&main->reset, &stats->reset, 1);
                mi_stat_add(&main->purged, &stats->purged, 1);
                mi_stat_add(&main->page_committed, &stats->page_committed, 1);
                mi_stat_add(&main->segments, &stats->segments, 1);
                mi_stat_add(&main->segments_abandoned, &stats->segments_abandoned, 1);
                mi_stat_add(&main->pages_abandoned, &stats->pages_abandoned, 1);
                mi_stat_add(&main->normal, &stats->normal, 1);
                mi_stat_add(&main->huge, &stats->huge, 1);
                mi_stat_add(&main->malloc_normal, &stats->malloc_normal, 1);
                mi_stat_add(&main->malloc_large, &stats->malloc_large, 1);
                mi_stat_add(&main->malloc_huge, &stats->malloc_huge, 1);
                mi_stat_add(&main->threads, &stats->threads, 1);
                mi_stat_add(&main->malloc_requested, &stats->malloc_requested, 1);
                mi_stat_add(&main->malloc_wasted, &stats->malloc_wasted, 1);
                mi_stat_counter_add(&main->malloc_normal_count, &stats->malloc_normal_count, 1);
                mi_stat_counter_add(&main->malloc_large_count, &stats->malloc_large_count, 1);
                mi_stat_counter_add(&main->malloc_huge_count, &stats->malloc_huge_count, 1);
                mi_stat_counter_add(&main->malloc_guarded_count, &stats->malloc_guarded_count, 1);
                mi_stat_counter_add(&main->arena_count, &stats->arena_count, 1);
                mi_stat_counter_add(&main->arena_crossover_count, &stats->arena_crossover_count, 1);
                mi_stat_counter_add(&main->arena_rollback_count, &stats->arena_rollback_count, 1);
            }
        }

        // -------------------------------------------------------
        // Reset all statistics (called after thread init)
        // -------------------------------------------------------
        public static void mi_stats_reset()
        {
            MiLibc.mi_memzero(_stats_ptr, (nuint)sizeof(mi_stats_t));
        }

        // -------------------------------------------------------
        // Print statistics (simplified, mirrors mi_stats_print)
        // -------------------------------------------------------
        public static void mi_stats_print(void* _out)
        {
            mi_stats_t* s = _stats_ptr;
            {
                MiLog.Stats("mimalloc statistics:");
                MiLog.Stats($"  committed: {s->committed.current,12:N0} (peak: {s->committed.peak:N0})");
                MiLog.Stats($"  reserved:  {s->reserved.current,12:N0} (peak: {s->reserved.peak:N0})");
                MiLog.Stats($"  segments:  {s->segments.current,12:N0}");
                MiLog.Stats($"  pages:     {s->pages.current,12:N0}");
                MiLog.Stats($"  threads:   {s->threads.current,12:N0}");
                MiLog.Stats($"  huge:      {s->huge.current,12:N0}");
                MiLog.Stats($"  malloc/n:  {s->malloc_normal.total,12:N0}");
            }
        }

        // -------------------------------------------------------
        // Helpers: heap stat increase/decrease macros from C
        // -------------------------------------------------------
        public static void mi_heap_stat_increase(mi_heap_t* heap, mi_stat_count_t* stat, nuint amount)
            => _mi_stat_increase(stat, amount);

        public static void mi_heap_stat_decrease(mi_heap_t* heap, mi_stat_count_t* stat, nuint amount)
            => _mi_stat_decrease(stat, amount);

        public static void mi_heap_stat_counter_increase(mi_heap_t* heap, mi_stat_counter_t* stat, nuint amount)
            => _mi_stat_counter_increase(stat, amount);

        // Get pointer to heap's stat by name (used by macros -> methods in C# translation)
        public static mi_stat_count_t* heap_stat_reserved(mi_heap_t* heap)   { fixed (mi_stats_t* s = &_mi_stats_main) return &s->reserved; }
        public static mi_stat_count_t* heap_stat_committed(mi_heap_t* heap)  { fixed (mi_stats_t* s = &_mi_stats_main) return &s->committed; }
        public static mi_stat_count_t* heap_stat_pages(mi_heap_t* heap)      { fixed (mi_stats_t* s = &_mi_stats_main) return &s->pages; }
        public static mi_stat_count_t* heap_stat_segments(mi_heap_t* heap)   { fixed (mi_stats_t* s = &_mi_stats_main) return &s->segments; }
        public static mi_stat_count_t* heap_stat_normal(mi_heap_t* heap)     { fixed (mi_stats_t* s = &_mi_stats_main) return &s->normal; }
        public static mi_stat_count_t* heap_stat_huge(mi_heap_t* heap)       { fixed (mi_stats_t* s = &_mi_stats_main) return &s->huge; }
        public static mi_stat_count_t* heap_stat_malloc_normal(mi_heap_t* h) { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_normal; }
        public static mi_stat_count_t* heap_stat_malloc_large(mi_heap_t* h)  { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_large; }
        public static mi_stat_count_t* heap_stat_malloc_huge(mi_heap_t* h)   { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_huge; }
        public static mi_stat_count_t* heap_stat_malloc_requested(mi_heap_t* h) { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_requested; }
        public static mi_stat_counter_t* heap_stat_malloc_normal_count(mi_heap_t* h) { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_normal_count; }
        public static mi_stat_counter_t* heap_stat_malloc_large_count(mi_heap_t* h)  { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_large_count; }
        public static mi_stat_counter_t* heap_stat_malloc_huge_count(mi_heap_t* h)   { fixed (mi_stats_t* s = &_mi_stats_main) return &s->malloc_huge_count; }
        public static mi_stat_counter_t* heap_stat_arena_rollback_count(mi_heap_t* h){ fixed (mi_stats_t* s = &_mi_stats_main) return &s->arena_rollback_count; }
        public static mi_stat_count_t* segments_ptr() { return &_stats_ptr->segments; }
    }
}
