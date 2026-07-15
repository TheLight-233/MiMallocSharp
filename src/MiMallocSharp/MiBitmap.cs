// C# translation of mimalloc/src/bitmap.c and bitmap.h
// Concurrent bitmap over arrays of nuint (atomic size_t fields)
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    /// <summary>
    /// Concurrent bitmap that can set/reset sequences of bits atomically.
    /// Maps directly to mimalloc bitmap.c/bitmap.h.
    /// A bitmap is simply a nuint* pointing to an array of atomic fields.
    /// Each field covers MI_BITMAP_FIELD_BITS bits.
    /// </summary>
    internal static unsafe class MiBitmap
    {
        private static readonly int BITS = MI.MI_BITMAP_FIELD_BITS; // 32 (32-bit) | 64 (64-bit)

        // -------------------------------------------------------
        // Index helpers (from bitmap.h)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_create(nuint field_idx, nuint bit_idx)
            => field_idx * (nuint)BITS + bit_idx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_create_ex(nuint field_idx, nuint bit_idx)
            => field_idx * (nuint)BITS + bit_idx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_create_from_bit(nuint full_bitidx)
            => mi_bitmap_index_create(full_bitidx / (nuint)BITS, full_bitidx % (nuint)BITS);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_field(nuint bitmap_idx) => bitmap_idx / (nuint)BITS;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_bit_in_field(nuint bitmap_idx) => bitmap_idx % (nuint)BITS;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint mi_bitmap_index_bit(nuint bitmap_idx) => bitmap_idx;

        // -------------------------------------------------------
        // Internal: mask for count bits starting at bitidx
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint mi_bitmap_mask_(nuint count, nuint bitidx)
        {
            if (count >= (nuint)BITS) return MI.MI_NUINT_MAX;
            if (count == 0) return 0;
            return (((nuint)1 << (int)count) - 1) << (int)bitidx;
        }

        // -------------------------------------------------------
        // Try to claim a sequence of `count` bits in a single field at idx
        // -------------------------------------------------------
        public static bool _mi_bitmap_try_find_claim_field(nuint* bitmap, nuint idx, nuint count, out nuint bitmap_idx)
        {
            bitmap_idx = 0;
            nuint* field = bitmap + idx;
            nuint map = MiAtomic.load_relaxed(field);
            if (map == MI.MI_NUINT_MAX) return false; // short-cut: all set

            nuint mask = mi_bitmap_mask_(count, 0);
            nuint bitidx_max = (nuint)BITS - count;

            // start at first zero bit (fast-scan)
            nuint bitidx = (nuint)MiLibc.mi_ctz(~map);
            nuint m = mask << (int)bitidx;

            while (bitidx <= bitidx_max)
            {
                nuint mapm = map & m;
                if (mapm == 0)
                {
                    nuint newmap = map | m;
                    nuint expected = map;
                    if (!MiAtomic.cas_strong_acq_rel(field, ref expected, newmap))
                    {
                        map = expected;
                        continue; // retry with updated map
                    }
                    bitmap_idx = mi_bitmap_index_create(idx, bitidx);
                    return true;
                }
                else
                {
                    // skip to next position after the rightmost set bit in mapm
                    nuint shift = (count == 1) ? 1 : (nuint)(BITS - MiLibc.mi_clz(mapm) - (int)bitidx);
                    if (shift == 0) shift = 1;
                    bitidx += shift;
                    m <<= (int)shift;
                }
            }
            return false;
        }

        // -------------------------------------------------------
        // Find `count` bits of 0, set to 1. Wraps around.
        // -------------------------------------------------------
        public static bool _mi_bitmap_try_find_from_claim(nuint* bitmap, nuint bitmap_fields, nuint start_field, nuint count, out nuint bitmap_idx)
        {
            nuint idx = start_field;
            for (nuint v = 0; v < bitmap_fields; v++, idx++)
            {
                if (idx >= bitmap_fields) idx = 0;
                if (_mi_bitmap_try_find_claim_field(bitmap, idx, count, out bitmap_idx))
                    return true;
            }
            bitmap_idx = 0;
            return false;
        }

        // Delegate for predicate-based claim
        public delegate bool mi_bitmap_pred_fun_t(nuint bitmap_idx, void* pred_arg);

        public static bool _mi_bitmap_try_find_from_claim_pred(nuint* bitmap, nuint bitmap_fields,
            nuint start_field, nuint count,
            mi_bitmap_pred_fun_t? pred_fun, void* pred_arg,
            out nuint bitmap_idx)
        {
            nuint idx = start_field;
            for (nuint v = 0; v < bitmap_fields; v++, idx++)
            {
                if (idx >= bitmap_fields) idx = 0;
                if (_mi_bitmap_try_find_claim_field(bitmap, idx, count, out bitmap_idx))
                {
                    if (pred_fun == null || pred_fun(bitmap_idx, pred_arg))
                        return true;
                    _mi_bitmap_unclaim(bitmap, bitmap_fields, count, bitmap_idx);
                }
            }
            bitmap_idx = 0;
            return false;
        }

        // -------------------------------------------------------
        // Unclaim (set count bits to 0)
        // -------------------------------------------------------
        public static bool _mi_bitmap_unclaim(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
        {
            nuint idx    = mi_bitmap_index_field(bitmap_idx);
            nuint bitidx = mi_bitmap_index_bit_in_field(bitmap_idx);
            nuint mask   = mi_bitmap_mask_(count, bitidx);
            nuint prev   = MiAtomic.and_acq_rel(bitmap + idx, ~mask);
            return (prev & mask) == mask;
        }

        // -------------------------------------------------------
        // Claim (set count bits to 1)
        // -------------------------------------------------------
        public static bool _mi_bitmap_claim(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx, bool* any_zero)
        {
            nuint idx    = mi_bitmap_index_field(bitmap_idx);
            nuint bitidx = mi_bitmap_index_bit_in_field(bitmap_idx);
            nuint mask   = mi_bitmap_mask_(count, bitidx);
            nuint prev   = MiAtomic.or_acq_rel(bitmap + idx, mask);
            if (any_zero != null) *any_zero = (prev & mask) != mask;
            return (prev & mask) == 0;
        }

        // Try claim (only if all zero)
        public static bool _mi_bitmap_try_claim(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
        {
            nuint idx    = mi_bitmap_index_field(bitmap_idx);
            nuint bitidx = mi_bitmap_index_bit_in_field(bitmap_idx);
            nuint mask   = mi_bitmap_mask_(count, bitidx);
            nuint expected = MiAtomic.load_relaxed(bitmap + idx);
            do
            {
                if ((expected & mask) != 0) return false;
            } while (!MiAtomic.cas_strong_acq_rel(bitmap + idx, ref expected, expected | mask));
            return true;
        }

        // -------------------------------------------------------
        // Is claimed?
        // -------------------------------------------------------
        private static bool mi_bitmap_is_claimedx(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx, bool* any_ones)
        {
            nuint idx    = mi_bitmap_index_field(bitmap_idx);
            nuint bitidx = mi_bitmap_index_bit_in_field(bitmap_idx);
            nuint mask   = mi_bitmap_mask_(count, bitidx);
            nuint field  = MiAtomic.load_relaxed(bitmap + idx);
            if (any_ones != null) *any_ones = (field & mask) != 0;
            return (field & mask) == mask;
        }

        public static bool _mi_bitmap_is_claimed(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
            => mi_bitmap_is_claimedx(bitmap, bitmap_fields, count, bitmap_idx, null);

        public static bool _mi_bitmap_is_any_claimed(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
        {
            bool any_ones;
            mi_bitmap_is_claimedx(bitmap, bitmap_fields, count, bitmap_idx, &any_ones);
            return any_ones;
        }

        // -------------------------------------------------------
        // Cross-field (across) operations
        // -------------------------------------------------------
        private static bool mi_bitmap_try_find_claim_field_across(nuint* bitmap, nuint bitmap_fields, nuint idx, nuint count, int retries, out nuint bitmap_idx)
        {
            bitmap_idx = 0;
            nuint* field = bitmap + idx;
            nuint map = MiAtomic.load_relaxed(field);
            nuint initial = (nuint)MiLibc.mi_clz(map); // leading zeros
            if (initial == 0) return false;
            if (initial >= count) return _mi_bitmap_try_find_claim_field(bitmap, idx, count, out bitmap_idx);
            if (MiLibc._mi_divide_up(count - initial, (nuint)BITS) >= (bitmap_fields - idx)) return false;

            // scan ahead
            nuint found = initial;
            nuint mask_final = 0;
            nuint* scan = field;
            while (found < count)
            {
                scan++;
                nuint scan_map = MiAtomic.load_relaxed(scan);
                nuint mask_bits = (found + (nuint)BITS <= count ? (nuint)BITS : count - found);
                mask_final = mi_bitmap_mask_(mask_bits, 0);
                if ((scan_map & mask_final) != 0) return false;
                found += mask_bits;
            }

            nuint* final_field = scan;
            nuint* initial_field = field;
            nuint initial_idx  = (nuint)BITS - initial;
            nuint initial_mask = mi_bitmap_mask_(initial, initial_idx);

            // claim initial field
            nuint curmap = MiAtomic.load_relaxed(initial_field);
            nuint newmap;
            do
            {
                if ((curmap & initial_mask) != 0) goto rollback;
                newmap = curmap | initial_mask;
            } while (!MiAtomic.cas_strong_acq_rel(initial_field, ref curmap, newmap));

            // claim intermediate fields
            for (nuint* f = initial_field + 1; f < final_field; f++)
            {
                nuint z = 0;
                if (!MiAtomic.cas_strong_acq_rel(f, ref z, MI.MI_NUINT_MAX)) goto rollback;
            }

            // claim final field
            curmap = MiAtomic.load_relaxed(final_field);
            do
            {
                if ((curmap & mask_final) != 0) goto rollback;
                newmap = curmap | mask_final;
            } while (!MiAtomic.cas_strong_acq_rel(final_field, ref curmap, newmap));

            bitmap_idx = mi_bitmap_index_create(idx, initial_idx);
            return true;

        rollback:
            for (nuint* f = final_field - 1; f > initial_field; f--)
            {
                nuint full = MI.MI_NUINT_MAX;
                MiAtomic.cas_strong_acq_rel(f, ref full, 0);
            }
            if (final_field > initial_field)
            {
                curmap = MiAtomic.load_relaxed(initial_field);
                do { newmap = curmap & ~initial_mask; }
                while (!MiAtomic.cas_strong_acq_rel(initial_field, ref curmap, newmap));
            }
            // rollback stat
            fixed (mi_stats_t* s = &MiStats._mi_stats_main)
                MiStats._mi_stat_counter_increase(&s->arena_rollback_count, 1);

            if (retries <= 2)
                return mi_bitmap_try_find_claim_field_across(bitmap, bitmap_fields, idx, count, retries + 1, out bitmap_idx);
            return false;
        }

        public static bool _mi_bitmap_try_find_from_claim_across(nuint* bitmap, nuint bitmap_fields, nuint start_field, nuint count, out nuint bitmap_idx)
        {
            if (count <= 2)
                return _mi_bitmap_try_find_from_claim(bitmap, bitmap_fields, start_field, count, out bitmap_idx);

            nuint idx = start_field;
            for (nuint v = 0; v < bitmap_fields; v++, idx++)
            {
                if (idx >= bitmap_fields) idx = 0;
                if (mi_bitmap_try_find_claim_field_across(bitmap, bitmap_fields, idx, count, 0, out bitmap_idx))
                    return true;
            }
            bitmap_idx = 0;
            return false;
        }

        // Cross-field helper: compute pre/mid/post masks
        private static nuint mi_bitmap_mask_across(nuint bitmap_idx, nuint bitmap_fields, nuint count, out nuint pre_mask, out nuint mid_mask, out nuint post_mask)
        {
            nuint bitidx = mi_bitmap_index_bit_in_field(bitmap_idx);
            if (bitidx + count <= (nuint)BITS)
            {
                pre_mask = mi_bitmap_mask_(count, bitidx);
                mid_mask = 0; post_mask = 0;
                return 0;
            }
            nuint pre_bits = (nuint)BITS - bitidx;
            pre_mask = mi_bitmap_mask_(pre_bits, bitidx);
            count -= pre_bits;
            nuint mid_count = count / (nuint)BITS;
            mid_mask = MI.MI_NUINT_MAX;
            count %= (nuint)BITS;
            post_mask = count == 0 ? 0 : mi_bitmap_mask_(count, 0);
            return mid_count;
        }

        public static bool _mi_bitmap_unclaim_across(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
        {
            nuint idx = mi_bitmap_index_field(bitmap_idx);
            nuint mid_count = mi_bitmap_mask_across(bitmap_idx, bitmap_fields, count, out nuint pre, out nuint mid, out nuint post);
            bool all_one = true;
            nuint* field = bitmap + idx;
            nuint prev = MiAtomic.and_acq_rel(field++, ~pre);
            if ((prev & pre) != pre) all_one = false;
            for (nuint i = 0; i < mid_count; i++)
            {
                prev = MiAtomic.and_acq_rel(field++, ~mid);
                if ((prev & mid) != mid) all_one = false;
            }
            if (post != 0)
            {
                prev = MiAtomic.and_acq_rel(field, ~post);
                if ((prev & post) != post) all_one = false;
            }
            return all_one;
        }

        public static bool _mi_bitmap_claim_across(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx, bool* pany_zero, nuint* already_set)
        {
            nuint idx = mi_bitmap_index_field(bitmap_idx);
            nuint mid_count = mi_bitmap_mask_across(bitmap_idx, bitmap_fields, count, out nuint pre, out nuint mid, out nuint post);
            bool all_zero = true, any_zero = false;
            nuint one_count = 0;
            nuint* field = bitmap + idx;
            nuint prev = MiAtomic.or_acq_rel(field++, pre);
            if ((prev & pre) != 0) { all_zero = false; one_count += MiLibc._mi_popcount(prev & pre); }
            if ((prev & pre) != pre) any_zero = true;
            for (nuint i = 0; i < mid_count; i++)
            {
                prev = MiAtomic.or_acq_rel(field++, mid);
                if ((prev & mid) != 0) { all_zero = false; one_count += MiLibc._mi_popcount(prev & mid); }
                if ((prev & mid) != mid) any_zero = true;
            }
            if (post != 0)
            {
                prev = MiAtomic.or_acq_rel(field, post);
                if ((prev & post) != 0) { all_zero = false; one_count += MiLibc._mi_popcount(prev & post); }
                if ((prev & post) != post) any_zero = true;
            }
            if (pany_zero != null) *pany_zero = any_zero;
            if (already_set != null) *already_set = one_count;
            return all_zero;
        }

        public static bool _mi_bitmap_is_claimed_across(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx, nuint* already_set)
        {
            nuint idx = mi_bitmap_index_field(bitmap_idx);
            nuint mid_count = mi_bitmap_mask_across(bitmap_idx, bitmap_fields, count, out nuint pre, out nuint mid, out nuint post);
            bool all_ones = true; nuint one_count = 0;
            nuint* field = bitmap + idx;
            nuint v = MiAtomic.load_relaxed(field++);
            if ((v & pre) != pre) all_ones = false;
            one_count += MiLibc._mi_popcount(v & pre);
            for (nuint i = 0; i < mid_count; i++)
            {
                v = MiAtomic.load_relaxed(field++);
                if ((v & mid) != mid) all_ones = false;
                one_count += MiLibc._mi_popcount(v & mid);
            }
            if (post != 0)
            {
                v = MiAtomic.load_relaxed(field);
                if ((v & post) != post) all_ones = false;
                one_count += MiLibc._mi_popcount(v & post);
            }
            if (already_set != null) *already_set = one_count;
            return all_ones;
        }

        public static bool _mi_bitmap_is_any_claimed_across(nuint* bitmap, nuint bitmap_fields, nuint count, nuint bitmap_idx)
        {
            nuint idx = mi_bitmap_index_field(bitmap_idx);
            nuint mid_count = mi_bitmap_mask_across(bitmap_idx, bitmap_fields, count, out nuint pre, out nuint mid, out nuint post);
            nuint* field = bitmap + idx;
            if ((MiAtomic.load_relaxed(field++) & pre) != 0) return true;
            for (nuint i = 0; i < mid_count; i++)
                if ((MiAtomic.load_relaxed(field++) & mid) != 0) return true;
            if (post != 0 && (MiAtomic.load_relaxed(field) & post) != 0) return true;
            return false;
        }
    }
}
