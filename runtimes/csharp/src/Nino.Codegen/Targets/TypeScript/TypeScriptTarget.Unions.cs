using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    public sealed partial class TypeScriptTarget
    {
        /// <summary>
        /// A C# union's cases as TypeScript writes them. Use the value itself for the first case that a runtime test
        /// identifies. Other cases sharing that test, or without a runtime test of their own, keep a case tag.
        /// </summary>
        internal List<TsCase> UnionCases(TypeModel union)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cases = new List<TsCase>(union.Cases.Count);
            var places = new List<int>();
            foreach (var model in union.Cases)
            {
                var shape = ShapeIn(union, model.Value);
                var test = RuntimeTest(shape);
                var result = new TsCase(model, shape);
                if (test is { } t && seen.Add(t.Key))
                {
                    result.Test = t.Test;
                    if (t.Key.StartsWith("class:", StringComparison.Ordinal))
                        places.Add(cases.Count);
                }
                cases.Add(result);
            }

            // A subclass before its base, so its values take its own case. Only reorder the direct class cases.
            var classes = places.OrderByDescending(i => cases[i].Model.Clr.IsValueType ? 0 : Depth(cases[i].Model.Clr))
                .Select(i => cases[i]).ToList();
            for (int i = 0; i < places.Count; i++)
                cases[places[i]] = classes[i];
            return cases;
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
