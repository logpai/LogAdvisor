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
            GetCodeStatistics(treeList, compilation);      
        }

        public static void LoadByTxtFile(String folderPath)
        {
            String txtFilePath = IOFileProcessing.CompleteFileName("AllSource.txt");
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
            var treeList = new List<SyntaxTree>(){ tree }; 
            var compilation = BuildCompilation(treeList);
            GetCodeStatistics(treeList, compilation);

            Program.patternMatchRule.PrintMatchedResults();
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

        public static void GetCodeStatistics(List<SyntaxTree> treeList, Compilation compilation)
        {
            // statistics
            int numFiles = treeList.Count;
            var treeNode = treeList.Select(tree => tree.GetRoot().DescendantNodes().Count());
            Logger.Log("Num of syntax nodes: " + treeNode.Sum());
            Logger.Log("Num of source files: " + numFiles);
            List<CodeStatistics> codeStatsList = treeList.AsParallel()
                .Select(tree => CodeAnalyzer.AnalyzeSingleTree(tree, compilation)).ToList();

            CodeStatistics totalStats = new CodeStatistics();
            Dictionary<String, int> exceptionTypeDic = new Dictionary<String, int>();
            var sb = new StringBuilder(treeList[0].Length * treeList.Count); //initial length
            for (int i = 0; i < numFiles; i++)
            {
                sb.Append(treeList[i].GetText());
                String fileName = treeList[i].FilePath;
                Program.FolderFileInfo.Add(new FileInfo(fileName,
                    codeStatsList[i].CodeStats["NumLOC"]));
                // get the total statistics
                Tools.MergeDic<String>(ref totalStats.CodeStats, codeStatsList[i].CodeStats);
                Tools.MergeDic<String>(ref exceptionTypeDic, codeStatsList[i].ExceptionTypeDic);
            }

            // Log statistics
            foreach (var stat in totalStats.CodeStats.Keys)
            {
                Logger.Log(stat + ": " + totalStats.CodeStats[stat]);
            }
            Logger.Log("NumExceptionType: " + exceptionTypeDic.Count);

            // Save all the source code into a txt file
            String txtFilePath = IOFileProcessing.CompleteFileName("AllSource.txt");
            using (StreamWriter sw = new StreamWriter(txtFilePath))
            {
                sw.Write(sb.ToString());
            }
        }

        public static Compilation BuildCompilation(List<SyntaxTree> treelist)
        {
            List<String> allLibNameList = new List<String>();
            List<MetadataReference> reflist = new List<MetadataReference>();

            // Add all the application API references
            IEnumerable<String> appLibFiles = Directory.EnumerateFiles(IOFileProcessing.FolderPath,
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

    class VisitSyntaxNode : SyntaxWalker
    {
        public IMatchListsForMultiplePatterns thisPatternCollection;
        public SemanticModel semanticModel;

        public VisitSyntaxNode(SemanticModel model, IMatchListsForMultiplePatterns patternMatchRule)
        {
            semanticModel = model;
            thisPatternCollection = patternMatchRule;
        }

        /// <summary>
        /// Override the visit methods to process each statement when traversing the code
        /// !!!This Line Matters!!! Make sure the base method is called. 
        /// </summary>
        /// <param name="node"></param>
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            base.VisitMethodDeclaration(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

        public override void VisitThrowStatement(ThrowStatementSyntax node)
        {
            base.VisitThrowStatement(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }
        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            base.VisitCatchClause(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            base.VisitIfStatement(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            base.VisitSwitchStatement(node);
            thisPatternCollection.CheckMutiplePatternsAndRecordMatches(node, semanticModel);
        }

    }
}


