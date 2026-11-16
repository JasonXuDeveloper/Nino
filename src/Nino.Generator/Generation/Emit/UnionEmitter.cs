using System.Collections.Generic;
using System.Globalization;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Emits Nino type-id dispatch over the closed set of case formatters of a C# union.</summary>
internal static class UnionEmitter
{
    public static void Emit(CodeWriter w, TypeModel model, FormatterNames names)
    {
        string type = model.TypeName;
        var cases = model.Cases;
        var seen = new HashSet<int>();
        bool checkIds = false;
        for (int i = 0; i < cases.Length; i++)
        {
            if (cases[i].TypeId is not { } id)
            {
                w.Line($"private static readonly int s_id{i} = {Core}NinoTypeId.Of(typeof({cases[i].TypeName}));");
                checkIds = true;
            }
            else if (!seen.Add(id))
            {
                checkIds = true;
            }
        }

        if (checkIds)
            EmitIdCheck(w, cases);

        w.Line(names.Inline);
        w.Line($"{Signature.Serialize(type)} => WriteBody(value, ref writer);");
        w.Line(names.Inline);
        w.Line($"{Signature.Deserialize(type)} => ReadBody(ref reader, ref value);");
        w.Line();

        w.Open(Signature.WriteBody(type));
        w.Line("object payload = value.Value;");
        w.Open("if (payload == null)");
        w.Line($"writer.UnsafeWrite({NinoWire.NullTypeId});");
        w.Line("return;");
        w.Close();
        if (checkIds)
        {
            w.Line("if (!s_uniqueIds)");
            w.IndentedLine($"throw new global::System.InvalidOperationException(\"Nino: union {type} has colliding case type ids.\");");
        }
        w.Line($"writer.EnterStruct(typeof({type}));");
        w.Open("try");
        w.Line("__WriteCase(payload, ref writer);");
        w.Close();
        w.Open("finally");
        w.Line("writer.ExitStruct();");
        w.Close();
        w.Close();
        w.Line();

        w.Open(Signature.ReadBody(type));
        w.Line("int id = reader.UnsafeRead<int>();");
        w.Open($"if (id == {NinoWire.NullTypeId})");
        w.Line("value = default;");
        w.Line("return;");
        w.Close();
        if (checkIds)
        {
            w.Line("if (!s_uniqueIds)");
            w.IndentedLine("throw __InvalidCase(id);");
        }
        w.Line($"reader.EnterStruct(typeof({type}));");
        w.Open("try");
        w.Line($"{type} next;");
        w.Open("switch (id)");
        seen.Clear();
        for (int i = 0; i < cases.Length; i++)
        {
            if (cases[i].TypeId is { } id && seen.Add(id))
                w.Line($"case {Id(cases[i], i)}: next = __ReadExact{i}(ref reader); break;");
        }
        for (int i = 0; i < cases.Length; i++)
        {
            if (cases[i].TypeId == null)
                w.Line($"case var _ when id == s_id{i}: next = __ReadExact{i}(ref reader); break;");
        }
        w.Line("default:");
        w.IndentedLine("throw __InvalidCase(id);");
        w.Close();
        w.Line("if (next.Value == null)");
        w.IndentedLine("throw __InvalidCase(id);");
        w.Line("value = next;");
        w.Close();
        w.Open("finally");
        w.Line("reader.ExitStruct();");
        w.Close();
        w.Close();
        w.Line();

        EmitWriteDispatch(w, cases);
        w.Line();
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            w.Line(names.NoInline);
            w.Open($"private static {type} __ReadExact{i}(ref {Core}Reader reader)");
            w.Line($"{item.TypeName} item = default;");
            w.Line($"default({item.Formatter}).Deserialize(ref reader, ref item);");
            w.Line($"return new {type}(item);");
            w.Close();
        }
        w.Line();
        w.Line(names.NoInline);
        w.Line($"private static {Core}NinoDataException __InvalidCase(int id) => new {Core}NinoDataException($" +
               $"\"Nino: invalid union case type id {{id}} for {{typeof({type})}}.\");");
    }

    private static void EmitWriteDispatch(CodeWriter w, EquatableArray<UnionCase> cases)
    {
        bool useTable = cases.Length > DispatchEmitter.MaxWriteChain;
        if (useTable)
        {
            const string dictionary = "global::System.Collections.Generic.Dictionary<global::System.Type, int>";
            w.Line($"private static readonly {dictionary} s_writeCases = __BuildWriteCases();");
            w.Open($"private static {dictionary} __BuildWriteCases()");
            w.Line($"var cases = new {dictionary}({cases.Length});");
            for (int i = 0; i < cases.Length; i++)
                w.Line($"cases.TryAdd(typeof({cases[i].MatchType}), {i});");
            w.Line("return cases;");
            w.Close();
            w.Line();
        }

        w.Open($"private static void __WriteCase(object payload, ref {Core}Writer writer)");
        w.Line("var runtimeType = payload.GetType();");
        // A broad case such as object must not hide an explicitly declared case of the actual type.
        if (useTable)
        {
            w.Open("if (s_writeCases.TryGetValue(runtimeType, out int index))");
            w.Open("switch (index)");
            for (int i = 0; i < cases.Length; i++)
                w.Line($"case {i}: __WriteExact{i}(payload, ref writer); return;");
            w.Close();
            w.Close();
        }
        else
        {
            for (int i = 0; i < cases.Length; i++)
                w.Line($"if (runtimeType == typeof({cases[i].MatchType})) {{ __WriteExact{i}(payload, ref writer); return; }}");
        }
        for (int i = 0; i < cases.Length; i++)
        {
            if (cases[i].CanMatchSubtype)
                w.Line($"if (payload is {cases[i].MatchType}) {{ __WriteExact{i}(payload, ref writer); return; }}");
        }
        w.Line("throw new global::System.InvalidOperationException($\"Nino: {runtimeType} is not a declared union case.\");");
        w.Close();

        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            w.Open($"private static void __WriteExact{i}(object payload, ref {Core}Writer writer)");
            w.Line($"writer.UnsafeWrite({Id(item, i)});");
            w.Line($"default({item.Formatter}).Serialize(({item.TypeName})payload, ref writer);");
            w.Close();
        }
    }

    private static void EmitIdCheck(CodeWriter w, EquatableArray<UnionCase> cases)
    {
        w.Line("private static readonly bool s_uniqueIds = __CheckCaseIds();");
        w.Open("private static bool __CheckCaseIds()");
        w.Line("var ids = new global::System.Collections.Generic.Dictionary<int, global::System.Type>();");
        for (int i = 0; i < cases.Length; i++)
        {
            string id = Id(cases[i], i);
            w.Line($"if (!ids.TryAdd({id}, typeof({cases[i].TypeName})) && ids[{id}] != typeof({cases[i].TypeName}))");
            w.IndentedLine("return false;");
        }
        w.Line("return true;");
        w.Close();
        w.Line();
    }

    private static string Id(UnionCase item, int index) =>
        item.TypeId?.ToString(CultureInfo.InvariantCulture) ?? $"s_id{index}";
}
