using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Roslyn.Compilers;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;
using Roslyn.Services;
using System.IO;

namespace CatchBlockExtraction
{
    static class CodeAnalyzer
    {
        public static TryStatementRemover tryblockremover = new TryStatementRemover();


        /// <summary>
        /// Analyze the code by all trees and semantic models
        /// </summary>
        /// <param name="treeList"></param>
        /// <param name="compilation"></param>
        public static void AnalyzeAllTrees(Dictionary<SyntaxTree, SemanticModel> treeAndModelDic,
            Compilation compilation)
        {
            // statistics
            int numFiles = treeAndModelDic.Count;
            var treeNode = treeAndModelDic.Keys
                .Select(tree => tree.GetRoot().DescendantNodes().Count());
            Logger.Log("Num of syntax nodes: " + treeNode.Sum());
            Logger.Log("Num of source files: " + numFiles);
            // analyze every tree simultaneously
            var codeStatsList = treeAndModelDic.Keys.AsParallel()
                .Select(tree => CodeAnalyzer.AnalyzeATree(tree, treeAndModelDic, compilation)).ToList();

            CodeStatistics allStats = new CodeStatistics(codeStatsList);

            // Log statistics
            allStats.PrintSatistics();

            // Save all the source code into a txt file
            var sb = new StringBuilder(treeAndModelDic.Keys.First().Length * numFiles); //initial length
            foreach (var stat in codeStatsList)
            {
                sb.Append(stat.Item1.GetText());
            }
            String txtFilePath = IOFile.CompleteFileName("AllSource.txt");
            using (StreamWriter sw = new StreamWriter(txtFilePath))
            {
                sw.Write(sb.ToString());
            }
        }

        /// <summary>
        /// Analyze the code statistics of a single AST
        /// </summary>
        /// <param name="tree"></param>
        /// <param name="compilation"></param>
        /// <returns></returns>
        public static Tuple<SyntaxTree, TreeStatistics> AnalyzeATree(SyntaxTree tree,
            Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            TreeStatistics stats = new TreeStatistics();
            var root = tree.GetRoot();

            // Num of LOC
            stats.CodeStats["NumLOC"] = tree.GetText().LineCount;

            // Num of call sites
            var callList = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
            stats.CodeStats["NumCall"] = callList.Count();

            // Num of logging
            var loggingList = callList.Where(call => IsLoggingStatement(call));
            int numLogging = loggingList.Count();
            stats.CodeStats["NumLogging"] = numLogging;
          
            if (numLogging > 0)
            {
                // Num of logged file
                stats.CodeStats["NumLoggedFile"] = 1;

                // Num of logged LOC
                var loggedLines = loggingList.Select(logging => logging.GetText().LineCount);
                stats.CodeStats["NumLoggedLOC"] = loggedLines.Sum();
            }

            // Num of classes
            var classList = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            int numClass =  classList.Count();
            stats.CodeStats["NumClass"] = numClass;

            // Num of methods
            var methodList = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
            int numMethod = methodList.Count();
            stats.CodeStats["NumMethod"] = numMethod;

            // Num of catch blocks
            var catchList = root.DescendantNodes().OfType<CatchClauseSyntax>();
            int numCatchBlock = catchList.Count();
            stats.CodeStats["NumCatchBlock"] = numCatchBlock;

            // Logging statistics
            if (numLogging > 0)
            {
                var loggedClasses = new Dictionary<ClassDeclarationSyntax, int>();
                var loggedMethods = new Dictionary<BaseMethodDeclarationSyntax, int>();
                var loggedCatchBlocks = new Dictionary<CatchClauseSyntax, int>();
                foreach (var logging in loggingList)    
                {
                    // Num of logged classes
                    if (numClass > 0)
                    {
                        try
                        {
                            var classNode = logging.Ancestors().OfType<ClassDeclarationSyntax>().First();
                            MergeDic<ClassDeclarationSyntax>(ref loggedClasses, 
                                new Dictionary<ClassDeclarationSyntax, int>(){{classNode, 1}});
                        }
                        catch (Exception)
                        { 
                            // ignore.
                        }
                    }

                    // Num of logged methods
                    if (numMethod > 0)
                    {
                        try
                        {
                            var method = logging.Ancestors().OfType<BaseMethodDeclarationSyntax>().First();
                            MergeDic<BaseMethodDeclarationSyntax>(ref loggedMethods,
                                new Dictionary<BaseMethodDeclarationSyntax, int>() { { method, 1 } });
                        }
                        catch (Exception)
                        {
                            // ignore.
                        }
                    }

                }

                stats.CodeStats["NumLoggedClass"] = loggedClasses.Count;
                stats.CodeStats["NumLoggedMethod"] = loggedMethods.Count;
            }

            // Statistics and features of catch blocks
            stats.CatchList = catchList
                .Select(catchblock => AnalyzeACatchBlock(catchblock, tree, treeAndModelDic,
                compilation)).ToList();

            return new Tuple<SyntaxTree, TreeStatistics>(tree, stats);
        }

