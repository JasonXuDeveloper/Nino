using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests
{
    /// <summary>
    /// The table NinoParameterMarks uses where a runtime keeps no attributes on generic parameters (IL2CPP) lists every
    /// [NinoRaw], unmanaged and [NinoCreated] parameter of Nino.Core's formatters, as the attributes do here.
    /// </summary>
    [TestClass]
    public class NinoParameterMarksTests
    {
        [TestMethod]
        public void TableMatchesTheAttributes()
        {
            Assert.IsTrue(NinoParameterMarks.Visible);
            var parameters = typeof(NinoSerializer).Assembly.GetTypes()
                .Where(t => t.IsGenericTypeDefinition && t.Namespace == "Nino.Core" && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INinoFormatter<>)))
                .SelectMany(t => t.GetGenericArguments()).ToArray();
            string Name(System.Type p) => $"{p.DeclaringType!.Name}[{p.GenericParameterPosition}]";
            var marked = parameters.Select(p => $"{Name(p)} raw={NinoParameterMarks.Raw(p)} " +
                                                $"unmanaged={NinoParameterMarks.Unmanaged(p)} created={NinoParameterMarks.Created(p)}")
                .OrderBy(s => s).ToArray();
            var table = parameters.Select(p => $"{Name(p)} raw={NinoParameterMarks.InTable(p, true)} " +
                                               $"unmanaged={NinoParameterMarks.InTable(p, true)} created={NinoParameterMarks.InTable(p, false)}")
                .OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(marked, table, string.Join("\n", marked));
        }
    }
}
