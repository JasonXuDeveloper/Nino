// Regenerates the source files of Nino.Core that are produced by a template:
//     dotnet run --project tools/CodeGen [-- <repository root>]
// The repository root defaults to the nearest directory above the working directory that contains src/Nino.Core.
using System;
using System.IO;
using System.Text;

namespace Nino.CodeGen
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRoot(Directory.GetCurrentDirectory());
            if (root == null || !Directory.Exists(Path.Combine(root, "src", "Nino.Core")))
            {
                Console.Error.WriteLine("Cannot find the repository root (a directory containing src/Nino.Core).");
                return 1;
            }

            Write(root, TupleFormattersTemplate.OutputPath, TupleFormattersTemplate.Generate());
            Write(root, MultiDimFormattersTemplate.OutputPath, MultiDimFormattersTemplate.Generate());
            return 0;
        }

        private static string FindRoot(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Nino.Core")))
                    return dir.FullName;
            }

            return null;
        }

        private static void Write(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                Console.WriteLine($"{relativePath}: up to date");
                return;
            }

            File.WriteAllText(path, content, new UTF8Encoding(false));
            Console.WriteLine($"{relativePath}: written");
        }
    }
}