        public static CatchBlock AnalyzeACatchBlock(CatchClauseSyntax catchblock, SyntaxTree tree, 
                Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            CatchBlock catchBlockInfo = new CatchBlock();
            var model = treeAndModelDic[tree];
            catchBlockInfo.ExceptionType = GetExceptionType(catchblock, model);
            catchBlockInfo.BoolFeatures["Logged"] = (FindLoggingIn(catchblock) != null) ? 1 : 0;

            var throwStatement = FindThrowIn(catchblock);
            if (throwStatement != null)
            {
                catchBlockInfo.MetaInfo["Thrown"] = throwStatement.ToString();
                catchBlockInfo.BoolFeatures["Thrown"] = 1;
            }

            var setLogicFlag = FindSetLogicFlagIn(catchblock);
            if (setLogicFlag != null)
            {
                catchBlockInfo.MetaInfo["SetLogicFlag"] = setLogicFlag.ToString();
                catchBlockInfo.BoolFeatures["SetLogicFlag"] = 1;
            }

            var returnStatement = FindReturnIn(catchblock);
            if (returnStatement != null)
            {
                catchBlockInfo.MetaInfo["Return"] = returnStatement.ToString();
                catchBlockInfo.BoolFeatures["Return"] = 1;
            }

            var recoverStatement = FindRecoverStatement(catchblock, model);
            if (recoverStatement != null)
            {
                catchBlockInfo.MetaInfo["RecoverFlag"] = recoverStatement.ToString();
                catchBlockInfo.BoolFeatures["RecoverFlag"] = 1;
            }

            var otherOperation = HasOtherOperation(catchblock, model);
            if (otherOperation != null)
            {
                catchBlockInfo.MetaInfo["OtherOperation"] = otherOperation.ToString();
                catchBlockInfo.BoolFeatures["OtherOperation"] = 1;
            }
            
            if (IsEmptyBlock(catchblock))
            {
                catchBlockInfo.BoolFeatures["EmptyBlock"] = 1;            
            }
            
            var tryBlock = catchblock.Parent as TryStatementSyntax;
            catchBlockInfo.VariableFeatures = GetVariablesAndComments(tryBlock);
            catchBlockInfo.MethodFeatures = GetAllInvokedMethodNamesByBFS(tryBlock, tree, 
                treeAndModelDic, compilation);

            return catchBlockInfo;
        }

        /// <summary>
        /// To check whether an invocation is a logging statement
        /// </summary>
        /// <param name="statement">The node to be checked: StatementSyntax or 
        /// InvocationExpressionSyntax</param>
        /// <returns></returns>
        static public bool IsLoggingStatement(SyntaxNode statement)
        {
            String logging = IOFile.TokenizeMethodName(statement.ToString());
            if (logging == null) return false;

            foreach (String notlogmethod in Config.NotLogMethods)
            {
                if (notlogmethod == "") break;
                if (logging.IndexOf(notlogmethod) > -1)
                {
                    return false;
                }
            }
            foreach (String logmethod in Config.LogMethods)
            {
                if (logging.IndexOf(logmethod) > -1)
                {
                    return true;
                }
            }
            return false;
        }

        static public InvocationExpressionSyntax FindLoggingIn(SyntaxNode codeSnippet)
        {
            InvocationExpressionSyntax loggingStatement;
            try
            {
                bool hasTryStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().Any();

                if (hasTryStatement == true)
                {
                    // remove try-catch-finally block inside
                    codeSnippet = tryblockremover.Visit(codeSnippet);        
                }
                loggingStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>().First(IsLoggingStatement);

                return loggingStatement;
            }
            catch
            {
                // has no InvocationExpressionSyntax
                return null;
            }
        }

