using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nino.Generator.Metadata;

public class NinoGraph
{
    public Dictionary<NinoType, List<NinoType>> BaseTypes { get; set; } = new();
    public Dictionary<NinoType, List<NinoType>> SubTypes { get; set; } = new();
    public List<NinoType> TopTypes { get; set; } = new();

    public NinoGraph(List<NinoType> ninoTypes)
    {
        foreach (var ninoType in ninoTypes)
        {
            List<NinoType> baseTypes = new();
            BaseTypes.Add(ninoType, baseTypes);

            // find base types
            void TraverseBaseTypes(NinoType type)
            {
                if (type.Parents.Count == 0)
                {
                    return;
                }

                foreach (var parent in type.Parents)
                {
                    if (baseTypes.Contains(parent))
                    {
                        continue;
                    }

                    baseTypes.Add(parent);
                    TraverseBaseTypes(parent);
                }
            }

            TraverseBaseTypes(ninoType);

            // if no base types, it's a top type
            if (baseTypes.Count == 0)
            {
                TopTypes.Add(ninoType);
            }
        }

        // add sub types based on base types
        foreach (var kvp in BaseTypes)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            foreach (var baseType in value)
            {
                if (!SubTypes.ContainsKey(baseType))
                {
                    SubTypes.Add(baseType, new());
                }

                if (SubTypes[baseType].Contains(key))
                {
                    continue;
                }

                SubTypes[baseType].Add(key);
            }
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine("Base Types:");
        foreach (var kvp in BaseTypes.Where(t => t.Value.Count > 0))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            sb.AppendLine(
                $"{key.TypeSymbol.ToDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.ToDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Sub Types:");
        foreach (var kvp in SubTypes.Where(t => t.Value.Count > 0))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            sb.AppendLine(
                $"{key.TypeSymbol.ToDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.ToDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Top Types:");
        sb.AppendLine(string.Join("\n",
            TopTypes.Where(t => t.Members.Count > 0).Select(x => x.TypeSymbol.ToDisplayString())));

        return sb.ToString();
    }
}