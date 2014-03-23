using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Roslyn.Compilers;
using Roslyn.Compilers.CSharp;
using Roslyn.Services;
using Roslyn.Services.CSharp;
using System.Diagnostics;
using System.IO;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp.Metadata.PE;
using Roslyn.Scripting;
using Roslyn.Scripting.CSharp;

namespace ContextFeatureExtraction
{
    class CodeWalker
    {
        public static List<MetadataReference> appReflist = new List<MetadataReference>();

        public CodeWalker()
        {        
            var mscorlib = MetadataReference.CreateAssemblyReference("mscorlib");
            appReflist.Add(mscorlib);

            // Find all the application API dll references files
            IEnumerable<String> appLibFiles = Directory.EnumerateFiles(IOFile.FolderPath,
                "*.dll", SearchOption.AllDirectories);
            foreach (var libFile in appLibFiles)
            {   
                // Add application API libs by new MetadataFileReference(libFile) 
                var reference = new MetadataFileReference(libFile);
                appReflist.Add(reference);
            }

        }

        public void LoadByInputMode(String inputMode, String filePath)
        {
            Logger.Log("Input mode: " + inputMode);
            switch (inputMode)
            {
                case "ByFolder":
                    LoadByFolder(filePath);
                    break;
                case "ByTxtFile":
                    LoadByTxtFile(filePath);
                    break;
                default:
                    Logger.Log("Invalid input mode. (Select ByFolder/ByTxtFile)");
                    Console.ReadKey();
                    return;
            }
        }

        public static void LoadByFolder(String folderPath)
        {
            Logger.Log("Loading from folder: " + folderPath);
            IEnumerable<String> FileNames = Directory.EnumerateFiles(folderPath, "*.cs",
                SearchOption.AllDirectories);
            int numFiles = FileNames.Count();
            Logger.Log("Loading " + numFiles + " *.cs files.");
            // parallelization
            var treeAndModelList = FileNames.AsParallel()
                .Select(fileName => LoadSourceFile(fileName))
                .ToList();

            var treeAndModelDic = new Dictionary<SyntaxTree, SemanticModel>();
            foreach (var treeAndModel in treeAndModelList)
            {
                treeAndModelDic.Add(treeAndModel.Item1, treeAndModel.Item2);
            }
            var compilation = BuildCompilation(treeAndModelDic.Keys.ToList());

            CodeAnalyzer.AnalyzeAllTrees(treeAndModelDic, compilation);
        }

        public static void LoadByTxtFile(String folderPath)
        {
            String txtFilePath = IOFile.CompleteFileName("AllSource.txt");
            Logger.Log("Load from txt file: " + txtFilePath);

            String content = "";
            try
            {
                using (StreamReader sr = new StreamReader(txtFilePath))
                {
                    content = sr.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Logger.Log("Txt file may not exist.");
                Logger.Log(e);
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }

            var tree = SyntaxTree.ParseText(content);
            var model = GetSemanticInfo(tree);
            var treeAndModelDic = new Dictionary<SyntaxTree, SemanticModel>();
            treeAndModelDic.Add(tree, model);
            var compilation = BuildCompilation(new List<SyntaxTree> { tree });
            CodeAnalyzer.AnalyzeAllTrees(treeAndModelDic, compilation);
        }

        public static Tuple<SyntaxTree, SemanticModel> LoadSourceFile(String sourceFile)
        {
            Logger.Log("Loading source file: " + sourceFile);
            //            if (InputFileName.Split('\\').Last().Contains("Log"))
            //            {
            //                fileContent = "";
            //            }
            var tree = SyntaxTree.ParseFile(sourceFile);
            var model = GetSemanticInfo(tree);

            return new Tuple<SyntaxTree, SemanticModel>(tree, model);
        }

        public static SemanticModel GetSemanticInfo(SyntaxTree tree)
        {
            // Collect the system API references from using directives
            List<MetadataReference> reflist = new List<MetadataReference>();
            var root = tree.GetRoot();
            var usingList = root.DescendantNodes().OfType<UsingDirectiveSyntax>();

            List<String> allLibNames = new List<string>();
            foreach (var usingLib in usingList)
            {
                String libName = usingLib.Name.ToString();
                MetadataReference reference = null;
                while (libName != "" && reference == null)
                {
                    if (allLibNames.Contains(libName)) break;

                    allLibNames.Add(libName);

                    try
                    {
                        // Add system API libs by MetadataReference.CreateAssemblyReference
                        reference = MetadataReference.CreateAssemblyReference(libName);
                    }
                    catch (Exception)
                    {
                        // handle cases that "libName.dll" does not exist
                        int idx = libName.LastIndexOf('.');
                        if (idx == -1)
                        {
                            libName = "";
                            break;
                        }
                        libName = libName.Substring(0, idx);                     
                    }
                }

                if (reference != null)
                {
                    Logger.Log("Adding reference: " + libName + ".dll");
                    reflist.Add(reference);
                }
            }

            reflist.AddRange(appReflist);
            var compilationOptions = new CompilationOptions(outputKind: OutputKind.WindowsApplication);
            var compilation = Compilation.Create(
                outputName: "ACompilation",
                options: compilationOptions,
                syntaxTrees: new[] { tree },
                references: reflist);

            var model = compilation.GetSemanticModel(tree);

            return model;
        }

        public static Compilation BuildCompilation(List<SyntaxTree> treelist)
        {
            List<MetadataReference> reflist = new List<MetadataReference>();

            // Collect the system API references from using directives
            var totalUsings = treelist.AsParallel().Select(
                    tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>());
            // transfer to a list
            var totalUsingList = totalUsings.SelectMany(x => x).ToList();
            // create metareference  
            List<String> allLibNames = new List<string>();
            foreach (var usingLib in totalUsingList)
            {
                String libName = usingLib.Name.ToString();
                MetadataReference reference = null;
                while (libName != "" && reference == null)
                {
                    if (allLibNames.Contains(libName)) break;

                    allLibNames.Add(libName);  

                    try
                    {
                        // Add system API libs by MetadataReference.CreateAssemblyReference
                        reference = MetadataReference.CreateAssemblyReference(libName);
                    }
                    catch (Exception)
                    {
                        // handle cases that "libName.dll" does not exist
                        int idx = libName.LastIndexOf('.');
                        if (idx == -1)
                        {
                            libName = "";
                            break;
                        }
                        libName = libName.Substring(0, idx);
                    }
                }

                if (reference != null)
                {
                    Logger.Log("Adding reference: " + libName + ".dll");
                    reflist.Add(reference);
                }
            }

            reflist.AddRange(appReflist);
            var compilationOptions = new CompilationOptions(outputKind: OutputKind.WindowsApplication);
            var compilation = Compilation.Create(
                outputName: "AllCompilation",
                options: compilationOptions,
                syntaxTrees: treelist,
                references: reflist);

            return compilation;
        }

    }

    /// <summary>
    /// Remove the try-catch block of a code snippet
    /// </summary>
    public class TryStatementRemover : SyntaxRewriter
    {
        public override SyntaxNode VisitTryStatement(TryStatementSyntax node)
        {
            // rewrite to null
            return null;
        }
    }

    class TryStatementSkipper : SyntaxWalker
    {
        public readonly List<InvocationExpressionSyntax> invokedMethods = new List<InvocationExpressionSyntax>();

        public override void VisitTryStatement(TryStatementSyntax node)
        {
            // skip over
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            this.invokedMethods.Add(node);
        }
    }

}


