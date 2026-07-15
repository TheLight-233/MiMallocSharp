// C# translation of mimalloc/atomic.h
// Maps C11 _Atomic operations to System.Threading.Interlocked and Volatile
using System;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiAtomic
    {
        // -------------------------------------------------------
        // Load / Store  (relaxed ~ no fence; release/acquire for ordering)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint load_relaxed(nuint* p) => Volatile.Read(ref *p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint load_acquire(nuint* p) => Volatile.Read(ref *p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void store_relaxed(nuint* p, nuint v) => Volatile.Write(ref *p, v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void store_release(nuint* p, nuint v) => Volatile.Write(ref *p, v);

        // pointer variants
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* load_ptr_relaxed(void** p) => (void*)Volatile.Read(ref *(nuint*)p);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void store_ptr_release(void** p, void* v) => Volatile.Write(ref *(nuint*)p, (nuint)v);

        // -------------------------------------------------------
        // Compare-and-swap (strong)  →  Interlocked.CompareExchange
        // C: returns true on success, updates *expected on failure
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool cas_strong_acq_rel(nuint* p, ref nuint expected, nuint desired)
        {
            nuint prev;
            if (sizeof(nuint) == 8)
                prev = (nuint)(ulong)Interlocked.CompareExchange(ref *(long*)p, (long)(ulong)desired, (long)(ulong)expected);
            else
                prev = (nuint)(uint)Interlocked.CompareExchange(ref *(int*)p, (int)(uint)desired, (int)(uint)expected);
            if (prev == expected) return true;
            expected = prev;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool cas_weak_release(nuint* p, ref nuint expected, nuint desired)
            => cas_strong_acq_rel(p, ref expected, desired); // C# has no weak CAS, use strong

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool cas_ptr_strong_release(void** p, ref void* expected, void* desired)
        {
            nuint exp_n = (nuint)expected;
            bool ok = cas_strong_acq_rel((nuint*)p, ref exp_n, (nuint)desired);
            expected = (void*)exp_n;
            return ok;
        }

        // -------------------------------------------------------
        // Fetch-and-add / sub / or / and / exchange
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint add_acq_rel(nuint* p, nuint add)
        {
            if (sizeof(nuint) == 8)
                return (nuint)(ulong)Interlocked.Add(ref *(long*)p, (long)(ulong)add) - add;
            else
                return (nuint)(uint)Interlocked.Add(ref *(int*)p, (int)(uint)add) - add;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint or_acq_rel(nuint* p, nuint mask)
        {
            nuint old = load_relaxed(p);
            nuint neu;
            do { neu = old | mask; } while (!cas_strong_acq_rel(p, ref old, neu));
            return old;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint and_acq_rel(nuint* p, nuint mask)
        {
            nuint old = load_relaxed(p);
            nuint neu;
            do { neu = old & mask; } while (!cas_strong_acq_rel(p, ref old, neu));
            return old;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint exchange_acq_rel(nuint* p, nuint v)
        {
            if (sizeof(nuint) == 8)
                return (nuint)(ulong)Interlocked.Exchange(ref *(long*)p, (long)(ulong)v);
            else
                return (nuint)(uint)Interlocked.Exchange(ref *(int*)p, (int)(uint)v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* exchange_ptr_acq_rel(void** p, void* v)
            => (void*)exchange_acq_rel((nuint*)p, (nuint)v);

        // -------------------------------------------------------
        // Increment / decrement
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void increment_relaxed(nuint* p) => add_acq_rel(p, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void decrement_relaxed(nuint* p) => add_acq_rel(p, MI.MI_NUINT_MAX); // -= 1

        // -------------------------------------------------------
        // 64-bit int operations (for stats, which use int64_t)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long addi64_relaxed(ref long stat, long amount)
            => Interlocked.Add(ref stat, amount) - amount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void maxi64_relaxed(ref long stat, long v)
        {
            long old = Volatile.Read(ref stat);
            while (v > old)
            {
                long prev = Interlocked.CompareExchange(ref stat, v, old);
                if (prev == old) break;
                old = prev;
            }
        }

        // -------------------------------------------------------
        // Once flag (atomic bool for one-time init)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool once(ref int flag)
        {
            // Returns true the FIRST time this is called (sets from 0 to 1)
            return Interlocked.CompareExchange(ref flag, 1, 0) == 0;
        }

        // -------------------------------------------------------
        // Yield (spin-wait)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void yield()
        {
            Thread.SpinWait(1);
        }

        // -------------------------------------------------------
        // Max (for peak tracking)  -- non-atomic version for thread-local
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void maxi64_local(ref long stat, long v) { if (v > stat) stat = v; }
    }

    // -------------------------------------------------------
    // mi_lock_t helpers (spin-lock)
    // -------------------------------------------------------
    internal static unsafe class MiLock
    {
        public static bool try_acquire(mi_lock_t* lk)
        {
            return Interlocked.CompareExchange(ref lk->locked, 1, 0) == 0;
        }

        public static void acquire(mi_lock_t* lk)
        {
            while (!try_acquire(lk))
                Thread.SpinWait(4);
        }

        public static void release(mi_lock_t* lk)
        {
            Volatile.Write(ref lk->locked, 0);
        }
    }
}
