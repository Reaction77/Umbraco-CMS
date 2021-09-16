using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyModel;

namespace Umbraco.Cms.Infrastructure.ModelsBuilder
{
    public class RoslynCompiler
    {
        public const string GeneratedAssemblyName = "ModelsGeneratedAssembly";

        private readonly OutputKind _outputKind;
        private readonly CSharpParseOptions _parseOptions;
        private readonly IEnumerable<MetadataReference> _refs;

        /// <summary>
        /// Initializes a new instance of the <see cref="RoslynCompiler"/> class.
        /// </summary>
        /// <remarks>
        /// Roslyn compiler which can be used to compile a c# file to a Dll assembly
        /// </remarks>
        public RoslynCompiler()
        {
            _outputKind = OutputKind.DynamicallyLinkedLibrary;
            _parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);  // What languageversion should we default to?

            // In order to dynamically compile the assembly, we need to add all refs from our current
            // application. This will also add the correct framework dependencies and we won't have to worry
            // about the specific framework that is currently being run.
            // This was borrowed from: https://github.com/dotnet/core/issues/2082#issuecomment-442713181
            // because we were running into the same error as that thread because we were either:
            // - not adding enough of the runtime dependencies OR
            // - we were explicitly adding the wrong runtime dependencies
            // ... at least that the gist of what I can tell.
            MetadataReference[] refs =
                DependencyContext.Default.CompileLibraries
                .SelectMany(cl => cl.ResolveReferencePaths())
                .Select(asm => MetadataReference.CreateFromFile(asm))
                .ToArray();

            _refs = refs.ToList();
        }

        /// <summary>
        /// Compile a source file to a dll
        /// </summary>
        /// <param name="pathToSourceFile">Path to the source file containing the code to be compiled.</param>
        /// <param name="savePath">The path where the output assembly will be saved.</param>
        public void CompileToFile(string pathToSourceFile, string savePath)
        {
            var sourceCode = File.ReadAllText(pathToSourceFile);
            CSharpCompilation compilation = CreateCompilation(GeneratedAssemblyName, sourceCode);

            EmitResult emitResult = compilation.Emit(savePath);
            if (!emitResult.Success)
            {
                throw new InvalidOperationException("Roslyn compiler could not create ModelsBuilder dll:\n" +
                                                    string.Join("\n", emitResult.Diagnostics.Select(x=>x.GetMessage())));
            }
        }

        /// <summary>
        /// Compiles package xml, together with a package migration class into a single dll
        /// </summary>
        /// <param name="packageName">The Package name, will be used as the assembly name, and resource namespace</param>
        /// <param name="packageXml">The package XML to be added to the dll</param>
        /// <param name="packageMigrationCode">Source code for a package migration as a string</param>
        /// <returns>A stream containing the compiled DLL</returns>
        public Stream CompilePackage(string packageName, Stream packageXml, string packageMigrationCode)
        {
            CSharpCompilation compilation = CreateCompilation(packageName, packageMigrationCode);

            string xmlName = $"{packageName}.package.xml";
            ResourceDescription[] resources = new[]
            {
                new ResourceDescription(
                    xmlName,
                    () => packageXml,
                    true
                    )
            };

            var dllStream = new MemoryStream();
            EmitResult result = compilation.Emit(dllStream, manifestResources: resources);
            if (!result.Success)
            {
                var diagnostics = string.Join(Environment.NewLine, result.Diagnostics);
                throw new InvalidOperationException(
                    $"Roslyn compiler failed to compile package dll:{Environment.NewLine}{diagnostics}");
            }
            return dllStream;
        }

        /// <summary>
        /// Create a Compilation that can be emitted to a DLL
        /// </summary>
        /// <param name="assemblyName">Name of the output assembly</param>
        /// <param name="sourceCode">Source code to create a compilation from</param>
        /// <returns>A CSharp DLL compilation</returns>
        private CSharpCompilation CreateCompilation(string assemblyName, string sourceCode)
        {
            var sourceText = SourceText.From(sourceCode);

            SyntaxTree syntaxTree = SyntaxFactory.ParseSyntaxTree(sourceText, _parseOptions);

            return CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                references: _refs,
                options: new CSharpCompilationOptions(
                    _outputKind,
                    optimizationLevel: OptimizationLevel.Release,
                    // Not entirely certain that assemblyIdentityComparer is nececary?
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));

        }
    }
}