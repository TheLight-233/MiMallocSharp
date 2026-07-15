// C# translation of mimalloc/src/options.c
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiOptions
    {
        // -------------------------------------------------------
        // Option table (mirrors static mi_option_desc_t options[])
        // -------------------------------------------------------
        private static readonly long[] _optionValues = new long[(int)mi_option_t._mi_option_last];
        private static readonly mi_init_t[] _optionInit = new mi_init_t[(int)mi_option_t._mi_option_last];
        private static bool _initialized = false;

        // Default values from options.c
        private static void _SetDefaults()
        {
            Set(mi_option_t.mi_option_show_errors,     0);
            Set(mi_option_t.mi_option_show_stats,      0);
            Set(mi_option_t.mi_option_verbose,         0);
            Set(mi_option_t.mi_option_eager_commit,    1);
            Set(mi_option_t.mi_option_arena_eager_commit, 2);
            Set(mi_option_t.mi_option_purge_decommits, 1);
            Set(mi_option_t.mi_option_allow_large_os_pages, 0);
            Set(mi_option_t.mi_option_reserve_huge_os_pages, 0);
            Set(mi_option_t.mi_option_reserve_huge_os_pages_at, -1);
            Set(mi_option_t.mi_option_reserve_os_memory, 0);
            Set(mi_option_t.mi_option_abandoned_page_purge, 1);
            Set(mi_option_t.mi_option_eager_commit_delay, 1);
            Set(mi_option_t.mi_option_purge_delay, 10);   // 10ms
            Set(mi_option_t.mi_option_use_numa_nodes, 0);
            Set(mi_option_t.mi_option_disallow_os_alloc, 0);
            Set(mi_option_t.mi_option_os_tag, 100);
            Set(mi_option_t.mi_option_max_errors, 16);
            Set(mi_option_t.mi_option_max_warnings, 16);
            Set(mi_option_t.mi_option_max_segment_reclaim, 2);
            Set(mi_option_t.mi_option_destroy_on_exit, 0);
            Set(mi_option_t.mi_option_arena_reserve,   1024 * 1024); // 1GiB in KiB
            Set(mi_option_t.mi_option_arena_purge_mult, 1);
            Set(mi_option_t.mi_option_disallow_arena_alloc, 0);
            Set(mi_option_t.mi_option_retry_on_oom, 0);
            Set(mi_option_t.mi_option_visit_abandoned, 0);
            Set(mi_option_t.mi_option_guarded_min, 0);
            Set(mi_option_t.mi_option_guarded_max, 0);
            Set(mi_option_t.mi_option_guarded_precise, 0);
            Set(mi_option_t.mi_option_guarded_sample_rate, 0);
        }

        private static void Set(mi_option_t opt, long value)
        {
            int idx = (int)opt;
            if (_optionInit[idx] != mi_init_t.INITIALIZED)
            {
                _optionValues[idx] = value;
                _optionInit[idx] = mi_init_t.DEFAULTED;
            }
        }

        // -------------------------------------------------------
        // Public API (mirrors mimalloc options API)
        // -------------------------------------------------------
        public static void _mi_options_init()
        {
            if (_initialized) return;
            _initialized = true;
            _SetDefaults();
            // Read environment variables
            for (int i = 0; i < (int)mi_option_t._mi_option_last; i++)
                _ReadEnv((mi_option_t)i);
        }

        private static readonly string[] _optionNames = new string[]
        {
            "show_errors","show_stats","verbose","eager_commit","arena_eager_commit",
            "purge_decommits","allow_large_os_pages","reserve_huge_os_pages",
            "reserve_huge_os_pages_at","reserve_os_memory","deprecated_segment_cache",
            "deprecated_page_reset","abandoned_page_purge","deprecated_segment_reset",
            "eager_commit_delay","purge_delay","use_numa_nodes","disallow_os_alloc",
            "os_tag","max_errors","max_warnings","max_segment_reclaim","destroy_on_exit",
            "arena_reserve","arena_purge_mult","disallow_arena_alloc","retry_on_oom",
            "visit_abandoned","guarded_min","guarded_max","guarded_precise","guarded_sample_rate"
        };

        private static void _ReadEnv(mi_option_t opt)
        {
            int idx = (int)opt;
            string name = idx < _optionNames.Length ? _optionNames[idx] : opt.ToString();
            string? val = Environment.GetEnvironmentVariable("mimalloc_" + name)
                       ?? Environment.GetEnvironmentVariable("MIMALLOC_" + name);
            if (val == null) return;
            string upper = val.Trim().ToUpperInvariant();
            if (upper == "1" || upper == "TRUE" || upper == "YES" || upper == "ON")
            { _optionValues[idx] = 1; _optionInit[idx] = mi_init_t.INITIALIZED; }
            else if (upper == "0" || upper == "FALSE" || upper == "NO" || upper == "OFF")
            { _optionValues[idx] = 0; _optionInit[idx] = mi_init_t.INITIALIZED; }
            else if (long.TryParse(val, out long lv))
            { _optionValues[idx] = lv; _optionInit[idx] = mi_init_t.INITIALIZED; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long mi_option_get(mi_option_t opt)
        {
            if (!_initialized) _mi_options_init();
            int idx = (int)opt;
            return (uint)idx < (uint)_optionValues.Length ? _optionValues[idx] : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long _mi_option_get_fast(mi_option_t opt)
        {
            int idx = (int)opt;
            return (uint)idx < (uint)_optionValues.Length ? _optionValues[idx] : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_option_is_enabled(mi_option_t opt) => mi_option_get(opt) != 0;

        public static void mi_option_set(mi_option_t opt, long value)
        {
            int idx = (int)opt;
            if ((uint)idx >= (uint)_optionValues.Length) return;
            _optionValues[idx] = value;
            _optionInit[idx] = mi_init_t.INITIALIZED;
        }

        public static void mi_option_set_default(mi_option_t opt, long value)
        {
            int idx = (int)opt;
            if ((uint)idx >= (uint)_optionValues.Length) return;
            if (_optionInit[idx] != mi_init_t.INITIALIZED)
                _optionValues[idx] = value;
        }

        public static long mi_option_get_clamp(mi_option_t opt, long min, long max)
        {
            long v = mi_option_get(opt);
            return v < min ? min : (v > max ? max : v);
        }

        public static nuint mi_option_get_size(mi_option_t opt)
        {
            long x = mi_option_get(opt);
            return x < 0 ? 0 : (nuint)(ulong)x;
        }

        // Purge extend delay
        public static long mi_option_purge_extend_delay()
        {
            long d = mi_option_get(mi_option_t.mi_option_purge_delay);
            return d <= 0 ? 0 : (d / 8 < 1 ? 1 : d / 8);
        }

        // Abandoned reclaim on free option (not in enum above, treat as 1)
        public static long mi_option_abandoned_reclaim_on_free() => 1;
    }
}
