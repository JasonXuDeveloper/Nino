using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    public sealed partial class TypeScriptTarget
    {
        /// <summary>
        /// A C# union's cases as TypeScript writes them: a plain union of the cases' types (<c>number | string</c>)
        /// when a writer can tell every case's values apart at run time (<see cref="RuntimeTest"/>), each case with its
        /// test, class cases most derived first; otherwise a union tagged with each case's name (<c>{ case, value }</c>).
        /// A C# union is a struct wrapping one value whose case is the value's runtime type, which a plain TypeScript
        /// union expresses exactly where TypeScript keeps the types apart; where it does not (an <c>int</c> and a
        /// <c>long</c> are both numbers, two lists both arrays, an interface has no constructor), the case is a tag.
        /// </summary>
        internal (bool Tagged, List<TsCase> Cases) UnionForm(TypeModel union)
        {
            var cases = union.Cases.Select(c => (Model: c, Shape: ShapeIn(union, c.Value))).ToList();
            var tests = cases.Select(c => RuntimeTest(c.Shape)).ToList();
            bool tagged = tests.Any(t => t == null) ||
                          tests.Select(t => t!.Value.Key).Distinct(System.StringComparer.Ordinal).Count() != tests.Count;
            var list = cases.Select((c, i) => new TsCase(c.Model, c.Shape) { Test = tests[i]?.Test }).ToList();
            if (tagged)
                return (true, list);
            // A subclass before its base, so that its values take its own case (.NET's writer tries the exact type
            // first too): the class cases are reordered among their own places, the others keep theirs.
            var places = Enumerable.Range(0, list.Count)
                .Where(i => tests[i]!.Value.Key.StartsWith("class:", System.StringComparison.Ordinal)).ToList();
            var classes = places.OrderByDescending(i => list[i].Model.Clr.IsValueType ? 0 : Depth(list[i].Model.Clr))
                .ThenBy(i => i).Select(i => list[i]).ToList();
            var ordered = list.ToList();
            for (int i = 0; i < places.Count; i++)
                ordered[places[i]] = classes[i];
            return (false, ordered);
        }

        private static int Depth(System.Type type)
        {
            int depth = 0;
            for (var t = type.BaseType; t != null; t = t.BaseType)
                depth++;
            return depth;
        }

        /// <summary>
        /// What tells a value of <paramref name="s"/> apart at run time, as a key (two cases with one key cannot be told
        /// apart) and a test of <c>v</c>; null where nothing does (<c>object</c>, an interface, a type parameter).
        /// </summary>
        private (string Key, string Test)? RuntimeTest(TsShape s)
        {
            static (string, string) Of(string type) => (type, $"typeof v === \"{type}\"");
            (string, string) Instance(string name) => ("class:" + name, $"v instanceof {name}");
            switch (s.Kind)
            {
                case "prim":
                    return s.Primitive switch
                    {
                        "bool" => Of("boolean"),
                        "i64" or "u64" or "timeSpan" => Of("bigint"),
                        "char" or "decimal" or "guid" => Of("string"),
                        "dateTime" => Instance("Date"),
                        _ => Of("number"),
                    };
                case "enum":
                case "nullable":
                    return RuntimeTest(s.Inner!);
                case "string":
                case "utf8":
                    return Of("string");
                case "bytes":
                    return Instance("Uint8Array");
                case "seq":
                    return s.ArrayType != null ? Instance(s.ArrayType)
                        : s.Set ? Instance("Set")
                        : ("Array", "Array.isArray(v)");
                case "map":
                    return Instance("Map");
                case "tuple":
                case "fixedtuple":
                    return ("Array", "Array.isArray(v)");
                case "rawstruct":
                    return Instance(Name(s.Struct!));
                case "type" when s.Header is "none" or "null" or "ref":
                case "type" when s.Header == "typeid" && !s.Type!.IsInterface:
                case "generic" when s.Header is "none" or "null":
                    return Instance(Name(s.Type!));
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether a union held as <c>object</c> needs a class of its own (<c>XObject</c>, holding the union's value):
        /// an <c>object</c> slot tells types apart by their constructors, which a plain or tagged union has none of.
        /// </summary>
        internal bool UnionObjects => _usesObject;
    }
}