        public static String GetExceptionType(CatchClauseSyntax catchClause, SemanticModel semanticModel)
        {
            try
            {
                TypeSyntax type = catchClause.Declaration.Type;
                TypeSymbol typeSymbol = semanticModel.GetTypeInfo(type).Type;
                if (typeSymbol != null)
                {
                    return typeSymbol.ToString(); //return "System.IO.IOException
                }
                else
                {
                    return type.ToString();
                }
            }
            catch
            {
                // the default exception type
                return "System.Exception";
            }
        }

        public static Dictionary<String, int> GetAllInvokedMethodNamesByBFS(SyntaxNode inputSnippet, 
            SyntaxTree syntaxtree, Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, 
            Compilation compilation)
        {
            Dictionary<String, int> allInovkedMethods = new Dictionary<String, int>();            
            Queue<Tuple<SyntaxNode, SyntaxTree>> codeSnippetQueue = 
                new Queue<Tuple<SyntaxNode, SyntaxTree>>();

            if (inputSnippet is TryStatementSyntax)
            {
                inputSnippet = (inputSnippet as TryStatementSyntax).Block;
            }
            codeSnippetQueue.Enqueue(new Tuple<SyntaxNode, SyntaxTree>(inputSnippet, syntaxtree));
            
            while (codeSnippetQueue.Any())
            {
                if (allInovkedMethods.Count > 50) break; // only save 20 method names

                Tuple<SyntaxNode, SyntaxTree> snippetAndTree = codeSnippetQueue.Dequeue();
                var snippet = snippetAndTree.Item1;
                var tree = snippetAndTree.Item2;
                List<InvocationExpressionSyntax> methodList = GetInvokedMethodsInACodeSnippet(snippet);
                
                foreach (var invocation in methodList)
                {
                    String methodName = null;
                    try
                    {   
                        // use a single semantic model
                        var model = treeAndModelDic[tree];
                        var symbolInfo = model.GetSymbolInfo(invocation);
                        var symbol = symbolInfo.Symbol;

                        if (symbol == null)
                        {   // recover by using the overall semantic model
                            model = compilation.GetSemanticModel(tree);
                            symbolInfo = model.GetSymbolInfo(invocation);
                            symbol = symbolInfo.Symbol;
                        }

                        if (symbol == null)
                        {
                            methodName = IOFile.TokenizeMethodName(invocation.ToString());
                        }
                        else
                        {
                            methodName = IOFile.TokenizeMethodName(symbol.ToString());
                        }
                        if (methodName.IndexOf('.') != -1)
                        {
                            methodName = methodName.Split('.').Last();
                        }
                        if (allInovkedMethods.ContainsKey(methodName))
                        {
                            allInovkedMethods[methodName]++;
                        }
                        else
                        {
                            allInovkedMethods.Add(methodName, 1);

                            // find the method declaration (go to definition)
                            var methodDeclarTupleList = treeAndModelDic.Keys.AsParallel()
                                .Select(
                                delegate(SyntaxTree searchtree)
                                {
                                    var root = searchtree.GetRoot();
                                    var methodDeclarList = root.DescendantNodes()
                                        .OfType<MethodDeclarationSyntax>()
                                        .Where(m => m.Identifier.ValueText == methodName)
                                        .ToList();
                                    var semanticModel = treeAndModelDic[searchtree];
                                    var semanticModelBackup = compilation.GetSemanticModel(searchtree);
                                    foreach (var mdeclar in methodDeclarList)
                                    {
                                        Symbol methodSymbol;
                                        try
                                        {
                                            methodSymbol = semanticModel.GetDeclaredSymbol(mdeclar);
                                        }
                                        catch
                                        {
                                            methodSymbol = semanticModelBackup.GetDeclaredSymbol(mdeclar);
                                        }
                                        if (symbol.ToString() == methodSymbol.ToString())
                                        {
                                            return new Tuple<SyntaxNode, SyntaxTree>(
                                                mdeclar, searchtree);
                                        }
                                    }
                                    return null;
                                });

                            try
                            {
                                var methodDeclaration = methodDeclarTupleList.First(mdeclar => mdeclar != null);
                                codeSnippetQueue.Enqueue(methodDeclaration);
                            }
                            catch { }
                        }
                    }
                    catch (Exception e)
                    {
                        methodName = IOFile.TokenizeMethodName(invocation.ToString());
                        MergeDic<String>(ref allInovkedMethods,
                                new Dictionary<String, int>() { { methodName, 1 } });
                        Logger.Log(tree.FilePath);
                        Logger.Log(snippet.ToFullString());
                        Logger.Log(invocation.ToFullString());
                        Logger.Log(e);
                        Logger.Log(e.StackTrace);
                    } 
                }
            }

            return allInovkedMethods;
        }

