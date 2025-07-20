using Microsoft.CodeAnalysis;

namespace Nino.Generator.Template;

public abstract class NinoGenerator(Compilation compilation)
{
    protected readonly Compilation Compilation = compilation;

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
                    $"An error occurred while generating code: {e.GetType()} {e.Message}, {e.StackTrace}",
                    "Nino.Generator",
                    DiagnosticSeverity.Error, true), Location.None));
        }
    }
}