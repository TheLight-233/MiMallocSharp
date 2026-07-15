// C# translation of mimalloc/src/random.c
// ChaCha20-based cryptographically secure PRNG
using System;
using System.Runtime.CompilerServices;

namespace Mimalloc
{
    internal static unsafe class MiRandom
    {
        // -------------------------------------------------------
        // ChaCha20 core (from random.c)
        // -------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint rotl(uint x, int shift) => (x << shift) | (x >> (32 - shift));

        private static void qround(uint* x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = rotl(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = rotl(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = rotl(x[d] ^ x[a],  8);
            x[c] += x[d]; x[b] = rotl(x[b] ^ x[c],  7);
        }

        private static void chacha_block(mi_random_ctx_t* ctx)
        {
            // Copy input into scratch buffer
            uint* x = stackalloc uint[16];
            for (int i = 0; i < 16; i++) x[i] = ctx->input[i];

            // 20 rounds (10 double-rounds)
            for (int i = 0; i < MI.MI_CHACHA_ROUNDS; i += 2)
            {
                qround(x, 0, 4,  8, 12);
                qround(x, 1, 5,  9, 13);
                qround(x, 2, 6, 10, 14);
                qround(x, 3, 7, 11, 15);
                qround(x, 0, 5, 10, 15);
                qround(x, 1, 6, 11, 12);
                qround(x, 2, 7,  8, 13);
                qround(x, 3, 4,  9, 14);
            }

            // Add scrambled data to initial state
            for (int i = 0; i < 16; i++) ctx->output[i] = x[i] + ctx->input[i];
            ctx->output_available = 16;

            // Increment counter
            ctx->input[12] += 1;
            if (ctx->input[12] == 0)
            {
                ctx->input[13] += 1;
                if (ctx->input[13] == 0) ctx->input[14] += 1;
            }
        }

        private static uint chacha_next32(mi_random_ctx_t* ctx)
        {
            if (ctx->output_available <= 0)
            {
                chacha_block(ctx);
                ctx->output_available = 16;
            }
            uint result = ctx->output[16 - ctx->output_available];
            ctx->output[16 - ctx->output_available] = 0;
            ctx->output_available--;
            return result;
        }

        private static uint read32(byte* p, int idx32)
        {
            int i = 4 * idx32;
            return (uint)p[i] | ((uint)p[i+1] << 8) | ((uint)p[i+2] << 16) | ((uint)p[i+3] << 24);
        }

        private static void chacha_init(mi_random_ctx_t* ctx, byte* key, ulong nonce)
        {
            MiLibc.mi_memzero(ctx, (nuint)sizeof(mi_random_ctx_t));
            // sigma constant: "expand 32-byte k"
            byte* sigma = stackalloc byte[16];
            string s = "expand 32-byte k";
            for (int i = 0; i < 16; i++) sigma[i] = (byte)s[i];
            for (int i = 0; i < 4; i++) ctx->input[i] = read32(sigma, i);
            for (int i = 0; i < 8; i++) ctx->input[i + 4] = read32(key, i);
            ctx->input[12] = 0;
            ctx->input[13] = 0;
            ctx->input[14] = (uint)nonce;
            ctx->input[15] = (uint)(nonce >> 32);
        }

        private static void chacha_split(mi_random_ctx_t* ctx, ulong nonce, mi_random_ctx_t* ctx_new)
        {
            MiLibc.mi_memzero(ctx_new, (nuint)sizeof(mi_random_ctx_t));
            MiLibc.mi_memcpy(ctx_new->input, ctx->input, 16 * sizeof(uint));
            ctx_new->input[12] = 0;
            ctx_new->input[13] = 0;
            ctx_new->input[14] = (uint)nonce;
            ctx_new->input[15] = (uint)(nonce >> 32);
            chacha_block(ctx_new);
        }

        // -------------------------------------------------------
        // Public API (mirrors mimalloc random.c public functions)
        // -------------------------------------------------------
        public static void _mi_random_split(mi_random_ctx_t* ctx, mi_random_ctx_t* ctx_new)
        {
            chacha_split(ctx, (ulong)(nuint)ctx_new, ctx_new);
        }

        public static nuint _mi_random_next(mi_random_ctx_t* ctx)
        {
            nuint r;
            do
            {
                if (sizeof(nuint) == 4)
                    r = chacha_next32(ctx);
                else
                    r = ((nuint)chacha_next32(ctx) << 32) | chacha_next32(ctx);
            } while (r == 0);
            return r;
        }

        public static nuint _mi_os_random_weak(nuint extra_seed)
        {
            nuint x = (nuint)(ulong)MiPlatform.ClockNow() ^ extra_seed;
            nuint max = ((x ^ (x >> 17)) & 0x0F) + 1;
            for (nuint i = 0; i < max || x == 0; i++, x++)
                x = MiLibc._mi_random_shuffle(x);
            return x;
        }

        private static void mi_random_init_ex(mi_random_ctx_t* ctx, bool use_weak)
        {
            byte* key = stackalloc byte[32];
            if (use_weak || !MiPlatform.RandomBytes(key, 32))
            {
                nuint v = _mi_os_random_weak(0);
                for (int i = 0; i < 8; i++, v++)
                {
                    v = MiLibc._mi_random_shuffle(v);
                    *((uint*)key + i) = (uint)v;
                }
                ctx->weak = 1;
            }
            else
            {
                ctx->weak = 0;
            }
            chacha_init(ctx, key, (ulong)(nuint)ctx);
        }

        public static void _mi_random_init(mi_random_ctx_t* ctx)
            => mi_random_init_ex(ctx, false);

        public static void _mi_random_init_weak(mi_random_ctx_t* ctx)
            => mi_random_init_ex(ctx, true);

        public static void _mi_random_reinit_if_weak(mi_random_ctx_t* ctx)
        {
            if (ctx->weak != 0) _mi_random_init(ctx);
        }
    }
}
