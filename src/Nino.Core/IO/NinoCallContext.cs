using System;

namespace Nino.Core
{
    /// <summary>
    /// The deferred bodies of one call, and what <see cref="NinoReadContext"/> and <see cref="NinoWriteContext"/>
    /// do with them when the call ends.
    /// </summary>
    internal abstract class NinoCallContext
    {
        public NinoDeferrals Deferrals;

        /// <summary>Hands <see cref="Deferrals"/> back to their cache.</summary>
        protected void ReturnDeferrals()
        {
            if (Deferrals == null)
                return;
            NinoDeferrals.Return(Deferrals);
            Deferrals = null;
        }
    }

    /// <summary>
    /// What a <see cref="Reader"/> and the readers made from it share beyond their position: the back references
    /// and the deferred bodies of one deserialization call. Rented on first use (one cached instance per thread),
    /// so calls that track nothing and never nest deep touch nothing.
    /// </summary>
    internal sealed class NinoReadContext : NinoCallContext
    {
        [ThreadStatic] private static NinoReadContext t_cached;

        public NinoReferenceReadTable Refs;

        /// <summary>Open bodies of structs that can hold themselves (<see cref="Reader.EnterStruct"/>).</summary>
        public int Structs;

        public static NinoReadContext Rent() => NinoThreadCache.Take(ref t_cached) ?? new NinoReadContext();

        public static void Return(NinoReadContext context)
        {
            context.ReturnDeferrals();
            context.Structs = 0;

            if (context.Refs != null)
            {
                NinoReferenceReadTable.Return(context.Refs);
                context.Refs = null;
            }

            t_cached = context;
        }
    }

    /// <summary>
    /// What a <see cref="Writer"/> keeps beyond its buffer and position: the back references and the deferred
    /// bodies of one serialization call. Rented on first use (one cached instance per thread), so calls that track
    /// nothing and never nest deep touch nothing.
    /// </summary>
    internal sealed class NinoWriteContext : NinoCallContext
    {
        [ThreadStatic] private static NinoWriteContext t_cached;

        public NinoReferenceWriteTable Refs;

        /// <summary>Open bodies of structs and collections that can hold themselves (<see cref="Writer.EnterStruct"/>).</summary>
        public int Structs;

        public static NinoWriteContext Rent() => NinoThreadCache.Take(ref t_cached) ?? new NinoWriteContext();

        public static void Return(NinoWriteContext context)
        {
            context.ReturnDeferrals();
            context.Structs = 0;

            if (context.Refs != null)
            {
                NinoReferenceWriteTable.Return(context.Refs);
                context.Refs = null;
            }

            t_cached = context;
        }
    }
}
