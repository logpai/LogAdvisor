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
            stats.CatchBlockList = catchList
                .Select(catchblock => AnalyzeACatchBlock(catchblock, treeAndModelDic,
                compilation)).ToList();

            // Statistics and features of (checked) API calls
            if (callList.Count() > 0)
            {
                stats.APICallList = callList
                    .Select(apicall => AnalyzeAnAPICall(apicall, treeAndModelDic,
                    compilation)).ToList();
            }

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
            catchBlockInfo.OperationFeatures["LOC"] = endLine - startLine + 1;
            catchBlockInfo.MetaInfo["Line"] = startLine.ToString();
            catchBlockInfo.MetaInfo["FilePath"] = tree.FilePath;

            bool hasTryStatement = catchblock.DescendantNodesAndSelf()
                      .OfType<TryStatementSyntax>().Any();
            SyntaxNode updatedCatchBlock = catchblock;
            if (hasTryStatement == true)
            {
                try {
                    // remove try-catch-finally block inside
                    updatedCatchBlock = tryblockremover.Visit(catchblock);
                }
                catch (System.ArgumentNullException e)
                {
                    // ignore the ArgumentNullException 
                }
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

        public static APICall AnalyzeAnAPICall(InvocationExpressionSyntax call,
                Dictionary<SyntaxTree, SemanticModel> treeAndModelDic, Compilation compilation)
        {
            var tree = call.SyntaxTree;
            var model = treeAndModelDic[tree];
            var returnType = GetReturnValueType(call, model);
            var apiCallName = GetAPICallName(call, model);

            StatementSyntax checkIfBlock;
            try
            {
                checkIfBlock = GetIfStatementForReturnValueCheck(call, model, returnType);
            }
            catch (Exception)
            {
                checkIfBlock = null;
            }

            if (checkIfBlock == null) return null;
            APICall apiCallInfo = new APICall();

            String callType = IOFile.ShortMethodNameExtraction(apiCallName);
            if (callType == null)
            {
                callType = "Error";
            }
            apiCallInfo.CallType = callType;

            var fileLinePositionSpan = tree.GetLineSpan(checkIfBlock.Span, false);
            var startLine = fileLinePositionSpan.StartLinePosition.Line + 1;
            var endLine = fileLinePositionSpan.EndLinePosition.Line + 1;
            apiCallInfo.OperationFeatures["LOC"] = endLine - startLine + 1;
            apiCallInfo.MetaInfo["Line"] = startLine.ToString();
            apiCallInfo.MetaInfo["FilePath"] = tree.FilePath;

            bool hasTryStatement = checkIfBlock.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().Any();
            SyntaxNode updatedcheckIfBlock = checkIfBlock;           
            if (hasTryStatement == true)
            {
                try
                {
                    // remove try-catch-finally block inside
                    updatedcheckIfBlock = tryblockremover.Visit(checkIfBlock);
                }
                catch (System.ArgumentNullException e)
                {
                    // ignore the ArgumentNullException 
                }
            }
            
            apiCallInfo.MetaInfo["CheckIfBlock"] = checkIfBlock.ToString();

            var loggingStatement = FindLoggingIn(updatedcheckIfBlock);
            if (loggingStatement != null)
            {
                apiCallInfo.MetaInfo["Logged"] = loggingStatement.ToString();
                apiCallInfo.OperationFeatures["Logged"] = 1;
            }

            var throwStatement = FindThrowIn(updatedcheckIfBlock);
            if (throwStatement != null)
            {
                apiCallInfo.MetaInfo["Thrown"] = throwStatement.ToString();
                apiCallInfo.OperationFeatures["Thrown"] = 1;
            }

            var setLogicFlag = FindSetLogicFlagIn(updatedcheckIfBlock);
            if (setLogicFlag != null)
            {
                apiCallInfo.MetaInfo["SetLogicFlag"] = setLogicFlag.ToString();
                apiCallInfo.OperationFeatures["SetLogicFlag"] = 1;
            }

            var returnStatement = FindReturnIn(updatedcheckIfBlock);
            if (returnStatement != null)
            {
                apiCallInfo.MetaInfo["Return"] = returnStatement.ToString();
                apiCallInfo.OperationFeatures["Return"] = 1;
            }

            var recoverStatement = FindRecoverStatement(updatedcheckIfBlock, model);
            if (recoverStatement != null)
            {
                apiCallInfo.MetaInfo["RecoverFlag"] = recoverStatement.ToString();
                apiCallInfo.OperationFeatures["RecoverFlag"] = 1;
            }

            var otherOperation = HasOtherOperation(updatedcheckIfBlock, model);
            if (otherOperation != null)
            {
                apiCallInfo.MetaInfo["OtherOperation"] = otherOperation.ToString();
                apiCallInfo.OperationFeatures["OtherOperation"] = 1;
            }

            if (IsEmptyBlock(updatedcheckIfBlock))
            {
                apiCallInfo.OperationFeatures["EmptyBlock"] = 1;
            }

            var containingMethod = GetContainingMethodName(checkIfBlock, model);
            var methodNameList = GetAllInvokedMethodNamesByBFS(checkIfBlock, treeAndModelDic, compilation);
            apiCallInfo.OperationFeatures["NumMethod"] = methodNameList.Count;
            apiCallInfo.TextFeatures = methodNameList;
            if (containingMethod != null)
            {
                MergeDic<String>(ref apiCallInfo.TextFeatures,
                    new Dictionary<String, int>() { { containingMethod, 1 } });
            }
            if (IOFile.MethodNameExtraction(apiCallName) != null)
            {
                MergeDic<String>(ref apiCallInfo.TextFeatures,
                    new Dictionary<String, int>() { { IOFile.MethodNameExtraction(apiCallName), 1 } });
            }
            else if (IOFile.ShortMethodNameExtraction(apiCallName) != null)
            {
                MergeDic<String>(ref apiCallInfo.TextFeatures,
                    new Dictionary<String, int>() { { IOFile.ShortMethodNameExtraction(apiCallName), 1 } });
            }

            return apiCallInfo;
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
                if (key == null) continue;
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
                if (key == null) continue;
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

        public static String GetReturnValueType(InvocationExpressionSyntax node, SemanticModel semanticModel)
        {
            String ReturnType = null;
            if (node == null)
            {
                Logger.Log("node == null");
                return ReturnType;
            }
            if (semanticModel != null)
            {
                try
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(node);
                    if (symbolInfo.Symbol == null)
                    {
                        //Logger.Log("Don't Understand Call {0}", expression.ToString ());
                        return ReturnType;
                    }
                    var definition = symbolInfo.Symbol/*.OriginalDefinition*/ as MethodSymbol;
                    ReturnType = definition.ReturnType.ToString();
                }
                catch (InvalidCastException)
                {
                    Logger.Log("Unexpected Invocation Syntax Structure! " + node.Expression);
                }
                catch (InvalidOperationException)
                {
                    Logger.Log("Unexpected Invocation Syntax Structure! " + node.Expression);
                }
                catch (NullReferenceException)
                {
                    //Log?
                }

            }
            return ReturnType;
        }

        public static String GetAPICallName(InvocationExpressionSyntax child, SemanticModel semanticModel)
        {
            try
            {
                var symbol = semanticModel.GetSymbolInfo(child).Symbol;
                String newCall = symbol.ToString();
                return newCall;
            }
            catch
            {
                return child.ToString();
            }
        }

        /// <summary>
        /// Main Entrance
        /// If Checked but not in a If Statement, return the smallest statement wrapping the call, or the call's parent if statement is not available.
        /// </summary>
        public static StatementSyntax GetIfStatementForReturnValueCheck(InvocationExpressionSyntax call, 
            SemanticModel semanticModel, String returnType)
        {
            bool Checked;
            try
            {
                SyntaxNode BoolVar;
                Checked = false;

                StatementSyntax ReturnVarCheckIf = GetDirectIfForReturnValueCheck(call, semanticModel, returnType, out Checked, out BoolVar);
                if ((ReturnVarCheckIf != null) && !(ReturnVarCheckIf is LocalDeclarationStatementSyntax)) return ReturnVarCheckIf;

                /////////////////////////////////// Find the Assigned Variable////////////////////////////////
                //Get Variable that's on the left side of "="
                //If it's assigned the value of AAA(), instead of BB(AAA()) or AAA().BB()
                //!!!!!!!!!!!!
                StatementSyntax statement = GetStatement(call);
                Symbol SymbolReturnVar = GetReturnVar(call, semanticModel, BoolVar, ref returnType);

                if (null == SymbolReturnVar)
                {
                    //No Check Found
                    //return ReturnVarCheckIf;
                    return null;
                }

                ///////////////////////////// check for Assigned Variables//////////////////////////////////
                var CheckScope = statement.Parent as StatementSyntax;

                ///ChildNodes(). Checks only at the same level.
                bool FirstCheckFound = false;
                bool ReturnVarNotChanged = true;

                var IfStatementList = CheckScope.ChildNodes().OfType<IfStatementSyntax>();
                foreach (IfStatementSyntax ifStatement in IfStatementList)
                {
                    if (ifStatement.Span.Start > statement.Span.End)
                    {
                        var Condition = (ifStatement as IfStatementSyntax).Condition;
                        ProcessPossibleReturnValueCheckStatement
                            (CheckScope, ifStatement, Condition, statement, semanticModel, SymbolReturnVar, returnType,
                            ref ReturnVarCheckIf, ref Checked, ref BoolVar, ref FirstCheckFound, ref ReturnVarNotChanged);

                        if (FirstCheckFound)
                        {
                            if (ReturnVarNotChanged)
                                ReturnVarCheckIf = ifStatement;
                            break;
                        }
                    }
                }
                if (ReturnVarCheckIf != null) return ReturnVarCheckIf;

                var AssertStatementList = CheckScope.ChildNodes().OfType<ExpressionStatementSyntax>().Where(IsAssertLogging);
                foreach (var AssertLogging in AssertStatementList)
                {
                    if (AssertLogging.Span.Start > statement.Span.End)
                    {
                        /*
                         * Microsoft.SharePoint.Diagnostics.ULS.AssertTag(uint, Microsoft.SharePoint.Diagnostics.ULSCatBase, bool, string, params object[])
                         * Microsoft.SharePoint.Diagnostics.ULS.ShipAssertTag(uint, Microsoft.SharePoint.Diagnostics.ULSCatBase, bool, string, params object[])
                         */
                        var Condition = (AssertLogging.Expression as InvocationExpressionSyntax).ArgumentList.Arguments[Config.AssertConditionIndex /*2*/].Expression;
                        ProcessPossibleReturnValueCheckStatement
                            (CheckScope, AssertLogging, Condition, statement, semanticModel, SymbolReturnVar, returnType,
                            ref ReturnVarCheckIf, ref Checked, ref BoolVar, ref FirstCheckFound, ref ReturnVarNotChanged);

                        if (FirstCheckFound)
                        {
                            if (ReturnVarNotChanged)
                                ReturnVarCheckIf = AssertLogging;
                            break;
                        }
                    }
                }
                if (ReturnVarCheckIf != null) return ReturnVarCheckIf;


                //return (FirstCheckFound && ReturnVarNotChanged);
                return ReturnVarCheckIf;
            }
            catch (NullReferenceException)
            {
                Checked = false;
                return null;
            }

        }

        /// <summary>
        /// If Statement, While Statement, *Assert* Statement.
        /// </summary>
        private static StatementSyntax GetDirectIfForReturnValueCheck(SyntaxNode invocation)
        {
            try
            {
                IfStatementSyntax IfStatement = invocation.Ancestors().OfType<IfStatementSyntax>().First();
                if (IfStatement.Condition.Span.Contains(invocation.Span))
                    return IfStatement;
            }
            catch { }
            try
            {
                WhileStatementSyntax WhileStatement = invocation.Ancestors().OfType<WhileStatementSyntax>().First();
                if (WhileStatement.Condition.Span.Contains(invocation.Span))
                    return WhileStatement;

            }
            catch
            { }
            try
            {
                /*
                 * Microsoft.SharePoint.Diagnostics.ULS.AssertTag(uint, Microsoft.SharePoint.Diagnostics.ULSCatBase, bool, string, params object[])
                 * Microsoft.SharePoint.Diagnostics.ULS.ShipAssertTag(uint, Microsoft.SharePoint.Diagnostics.ULSCatBase, bool, string, params object[])
                 * Debug.Assert((bool)Type.InvokeMethod(type, "inheritsFrom", obj.ObjectData.AssociatedObject.GetType()));
                 */
                var possibleLoggingInvocation = invocation.Ancestors().OfType<InvocationExpressionSyntax>().Last(); //Last one. The biggest one just inside the statement.
                if (IsAssertLogging(possibleLoggingInvocation)) //AssertTag
                {
                    if (possibleLoggingInvocation.ArgumentList.Arguments[Config.AssertConditionIndex].Span.Contains(invocation.Span))
                        return GetStatement(possibleLoggingInvocation);
                }
                else if (IsAssertLogging(possibleLoggingInvocation)) //Debug.Assert
                {
                    if (possibleLoggingInvocation.ArgumentList.Arguments[0].Span.Contains(invocation.Span))
                        return GetStatement(possibleLoggingInvocation);
                }
            }
            catch (IndexOutOfRangeException e)
            {
                Logger.Log("AssertConditionIndex config  error caused exception: " + e.ToString());
            }
            catch (Exception e)
            { }
            return null;
        }

        private static StatementSyntax GetDirectIfForReturnValueCheck(SyntaxNode call, SemanticModel semanticModel, 
            String ReturnType, out bool Checked, out SyntaxNode BoolVar)
        {
            StatementSyntax ReturnVarCheckStatement = null;
            BoolVar = null;
            Checked = false;
            // check if the Expression is in a check
            if (ReturnType != "bool")
            {
                SyntaxNode temp = call.Parent;
                while ((temp.Kind == SyntaxKind.AssignExpression) || temp.Kind == SyntaxKind.ParenthesizedExpression
                    ///!!!!C.B counts and C.B() does not
                    || (temp.Kind == SyntaxKind.MemberAccessExpression)
                    ///!!!C.B() is InvocationExperssion(C.B())->MemberAccessExpression(C.B)->Identifier(C)
                    ///So it cannot go through this filter.
                    )
                {
                    temp = temp.Parent;
                }
                if ((temp.Kind == SyntaxKind.NotEqualsExpression) // "AAA() != Empty" 
                    || (temp.Kind == SyntaxKind.EqualsExpression)) // "AAA() == null" 
                //|| (temp.Kind == SyntaxKind.IfStatement) //"if (Regex.Match(...).Success)"
                //|| (temp.Kind == SyntaxKind.log))
                {
                    ///It's checked. No matter whether an If is found.
                    Checked = true;
                    BoolVar = temp;
                    temp = temp.Parent;//Fixed Bug
                }
                if (temp.Kind == SyntaxKind.Argument)
                {
                    if (IsAssertLogging(temp.Parent.Parent))
                    {
                        Checked = true;
                        BoolVar = temp;
                        return GetStatement(temp);
                    }
                }

                ReturnVarCheckStatement = GetDirectIfForReturnValueCheck(call);
                try
                {
                    ///Mark BB(AAA()) and AAA().BB() as not checked
                    var anotherInvocation = call.Ancestors().OfType<InvocationExpressionSyntax>().First();
                    //If something constains anotherInvocation.Span?
                    Checked = false;
                    return null;
                }
                catch
                {
                    //Expected
                }
                if (null != ReturnVarCheckStatement)
                {
                    Checked = true;
                    return ReturnVarCheckStatement;
                }
                else
                {
                    if (Checked)
                    {
                        ReturnVarCheckStatement = GetStatement(call);
                        return ReturnVarCheckStatement;
                    }
                }

            }
            else // bool return value
            {
                try
                {
                    BoolVar = call;
                    ///Look for "If" or "While"
                    ReturnVarCheckStatement = GetDirectIfForReturnValueCheck(call);
                    if (null != ReturnVarCheckStatement)
                    {
                        Checked = true;
                        return ReturnVarCheckStatement;
                    }
                    ///Look for "**?**:**"
                    try
                    {
                        var ConditionalExpression = call.Ancestors().OfType<ConditionalExpressionSyntax>().First();
                        if (ConditionalExpression.Condition.Span.Contains(call.Span))
                        {
                            Checked = true;
                            ReturnVarCheckStatement = GetStatement(ConditionalExpression);
                            return ReturnVarCheckStatement;
                        }

                    }
                    catch { }
                }
                catch (Exception e)
                {
                    Logger.Log(e);
                    Logger.Log("Unknown Problem.");
                }
            }
            Checked = false;
            return null;
        }

        private static void ProcessPossibleReturnValueCheckStatement(StatementSyntax CheckScope, StatementSyntax ifStatement, 
            ExpressionSyntax Condition, StatementSyntax statement, SemanticModel semanticModel, Symbol SymbolReturnVar, 
            String ReturnType, ref StatementSyntax ReturnVarCheckIf, ref bool Checked, ref SyntaxNode BoolVar, 
            ref bool FirstCheckFound, ref bool ReturnVarNotChanged)
        {
            var identifierList = Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>();
            foreach (IdentifierNameSyntax identifier in identifierList)
            {
                var SymbolInfo = semanticModel.GetSymbolInfo(identifier);
                if (SymbolInfo.Symbol == null)
                {
                    continue;
                }
                var SymbolCheckVar = SymbolInfo.Symbol;
                if (SymbolReturnVar == SymbolCheckVar)
                {
                    ReturnVarCheckIf = GetDirectIfForReturnValueCheck(identifier, semanticModel, ReturnType, out Checked, out BoolVar);
                    if (!Checked)
                    {
                        continue;
                    }
                    FirstCheckFound = true;

                    //check if the return variable have been changed between invocation and first check. 
                    ReturnVarNotChanged = true;
                    var CheckIfChangedList = CheckScope.DescendantNodes().OfType<StatementSyntax>();
                    var Span1 = statement.Span;
                    var Span3 = ifStatement.Span;
                    foreach (StatementSyntax checkIfChangedStatement in CheckIfChangedList)
                    {
                        var Span2 = checkIfChangedStatement.Span;
                        if ((Span1.End < Span2.Start) && (Span2.End < Span3.Start))
                        {
                            DataFlowAnalysis dataflow = semanticModel.AnalyzeDataFlow(checkIfChangedStatement);
                            if (dataflow.WrittenInside.Contains(SymbolReturnVar))
                            {
                                ReturnVarNotChanged = false;
                                break;
                            }
                        }
                        if (Span2.End > Span3.Start) break;
                    }
                    break;
                }
            }
        }

        static public bool IsAssertLogging(SyntaxNode statement)
        {
            if (IsLoggingStatement(statement))
            {
                var loggingInvocation = FindLoggingIn(statement);
                if (loggingInvocation.Expression.ToString().Contains("Assert"))
                    return true;
            }
            return false;
        }

        public static StatementSyntax GetStatement(SyntaxNode node)
        {
            try
            {
                return node.Ancestors().OfType<StatementSyntax>().First();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// !!!!!!!!!! ToDo
        /// If it's assigned the value of AAA(), instead of BB(AAA()) or AAA().BB()
        /// </summary>
        private static Symbol GetReturnVar(InvocationExpressionSyntax call, SemanticModel semanticModel, SyntaxNode BoolVar, ref String ReturnType)
        {
            if (BoolVar == null)
            {
                BoolVar = call;
            }
            else
            {
                ReturnType = "bool";
            }
            var statement = GetStatement(BoolVar);
            if (statement == null) return null;

            /// C = AA().BB() or C = BBB(AA())  Does not count C as Return Variable for AA()
            /// C = AA().BB count
            try
            {
                var AnotherCall = call.Ancestors().OfType<InvocationExpressionSyntax>().First();
                var Statement = GetStatement(BoolVar);
                if (Statement.Span.Contains(AnotherCall.Span))
                {
                    /// C = AA().BB() or C = BBB(AA()) Does not count.
                    return null;
                }
            }
            catch (InvalidOperationException)
            {
                ///Expected condition
            }
            //if ((call.Parent.Kind != SyntaxKind.AssignExpression) 
            //    && (call.Parent.Kind != SyntaxKind.EqualsValueClause))
            //{
            //    if ((BoolVar != null) &&
            //        ((BoolVar.Parent.Kind == SyntaxKind.LogicalAndExpression) ||
            //         (BoolVar.Parent.Kind == SyntaxKind.LogicalNotExpression) ||
            //         (BoolVar.Parent.Kind == SyntaxKind.LogicalOrExpression)))
            //    {
            //        //"bool bNoUrl = bDisabled || String.IsNullOrEmpty(url);"
            //    }
            //    else
            //        return null;
            //}

            Symbol SymbolReturnVar = null;
            if (statement.Kind == SyntaxKind.LocalDeclarationStatement)
            {
                var DeclarationNode = statement as LocalDeclarationStatementSyntax;
                var Declaration = DeclarationNode.Declaration;
                foreach (VariableDeclaratorSyntax variable in Declaration.Variables)
                {
                    if (variable.Span.Contains(call.Span))
                    {
                        SymbolReturnVar = semanticModel.GetDeclaredSymbol(variable);
                        if (SymbolReturnVar == null)
                        {
                            throw new NullReferenceException();
                        }
                        break;
                    }
                }

            }
            else
            {
                if (statement.Kind == SyntaxKind.ExpressionStatement)
                {
                    var expressionNode = statement as ExpressionStatementSyntax;
                    var expression = expressionNode.Expression;
                    if ((expression.Kind == SyntaxKind.AssignExpression) //AddAssign? No.
                         && (semanticModel != null))
                    {
                        BinaryExpressionSyntax binary = expression as BinaryExpressionSyntax;
                        var ReturnVar = binary.Left;
                        String ReturnVarName = binary.Left.ToString();

                        SymbolReturnVar = semanticModel.GetSymbolInfo(ReturnVar).Symbol;
                        if (SymbolReturnVar == null) throw new NullReferenceException();
                    }
                    else
                    {

                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }
            return SymbolReturnVar;
        }

    }
}