        public static List<InvocationExpressionSyntax> GetInvokedMethodsInACodeSnippet(SyntaxNode codeSnippet)
        {
            List<InvocationExpressionSyntax> methodList;

            bool hasTryStatement = codeSnippet.DescendantNodes()
                    .OfType<TryStatementSyntax>().Any();

            if (hasTryStatement == true)
            {
                TryStatementSkipper tryblockskipper = new TryStatementSkipper();
                tryblockskipper.Visit(codeSnippet);
                methodList = tryblockskipper.invokedMethods;
                
            }
            else // has no try statement inside
            {
                methodList = codeSnippet.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            }

            var updatedMethodList = methodList.Where(method => !IsLoggingStatement(method)).ToList();
            return updatedMethodList;
        }

        public static Dictionary<String, int> GetVariablesAndComments(SyntaxNode codeSnippet)
        {
            Dictionary<String, int> variableAndComments = new Dictionary<String, int>();
            return variableAndComments;
        }

        public static void MergeDic<T>(ref Dictionary<T, int> dic1, Dictionary<T, int> dic2)
        {
            foreach (var key in dic2.Keys)
            {
                if (dic1.ContainsKey(key))
                {
                    dic1[key] += dic2[key];
                }
                else
                {
                    dic1.Add(key, dic2[key]);
                }
            }
        }

