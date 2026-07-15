// C# translation of mimalloc/src/init.c
// Process / thread / heap initialization
using System;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiInit
    {
        // -------------------------------------------------------
        // Globally allocated structs (NativeMemory so GC never moves them)
        // -------------------------------------------------------
        private static readonly mi_page_t*   _s_page_empty;
        private static readonly mi_heap_t*   _s_heap_empty;
        private static readonly mi_heap_t*   _s_heap_main;
        private static readonly mi_tld_t*    _s_tld_main;
        private static readonly mi_subproc_t* _s_subproc_default;

        internal static mi_page_t*  _mi_page_empty      => _s_page_empty;
        internal static mi_heap_t*  _mi_heap_empty_ptr  => _s_heap_empty;
        internal static mi_heap_t*  _mi_heap_main_ptr   => _s_heap_main;
        internal static mi_tld_t*   _mi_tld_main_ptr    => _s_tld_main;

        // Allocate native memory in static constructor
        static MiInit()
        {
            _s_page_empty      = (mi_page_t*)MiPlatform.Zalloc((nuint)sizeof(mi_page_t));
            _s_heap_empty      = (mi_heap_t*)MiPlatform.Zalloc((nuint)sizeof(mi_heap_t));
            _s_heap_main       = (mi_heap_t*)MiPlatform.Zalloc((nuint)sizeof(mi_heap_t));
            _s_tld_main        = (mi_tld_t*)MiPlatform.Zalloc((nuint)sizeof(mi_tld_t));
            _s_subproc_default = (mi_subproc_t*)MiPlatform.Zalloc((nuint)sizeof(mi_subproc_t));
        }

        // -------------------------------------------------------
        // Thread-local heap (per-thread default heap)
        // -------------------------------------------------------
        [ThreadStatic] private static unsafe mi_heap_t* _tls_heap;

        internal static mi_heap_t* mi_prim_get_default_heap()
        {
            mi_heap_t* h = _tls_heap;
            if (h == null)
            {
                if (!_process_initialized)
                    return _mi_heap_empty_ptr;
                h = _mi_heap_empty_ptr;
            }
            return h;
        }

        internal static void _mi_heap_set_default_direct(mi_heap_t* heap)
        {
            _tls_heap = heap;
        }

        // -------------------------------------------------------
        // Process init flags
        // -------------------------------------------------------
        internal static bool _mi_process_is_initialized = false;
        private static bool _process_initialized = false;
        private static int  _process_init_once = 0;
        private static bool _os_preloading = true;

        internal static bool _mi_preloading() => _os_preloading;
        internal static bool _mi_is_main_thread()
            => _s_heap_main->thread_id == 0 || _s_heap_main->thread_id == MiPlatform.CurrentThreadId();

        // -------------------------------------------------------
        // Heap initialization (from _mi_heap_init in heap.c)
        // -------------------------------------------------------
        private static void _mi_heap_init_empty(mi_heap_t* heap)
        {
            MiLibc.mi_memzero(heap, (nuint)sizeof(mi_heap_t));
            // set up pages_storage with empty queues
            // We need to initialize each page_queue.block_size from the QNULL table.
            // The block sizes come from init.c's MI_PAGE_QUEUES_EMPTY macro.
            InitPageQueues(heap);
            // initialize pages_free_direct to point to the empty page
            mi_page_t** direct = MiPage.heap_pages_free_direct(heap);
            for (int i = 0; i < MI.MI_PAGES_DIRECT; i++)
                direct[i] = _mi_page_empty;
        }

        // Block sizes for bins 0..MI_BIN_FULL (75 entries), from init.c QNULL table
        private static readonly nuint[] BinBlockSizes = new nuint[75]
        {
            1 * 8, // bin 0
            1*8, 2*8, 3*8, 4*8, 5*8, 6*8, 7*8, 8*8,                               // 8 bins
            10*8,12*8,14*8,16*8,20*8,24*8,28*8,32*8,                               // 8 bins
            40*8,48*8,56*8,64*8,80*8,96*8,112*8,128*8,                             // 8 bins
            160*8,192*8,224*8,256*8,320*8,384*8,448*8,512*8,                       // 8 bins
            640*8,768*8,896*8,1024*8,1280*8,1536*8,1792*8,2048*8,                  // 8 bins
            2560*8,3072*8,3584*8,4096*8,5120*8,6144*8,7168*8,8192*8,               // 8 bins
            10240*8,12288*8,14336*8,16384*8,20480*8,24576*8,28672*8,32768*8,       // 8 bins
            40960*8,49152*8,57344*8,65536*8,81920*8,98304*8,114688*8,131072*8,     // 8 bins
            163840*8,196608*8,229376*8,262144*8,327680*8,393216*8,458752*8,524288*8,// 8 bins
            (MI.MI_MEDIUM_OBJ_WSIZE_MAX + 1) * 8,  // bin 73 = MI_BIN_HUGE
            (MI.MI_MEDIUM_OBJ_WSIZE_MAX + 2) * 8,  // bin 74 = MI_BIN_FULL
        };

        private static void InitPageQueues(mi_heap_t* heap)
        {
            for (int i = 0; i < 75; i++)
            {
                mi_page_queue_t* pq = MiPage.heap_page_queue(heap, (nuint)i);
                pq->first = null;
                pq->last  = null;
                pq->block_size = (uint)i < (uint)BinBlockSizes.Length ? BinBlockSizes[i] : 0;
            }
        }

        public static void _mi_heap_init(mi_heap_t* heap, mi_tld_t* tld, int arena_id, bool noreclaim, byte tag)
        {
            _mi_heap_init_empty(heap);
            heap->tld        = tld;
            heap->thread_id  = MiPlatform.CurrentThreadId();
            heap->arena_id   = arena_id;
            heap->no_reclaim = noreclaim ? (byte)1 : (byte)0;
            heap->tag        = tag;

            if (heap == tld->heap_backing)
                MiRandom._mi_random_init(&heap->random);
            else
                MiRandom._mi_random_split(&tld->heap_backing->random, &heap->random);

            heap->cookie = MiRandom._mi_random_next(&heap->random) | 1;
            heap->key0   = MiRandom._mi_random_next(&heap->random);
            heap->key1   = MiRandom._mi_random_next(&heap->random);

            // push onto thread heap list
            heap->next = tld->heaps;
            tld->heaps = heap;
        }

        // -------------------------------------------------------
        // TLD initialization
        // -------------------------------------------------------
        private static void _mi_tld_init(mi_tld_t* tld, mi_heap_t* heap)
        {
            MiLibc.mi_memzero(tld, (nuint)sizeof(mi_tld_t));
            tld->heap_backing = heap;
            tld->heaps = heap;
            // initialize segments tld
            tld->segments.subproc = _s_subproc_default;
            // stats pointer: use MiStats._mi_stats_main which is a native-allocated struct
            tld->segments.stats = MiStats.stats_ptr();

            // init span queues in tld->segments.span_queue_storage
            // 35 queues with slice counts from SQNULL table
            nuint[] sqSizes = new nuint[]
            {
                1, 1,2,3,4,5,6,7,10, // 8
                12,14,16,20,24,28,32,40, // 16
                48,56,64,80,96,112,128,160, // 24
                192,224,256,320,384,448,512,640, // 32
                768,896,1024 // 35
            };
            for (int i = 0; i < MI.MI_SEGMENT_BIN_MAX && i < sqSizes.Length; i++)
            {
                mi_segment_span_queue_t* sq = (mi_segment_span_queue_t*)tld->segments.span_queue_storage + i;
                sq->first = null; sq->last = null;
                sq->slice_count = sqSizes[i];
            }
        }

        // -------------------------------------------------------
        // Main heap initialization
        // -------------------------------------------------------
        private static void mi_heap_main_init()
        {
            if (_s_heap_main->cookie != 0) return; // already done

            mi_heap_t*  heap = _mi_heap_main_ptr;
            mi_tld_t*   tld  = _mi_tld_main_ptr;

            _mi_tld_init(tld, heap);
            _mi_heap_init(heap, tld, 0, false, 0);
            heap->thread_id = MiPlatform.CurrentThreadId();
            _mi_heap_set_default_direct(heap);
        }

        // -------------------------------------------------------
        // Thread heap init
        // -------------------------------------------------------
        private static bool _mi_thread_heap_init()
        {
            if (_tls_heap != null && _tls_heap != _mi_heap_empty_ptr)
                return true; // already initialized

            // Allocate thread data (heap + tld together)
            nuint td_size = (nuint)sizeof(mi_thread_data_t);
            mi_memid_t memid;
            mi_thread_data_t* td = (mi_thread_data_t*)MiOs._mi_os_alloc(td_size, &memid);
            if (td == null) { _mi_heap_set_default_direct(_mi_heap_empty_ptr); return false; }
            MiLibc.mi_memzero(td, td_size);
            td->memid = memid;

            mi_heap_t* heap = &td->heap;
            mi_tld_t*  tld  = &td->tld;
            _mi_tld_init(tld, heap);
            _mi_heap_init(heap, tld, 0, false, 0);
            _mi_heap_set_default_direct(heap);

            MiStats._mi_stat_increase(MiStats.heap_stat_segments(_mi_heap_main_ptr), 0); // no-op stat
            return false; // first init
        }

        // -------------------------------------------------------
        // Thread done (cleanup)
        // -------------------------------------------------------
        public static void _mi_thread_heap_done(mi_heap_t* heap)
        {
            if (!mi_heap_is_initialized(heap)) return;
            if (heap == _mi_heap_main_ptr) return;
            // collect abandoned / delayed frees
            _mi_heap_set_default_direct(_mi_is_main_thread() ? _mi_heap_main_ptr : _mi_heap_empty_ptr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_heap_is_initialized(mi_heap_t* heap)
            => heap != null && heap != _mi_heap_empty_ptr && heap->cookie != 0;

        // -------------------------------------------------------
        // Thread / process init  (mirrors mi_thread_init / mi_process_init)
        // -------------------------------------------------------
        public static void mi_thread_init()
        {
            mi_process_init();
            if (!_mi_thread_heap_init()) return; // already initialized
        }

        public static void mi_process_init()
        {
            if (!MiAtomic.once(ref _process_init_once)) return;
            _process_initialized = true;
            _mi_process_is_initialized = true;
            _os_preloading = false;
            MiOs._mi_os_init();
            MiOptions._mi_options_init();
            mi_heap_main_init();
            MiStats.mi_stats_reset();
        }

        public static void mi_process_done()
        {
            if (!_mi_process_is_initialized) return;
            mi_heap_t* heap = mi_prim_get_default_heap();
            if (heap != null)
                mi_heap_collect(heap, true);
            if (MiOptions.mi_option_is_enabled(mi_option_t.mi_option_show_stats))
                MiStats.mi_stats_print(null);
        }

        // -------------------------------------------------------
        // Heap creation / deletion (from heap.c)
        // -------------------------------------------------------
        public static mi_heap_t* mi_heap_get_default()
        {
            mi_thread_init();
            return mi_prim_get_default_heap();
        }

        public static mi_heap_t* mi_heap_get_backing()
        {
            mi_heap_t* heap = mi_heap_get_default();
            return (heap->tld->heap_backing != null ? heap->tld->heap_backing : heap);
        }

        public static mi_heap_t* mi_heap_new()
        {
            mi_heap_t* bheap = mi_heap_get_backing();
            nuint size = (nuint)sizeof(mi_heap_t);
            mi_memid_t mid;
            mi_heap_t* heap = (mi_heap_t*)MiOs._mi_os_alloc(size, &mid);
            if (heap == null) return null;
            _mi_heap_init(heap, bheap->tld, 0, true, 0);
            return heap;
        }

        public static void mi_heap_delete(mi_heap_t* heap)
        {
            if (!mi_heap_is_initialized(heap)) return;
            mi_heap_t* bheap = heap->tld->heap_backing;
            if (bheap != heap) mi_heap_absorb(bheap, heap);
            else MiHeap.mi_heap_collect_ex(heap, 2 /* MI_ABANDON */);
            heap->page_count = 0;
            // free the heap struct (if it was separately allocated)
            mi_heap_free(heap);
        }

        private static void mi_heap_absorb(mi_heap_t* dst, mi_heap_t* src)
        {
            if (src == null || src->page_count == 0) return;
            MiPage._mi_heap_delayed_free_partial(src);
            for (nuint i = 0; i <= (nuint)MI.MI_BIN_FULL; i++)
            {
                mi_page_queue_t* pq     = MiPage.heap_page_queue(dst, i);
                mi_page_queue_t* append = MiPage.heap_page_queue(src, i);
                nuint n = MiPage._mi_page_queue_append(dst, pq, append);
                dst->page_count += n;
                src->page_count -= n;
            }
            MiPage._mi_heap_delayed_free_all(src);
        }

        private static void mi_heap_free(mi_heap_t* heap)
        {
            if (heap == null || heap == _mi_heap_main_ptr) return;
            // remove from thread list
            mi_heap_t* prev = null;
            mi_heap_t* curr = heap->tld->heaps;
            while (curr != null && curr != heap) { prev = curr; curr = curr->next; }
            if (curr == heap)
            {
                if (prev != null) prev->next = heap->next;
                else heap->tld->heaps = heap->next;
            }
            // the heap struct itself came from OS alloc
            MiOs._mi_os_free(heap, (nuint)sizeof(mi_heap_t), default);
        }

        public static void mi_heap_destroy(mi_heap_t* heap)
        {
            if (!mi_heap_is_initialized(heap)) return;
            MiHeap.mi_heap_visit_pages_destroy(heap);
            mi_heap_free(heap);
        }

        // -------------------------------------------------------
        // GC helpers
        // -------------------------------------------------------
        public static void mi_heap_collect(mi_heap_t* heap, bool force)
        {
            MiHeap.mi_heap_collect_ex(heap, force ? 2 : 0);
        }

        public static void mi_collect(bool force)
            => mi_heap_collect(mi_prim_get_default_heap(), force);

        // -------------------------------------------------------
        // mi_is_redirected (always false in managed port)
        // -------------------------------------------------------
        public static bool mi_is_redirected() => false;

        // -------------------------------------------------------
        // Thread data cache (no-op in managed port)
        // -------------------------------------------------------
        public static void _mi_thread_data_collect() { }

        // -------------------------------------------------------
        // Random helper
        // -------------------------------------------------------
        public static nuint _mi_heap_random_next(mi_heap_t* heap)
            => MiRandom._mi_random_next(&heap->random);

        // -------------------------------------------------------
        // mi_version
        // -------------------------------------------------------
        public static int mi_version() => MI.MI_MALLOC_VERSION;
    }
}
