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

namespace CatchBlockExtraction
{
    class CodeWalker
    {
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
            var treeList = FileNames.AsParallel()
                .Select(fileName => LoadSourceFile(fileName))
                .ToList();
            var compilation = BuildCompilation(treeList);

            CodeAnalyzer.AnalyzeAllTrees(treeList, compilation);      
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
            var treeList = new List<SyntaxTree>() { tree };
            var compilation = BuildCompilation(treeList);
            CodeAnalyzer.AnalyzeAllTrees(treeList, compilation);
        }

        public static SyntaxTree LoadSourceFile(String sourceFile)
        {
            Logger.Log("Loading source file: " + sourceFile);
//            if (InputFileName.Split('\\').Last().Contains("Log"))
//            {
//                fileContent = "";
//            }
            var tree = SyntaxTree.ParseFile(sourceFile);       
            return tree;
        }

        public static Compilation BuildCompilation(List<SyntaxTree> treelist)
        {
            List<String> allLibNameList = new List<String>();
            List<MetadataReference> reflist = new List<MetadataReference>();

            // Add all the application API references
            IEnumerable<String> appLibFiles = Directory.EnumerateFiles(IOFile.FolderPath,
                "*.dll", SearchOption.AllDirectories);
            foreach (var libfile in appLibFiles)
            {
                var libName = libfile.Split('\\').Last();
                libName = libName.Substring(0, libName.LastIndexOf('.'));
                if (allLibNameList.Contains(libName)) continue;
                allLibNameList.Add(libName);
                var reference = new MetadataFileReference(libfile);
                reflist.Add(reference);
                Logger.Log("Adding reference: " + libName + ".dll");
            }

            // Collect the system API references from using directives
            var totalUsingList = treelist.AsParallel().Select(
                delegate(SyntaxTree tree)
                {
                    var root = tree.GetRoot();
                    var usingList = root.DescendantNodes().OfType<UsingDirectiveSyntax>();
                    var resultlist = usingList
                        .Where(lib => !allLibNameList.Contains(lib.Name.ToString()))
                        .Select(lib => lib.Name.ToString()).ToList();
                    return resultlist;
                });
            // transfer to String list
            List<String> libFullNameList = totalUsingList.SelectMany(x => x).ToList();
            // create metareference                
            foreach (var libFullName in libFullNameList)
            {
                String libName = libFullName;
                MetadataReference reference = null;
                while (libName != "" && reference == null)
                {
                    if (allLibNameList.Contains(libName)) break;

                    allLibNameList.Add(libName);

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

            var compilation = Compilation.Create(
                outputName: "AllCompilation",
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
            //SyntaxNode updatedNode = base.VisitTryStatement(node);
            return null;
        }
    }
}