        public static bool IsThrow(SyntaxNode statement)
        {
            if (statement is ThrowStatementSyntax) return true;
            try
            {
                var invocation = statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().First();
                Regex regex = new Regex(@"(?i)Throw.*Exception");
                if (regex.Match(invocation.ToString()).Success)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        static public ThrowStatementSyntax FindThrowIn(SyntaxNode codeSnippet)
        {
            ThrowStatementSyntax throwStatement;
            try
            {
                bool hasTryStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().Any();

                if (hasTryStatement == true)
                {
                    // remove try-catch-finally block inside
                    codeSnippet = tryblockremover.Visit(codeSnippet);
                }
                throwStatement = codeSnippet.DescendantNodes().OfType<ThrowStatementSyntax>().First();
                return throwStatement;
            }
            catch
            {
                return null;
            }
        }

        static public bool IsSetLogicFlagStatement(SyntaxNode statement)
        {
            try 
            {
                var expression = statement.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().First();

                if (expression.Kind == SyntaxKind.AssignExpression)
                {
                    var node = expression.Right;
                    if (node.Kind == SyntaxKind.MemberAccessExpression 
                        || node.Kind == SyntaxKind.FalseLiteralExpression
                        || node.Kind == SyntaxKind.TrueLiteralExpression 
                        || node.Kind == SyntaxKind.NullLiteralExpression)
                    {
                        //Console.WriteLine(node.ToString());
                        return true;
                    }
                }                  
                return false;
            }
            catch
            {
                return false;
            }
        }

        static public BinaryExpressionSyntax FindSetLogicFlagIn(SyntaxNode codeSnippet)
        {
            BinaryExpressionSyntax setLogicFlagStatement;
            try
            {
                bool hasTryStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().Any();

                if (hasTryStatement == true)
                {
                    // remove try-catch-finally block inside
                    codeSnippet = tryblockremover.Visit(codeSnippet);
                }
                setLogicFlagStatement = codeSnippet.DescendantNodes().OfType<BinaryExpressionSyntax>()
                    .First(IsSetLogicFlagStatement);    
                return setLogicFlagStatement;
            }
            catch
            {
                return null;
            }
        }

        public static ReturnStatementSyntax FindReturnIn(SyntaxNode codeSnippet)
        {
            ReturnStatementSyntax returnStatement;
            try
            {
                bool hasTryStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().Any();

                if (hasTryStatement == true)
                {
                    // remove try-catch-finally block inside
                    codeSnippet = tryblockremover.Visit(codeSnippet);
                }
                returnStatement = codeSnippet.DescendantNodes().OfType<ReturnStatementSyntax>().First();
                return returnStatement;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsRecoverStatement(SyntaxNode statement, SemanticModel semanticModel)
        {
            if (!IsLoggingStatement(statement) && !IsSetLogicFlagStatement(statement) && !IsThrow(statement))
            {
                var recoverStatementSet = statement.DescendantNodes().OfType<IdentifierNameSyntax>();
                foreach (var recoverStatement in recoverStatementSet)
                {
                    try
                    {
                        var symbol = semanticModel.GetSymbolInfo(recoverStatement).Symbol as LocalSymbol;
                        String typeName = symbol.Type.ToString();
                        if (typeName.Contains("Exception"))
                        {
                            // To check
                            return true;
                        }
                    }
                    catch { }
                }
            }
            return false;
        }

        public static StatementSyntax FindRecoverStatement(SyntaxNode codeSnippet, SemanticModel semanticModel)
        {
            StatementSyntax recoverStatement;
            try
            {
                try
                {
                    recoverStatement = codeSnippet.DescendantNodesAndSelf()
                            .OfType<TryStatementSyntax>().First();
                    return recoverStatement;
                }
                catch
                {
                    // has no try statement inside
                }

                recoverStatement = codeSnippet.DescendantNodes().OfType<StatementSyntax>()
                    .First(statement => IsRecoverStatement(statement, semanticModel)
                    && statement.Kind != SyntaxKind.Block);
                return recoverStatement;
            }
            catch
            {
                return null;
            }
        }

        static public StatementSyntax HasOtherOperation(SyntaxNode codeSnippet, SemanticModel semanticModel)
        {

            bool hasTryStatement = codeSnippet.DescendantNodesAndSelf()
                .OfType<TryStatementSyntax>().Any();

            if (hasTryStatement == true)
            {
                // remove try-catch-finally block inside
                codeSnippet = tryblockremover.Visit(codeSnippet);
            }

            var statementNodes = codeSnippet.DescendantNodes().OfType<StatementSyntax>();
            foreach (var statement in statementNodes)
            {
                if (!statement.DescendantNodes().OfType<StatementSyntax>().Any() 
                    && statement.Kind != SyntaxKind.Block) //get each leaf statement node
                {
                    //Console.WriteLine(statement.ToString());
                    if (!IsLoggingStatement(statement) && !(IsRecoverStatement(statement, semanticModel))
                        && !(statement is ReturnStatementSyntax) && !IsSetLogicFlagStatement(statement)
                        && !IsThrow(statement))
                    {
                        return statement;
                    }
                }
            }
            return null;
        }

        public static bool IsEmptyBlock(SyntaxNode codeSnippet)
        {
            if (codeSnippet is CatchClauseSyntax)
            {
                codeSnippet = (codeSnippet as CatchClauseSyntax).Block;
            }
            bool isEmpty = !codeSnippet.DescendantNodes().OfType<SyntaxNode>().Any();
            return isEmpty;
        }









 






        static public List<String> FindVariablesIn(SyntaxNode PieceOfCode, SemanticModel semanticModel, 
            out List<Symbol> Symbols)
        {
            var AllPossibleVariables = PieceOfCode.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>();
            var VariablesList = new List<string>(); // To Return
            Symbols = new List<Symbol>();
            foreach (IdentifierNameSyntax variable in AllPossibleVariables)
            {
                try
                {
                    if (!variable.IsInTypeOnlyContext())
                    {
                        ExpressionSyntax RealVariable = variable as ExpressionSyntax;
                        if (variable.Parent is MemberAccessExpressionSyntax)
                        {
                            RealVariable = variable.Parent as ExpressionSyntax;
                            // Remove Duplicate  AAAA.BBBB will enter here twice, for AAAA and BBBB seperately
                            if (variable == (RealVariable as MemberAccessExpressionSyntax).Expression)
                            {
                                continue;
                            }
                        }
                        VariablesList.Add(RealVariable.ToString());
                        var symbol = semanticModel.GetSymbolInfo(RealVariable).Symbol;
                        if (symbol != null)
                            Symbols.Add(symbol);
                    }
                }
                catch { }
            }
            return VariablesList;
        }


    }

}
