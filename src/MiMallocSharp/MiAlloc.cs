// C# translation of mimalloc/src/alloc.c and alloc-aligned.c
// Public allocation API
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiAlloc
    {
        // -------------------------------------------------------
        // Internal fast-path page malloc (alloc.c _mi_page_malloc_zero)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* _mi_page_malloc_zero(mi_heap_t* heap, mi_page_t* page, nuint size, bool zero, nuint* usable)
        {
            mi_block_t* block = page->free;
            if (block == null)
                return _mi_malloc_generic(heap, size, zero, 0, usable);

            if (usable != null) *usable = MiPage.mi_page_usable_block_size(page);
            page->free = MiPage.mi_block_next(page, block);
            page->used++;

            if (zero)
            {
                if (page->free_is_zero)
                    block->next = 0;
                else
                    MiLibc.mi_memzero_aligned(block, page->block_size);
            }
            return block;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* _mi_page_malloc(mi_heap_t* heap, mi_page_t* page, nuint size)
            => _mi_page_malloc_zero(heap, page, size, false, null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* _mi_page_malloc_zeroed(mi_heap_t* heap, mi_page_t* page, nuint size)
            => _mi_page_malloc_zero(heap, page, size, true, null);

        // -------------------------------------------------------
        // Small allocation fast path
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void* mi_heap_malloc_small_zero(mi_heap_t* heap, nuint size, bool zero, nuint* usable)
        {
            if (size == 0) size = (nuint)sizeof(void*);
            if (heap == null || heap == MiInit._mi_heap_empty_ptr) {
                // Heap not initialized - call generic path
                MiInit.mi_thread_init();
                heap = MiInit.mi_prim_get_default_heap();
                if (heap == null || heap == MiInit._mi_heap_empty_ptr) return null;
            }
            mi_page_t* page = MiPage._mi_heap_get_free_small_page(heap, size);
            return _mi_page_malloc_zero(heap, page, size, zero, usable);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* mi_heap_malloc_small(mi_heap_t* heap, nuint size)
            => mi_heap_malloc_small_zero(heap, size, false, null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* mi_malloc_small(nuint size)
            => mi_heap_malloc_small(MiInit.mi_prim_get_default_heap(), size);

        // -------------------------------------------------------
        // Main allocation function
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* _mi_heap_malloc_zero_ex(mi_heap_t* heap, nuint size, bool zero, nuint huge_alignment, nuint* usable)
        {
            if (heap == null || heap->tld == null)
            {
                MiInit.mi_thread_init();
                heap = MiInit.mi_prim_get_default_heap();
                if (heap == null || heap->tld == null) return null;
            }
            if (size <= MI.MI_SMALL_SIZE_MAX)
                return mi_heap_malloc_small_zero(heap, size, zero, usable);
            return _mi_malloc_generic(heap, size, zero, huge_alignment, usable);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* _mi_heap_malloc_zero(mi_heap_t* heap, nuint size, bool zero)
            => _mi_heap_malloc_zero_ex(heap, size, zero, 0, null);

        // -------------------------------------------------------
        // Public allocation API (mirrors mimalloc public API)
        // -------------------------------------------------------
        public static void* mi_heap_malloc(mi_heap_t* heap, nuint size)
        {
            if (heap == null || heap == MiInit._mi_heap_empty_ptr || heap->tld == null)
            {
                MiInit.mi_thread_init();
                heap = MiInit.mi_prim_get_default_heap();
                if (heap == null || heap == MiInit._mi_heap_empty_ptr || heap->tld == null) return null;
            }
            return _mi_heap_malloc_zero(heap, size, false);
        }

        public static void* mi_malloc(nuint size)
            => mi_heap_malloc(MiInit.mi_prim_get_default_heap(), size);

        public static void* mi_heap_zalloc(mi_heap_t* heap, nuint size)
            => _mi_heap_malloc_zero(heap, size, true);

        public static void* mi_zalloc(nuint size)
            => mi_heap_zalloc(MiInit.mi_prim_get_default_heap(), size);

        public static void* mi_zalloc_small(nuint size)
            => mi_heap_malloc_small_zero(MiInit.mi_prim_get_default_heap(), size, true, null);

        public static void* mi_heap_calloc(mi_heap_t* heap, nuint count, nuint size)
        {
            if (MiLibc.mi_count_size_overflow(count, size, out nuint total)) return null;
            return mi_heap_zalloc(heap, total);
        }

        public static void* mi_calloc(nuint count, nuint size)
            => mi_heap_calloc(MiInit.mi_prim_get_default_heap(), count, size);

        public static void* mi_heap_mallocn(mi_heap_t* heap, nuint count, nuint size)
        {
            if (MiLibc.mi_count_size_overflow(count, size, out nuint total)) return null;
            return mi_heap_malloc(heap, total);
        }

        public static void* mi_mallocn(nuint count, nuint size)
            => mi_heap_mallocn(MiInit.mi_prim_get_default_heap(), count, size);

        // -------------------------------------------------------
        // Realloc
        // -------------------------------------------------------
        public static void* _mi_heap_realloc_zero(mi_heap_t* heap, void* p, nuint newsize, bool zero,
            nuint* usable_pre, nuint* usable_post)
        {
            mi_page_t* page;
            nuint size;
            if (p == null) { page = null; size = 0; if (usable_pre != null) *usable_pre = 0; }
            else
            {
                page = MiPage.mi_validate_ptr_page(p, "mi_realloc");
                size = MiPage._mi_usable_size(p, page);
                if (usable_pre != null) *usable_pre = MiPage.mi_page_usable_block_size(page);
            }
            // check if we can reuse (within 50% waste)
            if (newsize <= size && newsize >= (size / 2) && newsize > 0)
            {
                if (usable_post != null) *usable_post = MiPage.mi_page_usable_block_size(page!);
                return p;
            }
            // allocate new
            nuint post_usable;
            void* newp = _mi_heap_malloc_zero_ex(heap, newsize, false, 0, &post_usable);
            if (usable_post != null) *usable_post = post_usable;
            if (newp == null) return null;
            if (zero && newsize > size)
            {
                nuint start = size >= (nuint)sizeof(nint) ? size - (nuint)sizeof(nint) : 0;
                MiLibc.mi_memzero((byte*)newp + start, newsize - start);
            }
            else if (newsize == 0)
            {
                ((byte*)newp)[0] = 0;
            }
            if (p != null)
            {
                nuint copysize = newsize > size ? size : newsize;
                MiLibc.mi_memcpy(newp, p, copysize);
                MiFree.mi_free(p);
            }
            return newp;
        }

        public static void* mi_heap_realloc(mi_heap_t* heap, void* p, nuint newsize)
            => _mi_heap_realloc_zero(heap, p, newsize, false, null, null);

        public static void* mi_realloc(void* p, nuint newsize)
            => mi_heap_realloc(MiInit.mi_prim_get_default_heap(), p, newsize);

        public static void* mi_heap_reallocn(mi_heap_t* heap, void* p, nuint count, nuint size)
        {
            if (MiLibc.mi_count_size_overflow(count, size, out nuint total)) return null;
            return mi_heap_realloc(heap, p, total);
        }

        public static void* mi_reallocn(void* p, nuint count, nuint size)
            => mi_heap_reallocn(MiInit.mi_prim_get_default_heap(), p, count, size);

        public static void* mi_heap_rezalloc(mi_heap_t* heap, void* p, nuint newsize)
            => _mi_heap_realloc_zero(heap, p, newsize, true, null, null);

        public static void* mi_rezalloc(void* p, nuint newsize)
            => mi_heap_rezalloc(MiInit.mi_prim_get_default_heap(), p, newsize);

        public static void* mi_heap_recalloc(mi_heap_t* heap, void* p, nuint count, nuint size)
        {
            if (MiLibc.mi_count_size_overflow(count, size, out nuint total)) return null;
            return mi_heap_rezalloc(heap, p, total);
        }

        public static void* mi_recalloc(void* p, nuint count, nuint size)
            => mi_heap_recalloc(MiInit.mi_prim_get_default_heap(), p, count, size);

        public static void* mi_heap_reallocf(mi_heap_t* heap, void* p, nuint newsize)
        {
            void* newp = mi_heap_realloc(heap, p, newsize);
            if (newp == null && p != null) MiFree.mi_free(p);
            return newp;
        }

        public static void* mi_reallocf(void* p, nuint newsize)
            => mi_heap_reallocf(MiInit.mi_prim_get_default_heap(), p, newsize);

        // -------------------------------------------------------
        // expand (in-place shrink only)
        // -------------------------------------------------------
        public static void* mi_expand(void* p, nuint newsize)
        {
            if (p == null) return null;
            mi_page_t* page = MiPage.mi_validate_ptr_page(p, "mi_expand");
            if (page == null) return null;
            nuint size = MiPage._mi_usable_size(p, page);
            if (newsize > size) return null;
            return p;
        }

        // -------------------------------------------------------
        // Usable size
        // -------------------------------------------------------
        public static nuint mi_usable_size(void* p) => MiHeap.mi_usable_size(p);

        // -------------------------------------------------------
        // Generic slow path (called when page->free == null)
        // -------------------------------------------------------
        private static void* _mi_malloc_generic(mi_heap_t* heap, nuint size, bool zero, nuint huge_alignment, nuint* usable)
        {
            if (!MiInit._mi_process_is_initialized) MiInit.mi_process_init();
            mi_page_t* page = MiPage._mi_malloc_generic(heap, size, zero, huge_alignment, usable);
            if (page == null) return null;
            return _mi_page_malloc_zero(heap, page, size, zero, usable);
        }

        // -------------------------------------------------------
        // Aligned allocation (alloc-aligned.c)
        // -------------------------------------------------------
        public static void* mi_heap_malloc_aligned_at(mi_heap_t* heap, nuint size, nuint alignment, nuint offset)
        {
            if (alignment == 0 || !MiLibc._mi_is_power_of_two(alignment)) return null;
            if (size > MI.MI_MAX_ALLOC_SIZE) return null;
            // Fast path: naturally aligned?
            if (offset == 0 && alignment <= MI.MI_MAX_ALIGN_SIZE)
                return mi_heap_malloc(heap, size);
            if (offset == 0 && mi_malloc_is_naturally_aligned(size, alignment))
                return mi_heap_malloc(heap, size);
            // Slow path: over-allocate and align
            return mi_heap_malloc_zero_aligned_overalloc(heap, size, alignment, offset, false, null);
        }

        public static void* mi_heap_malloc_aligned(mi_heap_t* heap, nuint size, nuint alignment)
            => mi_heap_malloc_aligned_at(heap, size, alignment, 0);

        public static void* mi_malloc_aligned(nuint size, nuint alignment)
            => mi_heap_malloc_aligned(MiInit.mi_prim_get_default_heap(), size, alignment);

        public static void* mi_malloc_aligned_at(nuint size, nuint alignment, nuint offset)
            => mi_heap_malloc_aligned_at(MiInit.mi_prim_get_default_heap(), size, alignment, offset);

        public static void* mi_zalloc_aligned(nuint size, nuint alignment)
        {
            void* p = mi_malloc_aligned(size, alignment);
            if (p != null) MiLibc.mi_memzero(p, size);
            return p;
        }

        private static bool mi_malloc_is_naturally_aligned(nuint size, nuint alignment)
        {
            if (alignment > size) return false;
            if (alignment <= MI.MI_MAX_ALIGN_SIZE) return true;
            nuint bsize = MiHeap.mi_good_size(size);
            return bsize <= MI.MI_MAX_ALIGN_GUARANTEE && (bsize & (alignment - 1)) == 0;
        }

        private static void* mi_heap_malloc_zero_aligned_overalloc(mi_heap_t* heap, nuint size, nuint alignment,
            nuint offset, bool zero, nuint* usable)
        {
            nuint oversize = (size < MI.MI_MAX_ALIGN_SIZE ? MI.MI_MAX_ALIGN_SIZE : size) + alignment - 1;
            nuint post_usable;
            void* p = _mi_heap_malloc_zero_ex(heap, oversize, zero, 0, &post_usable);
            if (p == null) return null;

            nuint align_mask = alignment - 1;
            nuint poffset   = ((nuint)p + offset) & align_mask;
            nuint adjust    = poffset == 0 ? 0 : alignment - poffset;
            void* aligned_p = (void*)((nuint)p + adjust);

            if (aligned_p != p)
            {
                mi_page_t* page = MiPage.mi_validate_ptr_page(p, "mi_malloc_aligned");
                if (page != null) MiPage.mi_page_set_has_aligned(page, true);
            }
            if (usable != null) *usable = post_usable > adjust ? post_usable - adjust : 0;
            return aligned_p;
        }

        // -------------------------------------------------------
        // strdup / strndup
        // -------------------------------------------------------
        public static byte* mi_heap_strdup(mi_heap_t* heap, byte* s)
        {
            if (s == null) return null;
            nuint len = MiLibc._mi_strlen(s);
            byte* t = (byte*)mi_heap_malloc(heap, len + 1);
            if (t == null) return null;
            MiLibc.mi_memcpy(t, s, len);
            t[len] = 0;
            return t;
        }

        public static byte* mi_strdup(byte* s)
            => mi_heap_strdup(MiInit.mi_prim_get_default_heap(), s);

        public static byte* mi_heap_strndup(mi_heap_t* heap, byte* s, nuint n)
        {
            if (s == null) return null;
            nuint len = MiLibc._mi_strnlen(s, n);
            byte* t = (byte*)mi_heap_malloc(heap, len + 1);
            if (t == null) return null;
            MiLibc.mi_memcpy(t, s, len);
            t[len] = 0;
            return t;
        }

        public static byte* mi_strndup(byte* s, nuint n)
            => mi_heap_strndup(MiInit.mi_prim_get_default_heap(), s, n);
    }
}
