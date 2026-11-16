using System;

namespace Nino.Core
{
    /// <summary>
    /// Makes Nino reuse instances of a sealed <c>[NinoType]</c> class. The generator adds to the (partial) class:
    /// <list type="bullet">
    /// <item><c>ReturnToPool()</c>: hands the instance back once you are done with it. Returning it twice throws.</item>
    /// <item>two partial methods you must implement: <c>private static partial IEnumerable&lt;T&gt; OnNinoPoolWarmup()</c>,
    /// called once before Nino first deserializes the type, whose instances go straight into the pool (return none
    /// to start empty), and <c>private partial void OnNinoPoolReturn()</c>, called by <c>ReturnToPool</c> to release
    /// what must not outlive this use (state Nino does not deserialize, resources from other pools).</item>
    /// </list>
    /// Wherever Nino deserializes the type (top level, members, collection elements) it takes a pooled instance if
    /// there is one and deserializes into it, reusing its lists, arrays and nested objects as well; otherwise it
    /// creates one as usual. Instances that reuse deserialization drops (the data holds <c>null</c>, a collection
    /// shrinks) are returned to the pool automatically.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NinoPooledAttribute : Attribute
    {
    }
}
