using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator;

public abstract class NinoGenerator
{
    protected Compilation Compilation;
    protected List<ITypeSymbol> NinoSymbols;

    protected NinoGenerator(Compilation compilation, List<ITypeSymbol> ninoSymbols)
    {
        Compilation = compilation;
        NinoSymbols = ninoSymbols.ToList(); //copy
    }

    protected abstract void Generate(SourceProductionContext spc);

    public void Execute(SourceProductionContext spc)
    {
        try
        {
            Generate(spc);
        }
        catch (System.Exception e)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                new DiagnosticDescriptor("NINO001", "Nino Generator",
                    $"An error occurred while generating code: {e.Message}, {e.StackTrace}",
                    "Nino.Generator",
                    DiagnosticSeverity.Error, true), Location.None));
        }
    }
}