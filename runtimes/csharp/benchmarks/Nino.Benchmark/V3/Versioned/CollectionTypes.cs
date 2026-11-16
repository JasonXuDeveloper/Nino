#nullable disable
namespace Nino.Benchmark;

// v3's generator writes a collection's serializers only for the collection types this project's source names (its
// CollectionGenerator collects every generic name, array type and tuple type in the compilation): V3/VersionTolerantTest.cs
// writes and reads TolerantClass[] through this project's NinoGen, so the type is named here.
internal static class CollectionTypes
{
    internal static readonly System.Type[] Named = [typeof(TolerantClass[])];
}
