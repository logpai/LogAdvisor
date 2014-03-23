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

namespace ContextFeatureExtraction
{
    static class CodeAnalyzer
    {
        public static TryStatementRemover tryblockremover = new TryStatementRemover();
        public static Dictionary<String, MethodDeclarationSyntax> AllMethodDeclarations =
            new Dictionary<String, MethodDeclarationSyntax>();

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

            // analyze every tree simultaneously
            var allMethodDeclarations = treeAndModelDic.Keys.AsParallel()
                .Select(tree => GetAllMethodDeclarations(tree, treeAndModelDic, compilation));
            foreach (var methoddeclar in allMethodDeclarations)
            {
                MergeDic<String, MethodDeclarationSyntax>(ref AllMethodDeclarations, methoddeclar);
            }
            Logger.Log("Cached all method declarations.");

            var codeStatsList = treeAndModelDic.Keys.AsParallel()
                .Select(tree => AnalyzeATree(tree, treeAndModelDic, compilation)).ToList();
            CodeStatistics allStats = new CodeStatistics(codeStatsList);
            // Log statistics
            Logger.Log("Num of syntax nodes: " + treeNode.Sum());
            Logger.Log("Num of source files: " + numFiles);
            allStats.PrintSatistics();

            // Save all the source code into a txt file
            bool saveAllSource = false;
            if (saveAllSource == true)
            {
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
                .Select(catchblock => AnalyzeACatchBlock(catchblock, treeAndModelDic,
                compilation)).ToList();

            return new Tuple<SyntaxTree, TreeStatistics>(tree, stats);
        }

        public static CatchBlock AnalyzeACatchBlock(CatchClauseSyntax catchblock,
                Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            CatchBlock catchBlockInfo = new CatchBlock();
            var tree = catchblock.SyntaxTree;
            var model = treeAndModelDic[tree];          
            var exceptionType = GetExceptionType(catchblock, model);
            catchBlockInfo.ExceptionType = IOFile.MethodNameExtraction(exceptionType);
            var tryBlock = catchblock.Parent as TryStatementSyntax;

            var fileLinePositionSpan = tree.GetLineSpan(tryBlock.Block.Span, false);
            var startLine = fileLinePositionSpan.StartLinePosition.Line + 1;
            var endLine = fileLinePositionSpan.EndLinePosition.Line + 1;
            catchBlockInfo.OperationFeatures["LOC"] = endLine - startLine;
            catchBlockInfo.MetaInfo["Line"] = startLine.ToString();
            catchBlockInfo.MetaInfo["FilePath"] = tree.FilePath;

            bool hasTryStatement = catchblock.DescendantNodesAndSelf()
                      .OfType<TryStatementSyntax>().Any();
            SyntaxNode updatedCatchBlock = catchblock;
            if (hasTryStatement == true)
            {
                // remove try-catch-finally block inside
                updatedCatchBlock = tryblockremover.Visit(catchblock);
            }

            catchBlockInfo.MetaInfo["CatchBlock"] = catchblock.ToString();

            var loggingStatement = FindLoggingIn(updatedCatchBlock);
            if (loggingStatement != null)
            {
                catchBlockInfo.MetaInfo["Logged"] = loggingStatement.ToString();
                catchBlockInfo.OperationFeatures["Logged"] = 1;
            }

            var throwStatement = FindThrowIn(updatedCatchBlock);
            if (throwStatement != null)
            {
                catchBlockInfo.MetaInfo["Thrown"] = throwStatement.ToString();
                catchBlockInfo.OperationFeatures["Thrown"] = 1;
            }

            var setLogicFlag = FindSetLogicFlagIn(updatedCatchBlock);
            if (setLogicFlag != null)
            {
                catchBlockInfo.MetaInfo["SetLogicFlag"] = setLogicFlag.ToString();
                catchBlockInfo.OperationFeatures["SetLogicFlag"] = 1;
            }

            var returnStatement = FindReturnIn(updatedCatchBlock);
            if (returnStatement != null)
            {
                catchBlockInfo.MetaInfo["Return"] = returnStatement.ToString();
                catchBlockInfo.OperationFeatures["Return"] = 1;
            }

            var recoverStatement = FindRecoverStatement(catchblock, model);
            if (recoverStatement != null)
            {
                catchBlockInfo.MetaInfo["RecoverFlag"] = recoverStatement.ToString();
                catchBlockInfo.OperationFeatures["RecoverFlag"] = 1;
            }

            var otherOperation = HasOtherOperation(updatedCatchBlock, model);
            if (otherOperation != null)
            {
                catchBlockInfo.MetaInfo["OtherOperation"] = otherOperation.ToString();
                catchBlockInfo.OperationFeatures["OtherOperation"] = 1;
            }

            if (IsEmptyBlock(updatedCatchBlock))
            {
                catchBlockInfo.OperationFeatures["EmptyBlock"] = 1;            
            }
            
            var variableAndComments = GetVariablesAndComments(tryBlock.Block);
            var containingMethod = GetContainingMethodName(tryBlock, model);
            var methodNameList = GetAllInvokedMethodNamesByBFS(tryBlock.Block, treeAndModelDic, compilation);
            catchBlockInfo.OperationFeatures["NumMethod"] = methodNameList.Count;
            catchBlockInfo.TextFeatures = methodNameList;
            if (containingMethod != null)
            {
                MergeDic<String>(ref catchBlockInfo.TextFeatures,
                    new Dictionary<String, int>() { { containingMethod, 1 } });
            }
            MergeDic<String>(ref catchBlockInfo.TextFeatures,
                    new Dictionary<String, int>() { { "##spliter##", 0 } }); // to seperate methods and variables
            MergeDic<String>(ref catchBlockInfo.TextFeatures, variableAndComments);
            
            return catchBlockInfo;
        }

