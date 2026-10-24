using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace Nino.Codegen
{
    /// <summary>
    /// Renders the Scriban templates embedded under <c>Templates/&lt;language&gt;/</c>. The templates only assemble
    /// text: everything they decide on comes from the target's model, given to them as variables and functions.
    /// <c>include "x.sbn"</c> in a template includes another template of the same language.
    /// </summary>
    internal sealed class TemplateRenderer
    {
        private readonly string _language;
        private readonly ScriptObject _globals = new();

        public TemplateRenderer(string language) => _language = language;

        /// <summary>A variable of the templates (its name as the templates write it, snake_case).</summary>
        public void Set(string name, object? value) => _globals.SetValue(name, value, true);

        /// <summary>A function the templates call with C#'s decisions.</summary>
        public void Function(string name, Delegate function) => _globals.Import(name, function);

        public string Render(string template)
        {
            var context = new TemplateContext
            {
                TemplateLoader = new Loader(_language),
                StrictVariables = true,
                // Recursive functions (a sequence of sequences...) nest as deep as the values do.
                RecursiveLimit = 1000,
                LoopLimit = 0,
                NewLine = "\n",
            };
            context.PushGlobal(_globals);
            var parsed = Template.Parse(Read(_language, template), template);
            if (parsed.HasErrors)
                throw new InvalidOperationException($"{_language}/{template}: " +
                                                    string.Join("; ", parsed.Messages.Select(m => m.ToString())));
            return parsed.Render(context).Replace("\r\n", "\n");
        }

        /// <summary>The text of an embedded template.</summary>
        internal static string Read(string language, string template)
        {
            string name = $"templates/{language}/{template}";
            var assembly = typeof(TemplateRenderer).Assembly;
            string? resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.Replace('\\', '/') == name);
            if (resource == null)
                throw new FileNotFoundException($"No template {name}.");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }

        private sealed class Loader : ITemplateLoader
        {
            private readonly string _language;

            public Loader(string language) => _language = language;

            public string GetPath(TemplateContext context, SourceSpan callerSpan, string templateName) => templateName;

            public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
                Read(_language, templatePath);

            public System.Threading.Tasks.ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan,
                string templatePath) => new(Load(context, callerSpan, templatePath));
        }
    }
}
