// C# translation of mimalloc/src/libc.c
// Provides memcpy, memset, strlen, snprintf, etc. to avoid C runtime dependency
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiLibc
    {
        // -------------------------------------------------------
        // mi_memcpy / mi_memzero / mi_memset
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_memcpy(void* dst, void* src, nuint n)
        {
            Buffer.MemoryCopy(src, dst, n, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_memzero(void* dst, nuint n)
        {
            new Span<byte>(dst, (int)n).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_memzero_aligned(void* dst, nuint n)
        {
            // on modern CPUs all zeroing paths are fine; no special SIMD needed here
            mi_memzero(dst, n);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void mi_memcpy_aligned(void* dst, void* src, nuint n)
        {
            mi_memcpy(dst, src, n);
        }

        // -------------------------------------------------------
        // String helpers (from libc.c)
        // -------------------------------------------------------
        public static char _mi_toupper(char c)
            => c >= 'a' && c <= 'z' ? (char)(c - 'a' + 'A') : c;

        public static int _mi_strnicmp(byte* s, byte* t, nuint n)
        {
            if (n == 0) return 0;
            for (; *s != 0 && *t != 0 && n > 0; s++, t++, n--)
            {
                if (_mi_toupper((char)*s) != _mi_toupper((char)*t)) break;
            }
            return n == 0 ? 0 : *s - *t;
        }

        public static void _mi_strlcpy(byte* dest, byte* src, nuint dest_size)
        {
            if (dest == null || src == null || dest_size == 0) return;
            while (*src != 0 && dest_size > 1) { *dest++ = *src++; dest_size--; }
            *dest = 0;
        }

        public static void _mi_strlcat(byte* dest, byte* src, nuint dest_size)
        {
            if (dest == null || src == null || dest_size == 0) return;
            while (*dest != 0 && dest_size > 1) { dest++; dest_size--; }
            _mi_strlcpy(dest, src, dest_size);
        }

        public static nuint _mi_strlen(byte* s)
        {
            if (s == null) return 0;
            nuint len = 0;
            while (s[len] != 0) len++;
            return len;
        }

        public static nuint _mi_strnlen(byte* s, nuint max_len)
        {
            if (s == null) return 0;
            nuint len = 0;
            while (s[len] != 0 && len < max_len) len++;
            return len;
        }

        // -------------------------------------------------------
        // Popcount (from libc.c)
        // -------------------------------------------------------
        public static nuint _mi_popcount(nuint x)
        {
#if NET6_0_OR_GREATER
            if (sizeof(nuint) == 8)
                return (nuint)System.Numerics.BitOperations.PopCount((ulong)x);
            else
                return (nuint)System.Numerics.BitOperations.PopCount((uint)x);
#else
            return sizeof(nuint) == 8 ? popcount64((ulong)x) : popcount32((uint)x);
#endif
        }

#if !NET6_0_OR_GREATER
        private static nuint popcount32(uint x)
        {
            x -= (x >> 1) & 0x55555555u;
            x  = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
            x  = (x + (x >> 4)) & 0x0F0F0F0Fu;
            x += (x << 8); x += (x << 16);
            return (nuint)(x >> 24);
        }
        private static nuint popcount64(ulong x)
        {
            x -= (x >> 1) & 0x5555555555555555UL;
            x  = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x  = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            x += (x << 8); x += (x << 16); x += (x << 32);
            return (nuint)(x >> 56);
        }
#endif

        // -------------------------------------------------------
        // Count trailing zeros (ctz) and leading zeros (clz)
        // -------------------------------------------------------
        public static int mi_ctz(nuint x)
        {
            if (x == 0) return (int)(sizeof(nuint) * 8);
#if NET6_0_OR_GREATER
            return sizeof(nuint) == 8
                ? System.Numerics.BitOperations.TrailingZeroCount((ulong)x)
                : System.Numerics.BitOperations.TrailingZeroCount((uint)x);
#else
            int n = 0;
            if (sizeof(nuint) == 8)
            {
                ulong v = (ulong)x;
                if ((v & 0xFFFFFFFFu) == 0) { n += 32; v >>= 32; }
                if ((v & 0x0000FFFFu) == 0) { n += 16; v >>= 16; }
                if ((v & 0x000000FFu) == 0) { n +=  8; v >>=  8; }
                if ((v & 0x0000000Fu) == 0) { n +=  4; v >>=  4; }
                if ((v & 0x00000003u) == 0) { n +=  2; v >>=  2; }
                if ((v & 0x00000001u) == 0) { n +=  1; }
            }
            else
            {
                uint v = (uint)x;
                if ((v & 0x0000FFFFu) == 0) { n += 16; v >>= 16; }
                if ((v & 0x000000FFu) == 0) { n +=  8; v >>=  8; }
                if ((v & 0x0000000Fu) == 0) { n +=  4; v >>=  4; }
                if ((v & 0x00000003u) == 0) { n +=  2; v >>=  2; }
                if ((v & 0x00000001u) == 0) { n +=  1; }
            }
            return n;
#endif
        }

        public static int mi_clz(nuint x)
        {
            if (x == 0) return (int)(sizeof(nuint) * 8);
#if NET6_0_OR_GREATER
            return sizeof(nuint) == 8
                ? System.Numerics.BitOperations.LeadingZeroCount((ulong)x)
                : System.Numerics.BitOperations.LeadingZeroCount((uint)x);
#else
            int n = 0;
            if (sizeof(nuint) == 8)
            {
                ulong v = (ulong)x;
                if (v <= 0xFFFFFFFFu)  { n += 32; }
                else { v >>= 32; }
                if ((v & 0xFFFF0000u) == 0) { n += 16; v <<= 16; }
                if ((v & 0xFF000000u) == 0) { n +=  8; v <<=  8; }
                if ((v & 0xF0000000u) == 0) { n +=  4; v <<=  4; }
                if ((v & 0xC0000000u) == 0) { n +=  2; v <<=  2; }
                if ((v & 0x80000000u) == 0) { n +=  1; }
            }
            else
            {
                uint v = (uint)x;
                if ((v & 0xFFFF0000u) == 0) { n += 16; v <<= 16; }
                if ((v & 0xFF000000u) == 0) { n +=  8; v <<=  8; }
                if ((v & 0xF0000000u) == 0) { n +=  4; v <<=  4; }
                if ((v & 0xC0000000u) == 0) { n +=  2; v <<=  2; }
                if ((v & 0x80000000u) == 0) { n +=  1; }
            }
            return n;
#endif
        }

        // -------------------------------------------------------
        // Alignment helpers
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint _mi_align_up(nuint x, nuint alignment)
        {
            // alignment must be power-of-two
            nuint mask = alignment - 1;
            return (x + mask) & ~mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint _mi_align_down(nuint x, nuint alignment)
        {
            return x & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool _mi_is_power_of_two(nuint x)
            => x != 0 && (x & (x - 1)) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool _mi_is_aligned(void* p, nuint alignment)
            => ((nuint)p & (alignment - 1)) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint _mi_divide_up(nuint x, nuint d)
            => (x + d - 1) / d;

        // -------------------------------------------------------
        // wsize (word-size) helpers
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint _mi_wsize_from_size(nuint size)
            => (size + (nuint)sizeof(void*) - 1) / (nuint)sizeof(void*);

        // -------------------------------------------------------
        // Count-size overflow check
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool mi_count_size_overflow(nuint count, nuint size, out nuint total)
        {
            if (size == 0 || count == 0) { total = 0; return false; }
            if (count > MI.MI_NUINT_MAX / size) { total = MI.MI_NUINT_MAX; return true; }
            total = count * size;
            return false;
        }

        // -------------------------------------------------------
        // Random shuffle (for weak randomness)
        // -------------------------------------------------------
        public static nuint _mi_random_shuffle(nuint x)
        {
            if (sizeof(nuint) == 8)
            {
                ulong v = (ulong)x;
                v ^= v >> 30; v *= 0xbf58476d1ce4e5b9UL;
                v ^= v >> 27; v *= 0x94d049bb133111ebUL;
                v ^= v >> 31;
                return (nuint)v;
            }
            else
            {
                uint v = (uint)x;
                v ^= v >> 16; v *= 0x45d9f3bu;
                v ^= v >> 16;
                return (nuint)v;
            }
        }

        // -------------------------------------------------------
        // Usable size: determines actual usable block size from a pointer
        // -------------------------------------------------------
        public static bool mi_mem_is_zero(void* p, nuint size)
        {
            byte* b = (byte*)p;
            for (nuint i = 0; i < size; i++)
                if (b[i] != 0) return false;
            return true;
        }

        // -------------------------------------------------------
        // Simple snprintf equivalent (from libc.c _mi_snprintf)
        // -------------------------------------------------------

        public static void _mi_warning_message(string fmt, params object?[] args)
        {
            MiStats.warning_count++;
            if (MiStats.warning_count > MI.MI_MAX_WARNING_COUNT) return;
            MiLog.Warning(fmt, args!);
        }

        public static void _mi_error_message(int err, string fmt, params object?[] args)
        {
            MiStats.error_count++;
            if (MiStats.error_count > MI.MI_MAX_ERROR_COUNT) return;
            MiLog.Error(err, fmt, args!);
        }

    }
}