        public static Dictionary<String, MethodDeclarationSyntax> GetAllMethodDeclarations(SyntaxTree tree,
            Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            var allMethodDeclarations = new Dictionary<String, MethodDeclarationSyntax>();

            var root = tree.GetRoot();
            var methodDeclarList = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            var model = treeAndModelDic[tree];
            var modelBackup = compilation.GetSemanticModel(tree);
            foreach (var method in methodDeclarList)
            {
                Symbol methodSymbol = null;
                try
                {
                    methodSymbol = model.GetDeclaredSymbol(method);
                }
                catch
                {
                    try
                    {
                        methodSymbol = modelBackup.GetDeclaredSymbol(method);
                    }
                    catch { }
                }
                if (methodSymbol != null)
                {
                    var methodDeclaration = methodSymbol.ToString();
                    if (methodDeclaration != null && !allMethodDeclarations.ContainsKey(methodDeclaration))
                    {
                        allMethodDeclarations.Add(methodDeclaration, method);
                    }
                }
            }
            return allMethodDeclarations;
        }

        /// <summary>
        /// To check whether an invocation is a logging statement
        /// </summary>
        static public bool IsLoggingStatement(SyntaxNode statement)
        {
            String logging = IOFile.MethodNameExtraction(statement.ToString());
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
                    return typeSymbol.ToString(); //e.g., "System.IO.IOException
                }
                else
                {
                    return type.ToString();
                }
            }
            catch
            {
                // the default exception type
                return "System.UndeclaredException.Type";
            }
        }

        public static Dictionary<String, int> GetAllInvokedMethodNamesByBFS(SyntaxNode inputSnippet, 
            Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            Dictionary<String, int> allInovkedMethods = new Dictionary<String, int>();
            // to save a code snippet and its backward level
            Queue<Tuple<SyntaxNode, int>> codeSnippetQueue = new Queue<Tuple<SyntaxNode, int>>();

            codeSnippetQueue.Enqueue(new Tuple<SyntaxNode, int>(inputSnippet, 0));
            
            while (codeSnippetQueue.Any())
            {
                Tuple<SyntaxNode, int> snippetAndLevel = codeSnippetQueue.Dequeue();
                var level = snippetAndLevel.Item2;
                var snippet = snippetAndLevel.Item1;
                var tree = snippet.SyntaxTree;
                List<InvocationExpressionSyntax> methodList = GetInvokedMethodsInACodeSnippet(snippet);
                
                foreach (var invocation in methodList)
                {
                    String methodName = IOFile.MethodNameExtraction(invocation.ToString());
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
                        if (symbol != null)
                        {
                            methodName = IOFile.MethodNameExtraction(symbol.ToString());
                        }
                        if (allInovkedMethods.ContainsKey(methodName))
                        {
                            allInovkedMethods[methodName]++;
                        }
                        else
                        {
                            allInovkedMethods.Add(methodName, 1);
                            if (level > 3) continue; // only go backward to 3 levels
                            if (methodName.StartsWith("System")) continue; // System API

                            if (symbol != null && AllMethodDeclarations.ContainsKey(symbol.ToString()))
                            {
                                // find the method declaration (go to definition)
                                var mdeclar = AllMethodDeclarations[symbol.ToString()];
                                codeSnippetQueue.Enqueue(new Tuple<SyntaxNode, int>(mdeclar, level + 1));
                            }
                        }
                    }
                    catch (Exception e)
                    {
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

            bool hasTryStatement = codeSnippet.DescendantNodes()
                .OfType<TryStatementSyntax>().Any();
            if (hasTryStatement == true)
            {
                codeSnippet = tryblockremover.Visit(codeSnippet);
            }

            var variableList = codeSnippet.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(variable => !variable.IsInTypeOnlyContext());
            foreach (var variable in variableList)
            {
                var variableName = IOFile.MethodNameExtraction(variable.ToString());
                MergeDic<String>(ref variableAndComments,
                    new Dictionary<String, int>() { { variableName, 1 } });
            }

            var commentList = codeSnippet.DescendantTrivia()
                .Where(childNode => childNode.Kind == SyntaxKind.SingleLineCommentTrivia 
                    || childNode.Kind == SyntaxKind.MultiLineCommentTrivia);
            foreach (var comment in commentList)
            {
                String updatedComment = IOFile.DeleteSpace(comment.ToString());
                updatedComment = Regex.Replace(updatedComment, "<.*>", "");
                updatedComment = Regex.Replace(updatedComment, "{.*}", "");
                updatedComment = Regex.Replace(updatedComment, "\\(.*\\)", "");
                MergeDic<String>(ref variableAndComments,
                    new Dictionary<String, int>() { { updatedComment, 1 } });
            }
            return variableAndComments;
        }

        public static String GetContainingMethodName(SyntaxNode codeSnippet, SemanticModel model)
        {
            // Method name
            SyntaxNode method = null;
            String methodName = null;
            try
            {
                method = codeSnippet.Ancestors().OfType<BaseMethodDeclarationSyntax>().First();
            }
            catch
            {
                // Skip method type: e.g., operator method
            }

            Symbol methodSymbol;
            if (method != null)
            {
                if (method is MethodDeclarationSyntax)
                {
                    var methodDeclaration = method as MethodDeclarationSyntax;
                    try
                    {
                        methodSymbol = model.GetDeclaredSymbol(methodDeclaration);
                        methodName = methodSymbol.ToString();
                    }
                    catch 
                    {
                        methodName = methodDeclaration.Identifier.ValueText;
                    }
                }
                else if (method is ConstructorDeclarationSyntax)
                {
                    var methodDeclaration = method as ConstructorDeclarationSyntax;
                    try
                    {
                        methodSymbol = model.GetDeclaredSymbol(methodDeclaration);
                        methodName = methodSymbol.ToString();
                    }
                    catch
                    {
                        methodName = methodDeclaration.Identifier.ValueText;
                    }
                }
            }
            return IOFile.MethodNameExtraction(methodName);
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

        public static void MergeDic<T1, T2>(ref Dictionary<T1, T2> dic1, Dictionary<T1, T2> dic2)
        {
            foreach (var key in dic2.Keys)
            {
                if (!dic1.ContainsKey(key))
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

    }
}
