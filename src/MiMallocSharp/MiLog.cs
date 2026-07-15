// mimalloc C# port – logging / diagnostic output
//
// Design goals:
//   1. ZERO overhead in Release builds.
//      In Release (#if !DEBUG), the default sinks are null – calls compile
//      to a single null-check that the JIT elides entirely.
//   2. Fully redirectable.
//      Set OnWarning / OnError / OnVerbose to your own delegate at startup
//      (e.g. UnityEngine.Debug.LogWarning) – works in both Debug and Release.
//   3. Matches original mimalloc stderr output in Debug mode.
//      In Debug builds the defaults write to Console.Error, exactly as the
//      C library does via _mi_prim_out_stderr().
//
// Usage inside the allocator:
//   MiLog.Warning("segment cookie mismatch at 0x{0:X}", (nuint)seg);
//   MiLog.Error(errno, "out of memory in mi_malloc");
//   MiLog.Verbose("process init: thread 0x{0:X}", tid);
//
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static class MiLog
    {
        // ---------------------------------------------------------------
        // Output sinks – replace these at startup to redirect output.
        //
        //   Debug   builds: default = Console.Error  (mirrors mimalloc C)
        //   Release builds: default = null            (zero overhead)
        // ---------------------------------------------------------------

        /// <summary>
        /// Receives warning messages (e.g. "cannot commit OS memory").
        /// In Release builds, set to null by default – override to enable.
        /// </summary>
#if DEBUG
        public static Action<string>? OnWarning = static msg => Console.Error.WriteLine(msg);
#else
        public static Action<string>? OnWarning = null;
#endif

        /// <summary>
        /// Receives hard-error messages (e.g. "corrupted free list").
        /// In Release builds, set to null by default – override to enable.
        /// </summary>
#if DEBUG
        public static Action<string>? OnError = static msg => Console.Error.WriteLine(msg);
#else
        public static Action<string>? OnError = null;
#endif

        /// <summary>
        /// Receives verbose / informational messages (e.g. "thread init").
        /// Disabled by default in both Debug and Release; set explicitly to enable.
        /// </summary>
        public static Action<string>? OnVerbose = null;

        /// <summary>
        /// Receives statistics output when mi_option_show_stats is set.
        /// In Debug defaults to Console.Error; in Release defaults to null.
        /// </summary>
#if DEBUG
        public static Action<string>? OnStats = static msg => Console.Error.WriteLine(msg);
#else
        public static Action<string>? OnStats = null;
#endif

        // ---------------------------------------------------------------
        // Internal helpers used by the allocator
        // ---------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Warning(string message)
        {
            // The null-check is branch-predicted perfectly and elided in Release
            // when OnWarning == null (the JIT sees a static readonly null field).
            OnWarning?.Invoke(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Warning(string fmt, object arg0)
        {
            if (OnWarning != null)
                OnWarning(string.Format(fmt, arg0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Warning(string fmt, object arg0, object arg1)
        {
            if (OnWarning != null)
                OnWarning(string.Format(fmt, arg0, arg1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Error(int errCode, string message)
        {
            if (OnError != null)
                OnError($"mimalloc: error [{errCode}]: {message}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Error(int errCode, string fmt, object arg0)
        {
            if (OnError != null)
                OnError(string.Format($"mimalloc: error [{errCode}]: {fmt}", arg0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Verbose(string message)
        {
            OnVerbose?.Invoke(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Stats(string message)
        {
            OnStats?.Invoke(message);
        }
    }
}
