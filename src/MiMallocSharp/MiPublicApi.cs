// mimalloc C# public API wrapper
// Provides a safe, managed interface over the unsafe core.
// All sizes are in bytes. Returns IntPtr for pointer handles.
using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    /// <summary>
    /// mimalloc - a compact, fast, general-purpose allocator.
    /// C# translation of Microsoft Research's mimalloc v2.
    ///
    /// .NET 6+      : uses NativeMemory.AlignedAlloc / AlignedFree
    /// netstandard2.1: uses Marshal.AllocHGlobal with manual alignment
    /// </summary>
    public static unsafe class Mi
    {
        // -------------------------------------------------------
        // Initialization (call once before first use, or let
        // the allocator auto-initialize on first allocation)
        // -------------------------------------------------------
        public static void ProcessInit() => MiInit.mi_process_init();
        public static void ProcessDone() => MiInit.mi_process_done();
        public static void ThreadInit()  => MiInit.mi_thread_init();
        public static void Collect(bool force = false) => MiInit.mi_collect(force);
        public static int  Version()     => MiInit.mi_version();

        // -------------------------------------------------------
        // Core allocation
        // -------------------------------------------------------
        /// <summary>Allocate <paramref name="size"/> bytes. Returns IntPtr.Zero on failure.</summary>
        public static IntPtr Malloc(nuint size)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_malloc(size);
        }

        /// <summary>Allocate <paramref name="size"/> zero-initialized bytes.</summary>
        public static IntPtr Zalloc(nuint size)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_zalloc(size);
        }

        /// <summary>Allocate <paramref name="count"/> × <paramref name="elemSize"/> zero-initialized bytes.</summary>
        public static IntPtr Calloc(nuint count, nuint elemSize)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_calloc(count, elemSize);
        }

        /// <summary>Reallocate <paramref name="ptr"/> to <paramref name="newSize"/> bytes.</summary>
        public static IntPtr Realloc(IntPtr ptr, nuint newSize)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_realloc((void*)ptr, newSize);
        }

        /// <summary>Free a pointer allocated by Mi.*.</summary>
        public static void Free(IntPtr ptr)
        {
            MiFree.mi_free((void*)ptr);
        }

        // -------------------------------------------------------
        // Aligned allocation
        // -------------------------------------------------------
        /// <summary>Allocate with at least <paramref name="alignment"/> byte alignment (must be power-of-two).</summary>
        public static IntPtr MallocAligned(nuint size, nuint alignment)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_malloc_aligned(size, alignment);
        }

        /// <summary>Aligned, zero-initialized allocation.</summary>
        public static IntPtr ZallocAligned(nuint size, nuint alignment)
        {
            MiInit.mi_thread_init();
            return (IntPtr)MiAlloc.mi_zalloc_aligned(size, alignment);
        }

        // -------------------------------------------------------
        // Usable size
        // -------------------------------------------------------
        /// <summary>Returns the usable size of the block at <paramref name="ptr"/> (≥ requested size).</summary>
        public static nuint UsableSize(IntPtr ptr) => MiAlloc.mi_usable_size((void*)ptr);

        /// <summary>Returns a good allocation size for <paramref name="size"/> bytes (rounded up to bin).</summary>
        public static nuint GoodSize(nuint size) => MiHeap.mi_good_size(size);

        // -------------------------------------------------------
        // Statistics
        // -------------------------------------------------------
        public static void PrintStats() => MiStats.mi_stats_print(null);
        public static void ResetStats() => MiStats.mi_stats_reset();

        // -------------------------------------------------------
        // Options
        // -------------------------------------------------------
        public static long  GetOption(string name)  => MiOptions.mi_option_get(ParseOption(name));
        public static void  SetOption(string name, long value) => MiOptions.mi_option_set(ParseOption(name), value);
        public static bool  IsEnabled(string name)  => MiOptions.mi_option_is_enabled(ParseOption(name));

        private static mi_option_t ParseOption(string name)
        {
            if (Enum.TryParse<mi_option_t>("mi_option_" + name, true, out var v)) return v;
            return mi_option_t.mi_option_verbose;
        }

        // -------------------------------------------------------
        // Heap API
        // -------------------------------------------------------
        public static IntPtr HeapNew()    => (IntPtr)MiInit.mi_heap_new();
        public static void HeapDelete(IntPtr heap)  => MiInit.mi_heap_delete((mi_heap_t*)(void*)heap);
        public static void HeapDestroy(IntPtr heap) => MiInit.mi_heap_destroy((mi_heap_t*)(void*)heap);
        public static void HeapCollect(IntPtr heap, bool force)
            => MiInit.mi_heap_collect((mi_heap_t*)(void*)heap, force);

        public static IntPtr HeapMalloc(IntPtr heap, nuint size)
        {
            mi_heap_t* h = heap == IntPtr.Zero ? MiInit.mi_prim_get_default_heap() : (mi_heap_t*)(void*)heap;
            return (IntPtr)MiAlloc.mi_heap_malloc(h, size);
        }

        public static IntPtr HeapZalloc(IntPtr heap, nuint size)
        {
            mi_heap_t* h = heap == IntPtr.Zero ? MiInit.mi_prim_get_default_heap() : (mi_heap_t*)(void*)heap;
            return (IntPtr)MiAlloc.mi_heap_zalloc(h, size);
        }

        public static IntPtr HeapCalloc(IntPtr heap, nuint count, nuint size)
        {
            mi_heap_t* h = heap == IntPtr.Zero ? MiInit.mi_prim_get_default_heap() : (mi_heap_t*)(void*)heap;
            return (IntPtr)MiAlloc.mi_heap_calloc(h, count, size);
        }

        public static IntPtr HeapRealloc(IntPtr heap, IntPtr p, nuint newSize)
        {
            mi_heap_t* h = heap == IntPtr.Zero ? MiInit.mi_prim_get_default_heap() : (mi_heap_t*)(void*)heap;
            return (IntPtr)MiAlloc.mi_heap_realloc(h, (void*)p, newSize);
        }

        // -------------------------------------------------------
        // Validation helpers
        // -------------------------------------------------------
        public static bool IsInHeapRegion(IntPtr p) => MiSegmentMap.mi_is_in_heap_region((void*)p);
    }
}
