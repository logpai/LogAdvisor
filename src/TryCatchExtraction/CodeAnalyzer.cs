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
        public static void AnalyzeAllTrees(List<SyntaxTree> treeList, Compilation compilation)
        {
            // statistics
            int numFiles = treeList.Count;
            var treeNode = treeList.Select(tree => tree.GetRoot().DescendantNodes().Count());
            Logger.Log("Num of syntax nodes: " + treeNode.Sum());
            Logger.Log("Num of source files: " + numFiles);
            // analyze every tree simultaneously
            var codeStatsList = treeList.AsParallel()
                .Select(tree => CodeAnalyzer.AnalyzeATree(tree, compilation)).ToList();

            CodeStatistics allStats = new CodeStatistics(codeStatsList);

            // Log statistics
            allStats.PrintSatistics();

            // Save all the source code into a txt file
            var sb = new StringBuilder(treeList.First().Length * numFiles); //initial length
            foreach (var stat in codeStatsList)
            {
                sb.Append(stat.Item1.GetText());
            }
            String txtFilePath = IOFileProcessing.CompleteFileName("AllSource.txt");
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
            Compilation compilation)
        {
            TreeStatistics stats = new TreeStatistics();
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);

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
            List<CatchBlock> catchStatsList = catchList.AsParallel()
                .Select(catchblock => AnalyzeACatchBlock(catchblock, model, compilation)).ToList();
            stats.CatchList = catchStatsList;

            return new Tuple<SyntaxTree, TreeStatistics>(tree, stats);
        }

        public static CatchBlock AnalyzeACatchBlock(CatchClauseSyntax catchblock, SemanticModel model,
            Compilation compilation)
        {
            CatchBlock catchBlockInfo = new CatchBlock();
            catchBlockInfo.ExceptionType = GetExceptionType(catchblock, model);
            if (FindLoggingIn(catchblock) != null)
            {
                catchBlockInfo.BoolFeatures["Logged"] = 1;
            }

            var tryBlock = catchblock.Parent as TryStatementSyntax;
            catchBlockInfo.TextFeatures = GetMethodNames(tryBlock, model);

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
            if (statement.ToString().IndexOf('(') == -1)
                return false;

            String logging = statement.ToString().Split('(').First();

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
            try
            {
                bool hasTryStatement = true;
                try
                {
                    var tryStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<TryStatementSyntax>().First();
                }
                catch (InvalidOperationException)
                {
                    hasTryStatement = false;
                }
                if (hasTryStatement == true)
                {
                    // remove try-catch-finally block inside
                    var updatedNode = tryblockremover.Visit(codeSnippet);
                    return FindLoggingIn(updatedNode);               
                }
                else
                {
                    var loggingStatement = codeSnippet.DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>().First(IsLoggingStatement);
                    return loggingStatement;
                }
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
                return "Exception";
            }
        }

        public static Dictionary<String, int> GetMethodNames(SyntaxNode codeSnippet, SemanticModel model)
        {
            Dictionary<String, int> methodNames = new Dictionary<String, int>();
            List<InvocationExpressionSyntax> methodList;

            if (codeSnippet is TryStatementSyntax)
            {
                codeSnippet = (codeSnippet as TryStatementSyntax).Block;
            }
            
            bool hasTryStatement = true;
            try
            {
                var tryStatement = codeSnippet.DescendantNodes()
                    .OfType<TryStatementSyntax>().First();
            }
            catch (InvalidOperationException)
            {
                hasTryStatement = false;
            }
            if (hasTryStatement == true)
            {
                var childNodes = codeSnippet.ChildNodes().OfType<StatementSyntax>();

                var methodNodeList = childNodes
                    .Where(child => !(child is TryStatementSyntax))
                    .Select(child => child.DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>().ToList());

                methodList = methodNodeList.SelectMany(x => x).ToList();
            }
            else // has no try statement inside
            {
                methodList = codeSnippet.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            }
            
            foreach (var invocation in methodList)
            {
                //Console.WriteLine(invocation);
                if (IsLoggingStatement(invocation))
                    continue; // ignore the logging method
                try
                {
                    var symbolInfo = model.GetSymbolInfo(invocation);
                    var symbol = symbolInfo.Symbol;
                    String methodName = symbol.ToString();
                    MergeDic<String>(ref methodNames,
                                new Dictionary<String, int>() { { methodName, 1 } });
                }
                catch (Exception)
                {
                    // no symbol info
                    MergeDic<String>(ref methodNames,
                            new Dictionary<String, int>() { { invocation.ToString(), 1 } });
                } 

            }
            return methodNames;
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








        public static bool IsInCatch(SyntaxNode node)
        {
            try
            {
                var catchSatement = node.Ancestors().OfType<CatchClauseSyntax>().First();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// return null if not found
        /// </summary>
        public static CatchClauseSyntax FindWrappingCatch(SyntaxNode node)
        {
            try
            {
                var Catch = node.Ancestors().OfType<CatchClauseSyntax>().First();
                return Catch;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// return null if not found
        /// </summary>
        public static TypeToFind FindWrapping<TypeToFind>(SyntaxNode node)
            where TypeToFind : SyntaxNode
        {
            try
            {
                var NodeToFind = node.Ancestors().OfType<TypeToFind>().First();
                return NodeToFind;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Intend to work for IfStatement, WhileStatement, SwitchStatement
        /// </summary>
        static public TypeToFind FindDirectWrapping<TypeToFind>(SyntaxNode node) where TypeToFind : SyntaxNode
        {
            try
            {
                SyntaxNode WrappingIf = node.Parent;
                while ((WrappingIf.Kind == SyntaxKind.Block) || (WrappingIf.Kind == SyntaxKind.ElseClause) 
                    || WrappingIf.Kind == SyntaxKind.SwitchSection)
                {
                    WrappingIf = WrappingIf.Parent;
                }
                if (WrappingIf is TypeToFind)
                    return WrappingIf as TypeToFind;
                else
                    return null;
            }
            catch { return null; }
        }

        public static ReturnStatementSyntax FindReturnIn(SyntaxNode PieceOfCode)
        {
            try
            {
                var Return = PieceOfCode.DescendantNodes().OfType<ReturnStatementSyntax>().First();
                return Return;
            }
            catch
            {
                return null;
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

        static public bool IsAssertTagLogging(SyntaxNode statement)
        {
            if (IsLoggingStatement(statement))
            {
                var loggingInvocation = FindLoggingIn(statement);
                if (loggingInvocation.Expression.ToString().Contains("AssertTag"))
                    return true;
            }
            return false;
        }

        static public ThrowStatementSyntax FindThrowIn(SyntaxNode PieceOfCode)
        {
            try
            {
                var ThrowState = PieceOfCode.DescendantNodes().OfType<ThrowStatementSyntax>().First();
                return ThrowState;
            }
            catch
            {
                return null;
            }
        }

        static public StatementSyntax FindGeneralThrowIn(SyntaxNode PieceOfCode)
        {
            try
            {
                Regex regex = new Regex("(?i)Throw.*Exception");
                var invocation = PieceOfCode.DescendantNodes().OfType<InvocationExpressionSyntax>().First(
                    IsGeneralThrow);
                var statement = GetStatement(invocation);
                return statement;
            }
            catch
            { return null; }
        }

        static public bool IsSetLogicFlagStatement(StatementSyntax statement) 
        {
            if (!IsLoggingStatement(statement))
            {
                try
                {
                    if (statement.Kind == SyntaxKind.ExpressionStatement)
                    {
                        var expression = statement.DescendantNodes().OfType<BinaryExpressionSyntax>().First();
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
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        static public StatementSyntax FindSetLogicFlagIn(SyntaxNode PieceOfCode)
        {
            try
            {
                var setLogicFlagStatement = PieceOfCode.DescendantNodes().OfType<StatementSyntax>()
                    .First(IsSetLogicFlagStatement);
                return setLogicFlagStatement;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsReturnStatement(SyntaxNode PieceOfCode)
        {
            try
            {
                var returnStatement = PieceOfCode.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().First();
                if (returnStatement != null)
                    return true;
                else return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsRecoverStatement(StatementSyntax statement, SemanticModel semanticModel)
        {
            if (!IsLoggingStatement(statement) && !IsSetLogicFlagStatement(statement) && !IsGeneralThrow(statement))
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
                            return true;
                        }
                    }
                    catch { }
                }
            }
            return false;
        }

        static public StatementSyntax FindRecoverStatement(SyntaxNode PieceOfCode, SemanticModel semanticModel)
        {
            try
            {
                var recoverStatement = PieceOfCode.DescendantNodes().OfType<StatementSyntax>()
                    .First(statement => IsRecoverStatement(statement, semanticModel) 
                    && statement.Kind != SyntaxKind.Block);
                return recoverStatement;
            }
            catch
            {
                return null;
            }
        }

        static public bool HasCriticalOperation(SyntaxNode PieceOfCode, SemanticModel semanticModel)
        {
            var statementNodes = PieceOfCode.DescendantNodes().OfType<StatementSyntax>();
            foreach (var statement in statementNodes)
            {
                if (!statement.DescendantNodes().OfType<StatementSyntax>().Any() 
                    && statement.Kind != SyntaxKind.Block) //get each leaf statement node
                {
                    //Console.WriteLine(statement.ToString());
                    if (!IsLoggingStatement(statement) && !(IsRecoverStatement(statement, semanticModel)) 
                        && !(IsReturnStatement(statement)) && !IsSetLogicFlagStatement(statement) 
                        && !IsGeneralThrow(statement))
                        return true;                 
                }
            }
            return false;
        }

        public static bool IsGeneralThrow(InvocationExpressionSyntax invocation)
        {
            Regex regex = new Regex(@"(?i)Throw.*Exception");
            if (regex.Match(invocation.Expression.ToString()).Success) //!!!
                return true;
            return false;
        }

        public static bool IsGeneralThrow(StatementSyntax statement)
        {
            try
            {
                if (statement.Kind == SyntaxKind.ThrowStatement)
                    return true;
                var invocation = statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().First();
                return IsGeneralThrow(invocation);
            }
            catch (InvalidOperationException) { }
            catch (Exception e)
            {
                Logger.Log(e);
            }
            return false;
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

        public static SyntaxNode FindSyntaxNodeBySpan(SyntaxNode root, TextSpan TargetSpan)
        {
            SyntaxNode TargetRange = root;
            while (true) //(!((TargetRange.Kind == SyntaxKind.InvocationExpression) || (TargetRange.Kind == SyntaxKind.ObjectCreationExpression)))
            {
                try
                {
                    TargetRange = TargetRange.ChildNodes().First(m => m.FullSpan.Contains(TargetSpan));
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
            return TargetRange;
        }

        /// <summary>
        /// Ignored Get and Set Accessor of Type AccessorDeclarationSyntax
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public static BaseMethodDeclarationSyntax FindBaseMethod(SyntaxNode node)
        {
            BaseMethodDeclarationSyntax BaseMethod;
            try
            {
                BaseMethod = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().First();
            }
            catch (InvalidOperationException)
            {
                try
                {
                    var x = node.Ancestors().OfType<AccessorDeclarationSyntax>().First();
                    //The Statement is inside a Get/Set Accessor in a Property Definition instead of a Method Definition.
                    //Don't know how to find a reference to get even if I could look for AccessorDeclarationSyntax
                    //So just ignore it.
                    //Logger.Log("Get/Set Accessor. (In CallerFinder.cs)");
                    throw new PleaseIgnoreAndContinueException("No Method but a Get or Set Accessor");
                }
                catch (InvalidOperationException)
                {
                    try
                    {
                        var x = node.Ancestors().OfType<FieldDeclarationSyntax>().First();
                        // Like class A { Object B = new Object() }
                        throw new PleaseIgnoreAndContinueException("No Method but a field initialization.");
                    }
                    catch (InvalidOperationException)
                    {
                        Logger.Log("Can't find containing method. (In CallerFinder.cs)");
                        throw new Exception();
                    }
                }
            }

            return BaseMethod;
        }

        public static TypeSymbol GetExceptionType(ThrowStatementSyntax CurrentStatement, SemanticModel semanticModel)
        {
            TypeSymbol excepType = null;
            if (CurrentStatement.Expression != null)
            {
                excepType = semanticModel.GetTypeInfo(CurrentStatement.Expression).Type;
            }
            if (excepType == null)
            {
                try
                {
                    var CatchClause = CurrentStatement.Ancestors().OfType<CatchClauseSyntax>().First();
                    excepType = semanticModel.GetTypeInfo(CatchClause.Declaration.Type).Type;
                }
                catch
                {

                }
            }
            return excepType;
        }

        static public List<String> GetExceptionAPIs(SyntaxNode node, SemanticModel semanticModel)
        {
            List<String> exceptionAPIs = new List<string>();
            try
            {
                var ExceptionMemberList = node.DescendantNodes().OfType<IdentifierNameSyntax>().Where(
                    delegate(IdentifierNameSyntax m)
                    {
                        try
                        {
                            var symbol = semanticModel.GetSymbolInfo(m).Symbol as LocalSymbol;
                            return symbol.Type.Name.EndsWith("Exception");
                        }
                        catch { return false; }
                    });

                foreach (IdentifierNameSyntax ExceptionRef in ExceptionMemberList)
                {
                    if (ExceptionRef.Parent.Kind != SyntaxKind.ObjectCreationExpression)//??
                    {
                        exceptionAPIs.Add(ExceptionRef.Parent.ToString());
                    }
                }

                exceptionAPIs.Add("#");
            }
            catch (Exception e)
            {
                Logger.Log("GetExceptionAPIs(...)");
                Logger.Log(e.ToString());
            }

            return exceptionAPIs;
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
        public static String GetCallName(InvocationExpressionSyntax child, SemanticModel semanticModel)
        {
            try
            {
                var symbol = semanticModel.GetSymbolInfo(child).Symbol;
                String newCall = symbol.ToString();
                return newCall;
            }
            catch
            {
                return null;
            }
        }

        public static String GetNameSpace(ExpressionSyntax node, SemanticModel semanticModel)
        {
            if (semanticModel != null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(node);

                if (symbolInfo.Symbol == null)
                {
                    //Logger.Log("Error! Not able to recognize: {0}", node.WithLeadingTrivia().ToString());
                    return "";
                }
                string NameSpace = symbolInfo.Symbol.ContainingNamespace.ToDisplayString();
                return NameSpace;
            }
            return "";
        }

        #region Return Value Check Methods
        public static String GetReturnType(InvocationExpressionSyntax node, SemanticModel semanticModel)
        {
            String ReturnType = "Error";
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
                if (IsAssertTagLogging(possibleLoggingInvocation)) //AssertTag
                {
                    if (possibleLoggingInvocation.ArgumentList.Arguments[Config.AssertConditionIndex].Span.Contains(invocation.Span))
                        return GetStatement(possibleLoggingInvocation);
                }
                else if(IsAssertLogging(possibleLoggingInvocation)) //Debug.Assert
                {
                    if (possibleLoggingInvocation.ArgumentList.Arguments[0].Span.Contains(invocation.Span))
                        return GetStatement(possibleLoggingInvocation);
                }
            }
            catch (IndexOutOfRangeException e)
            {
                Logger.Log("AssertConditionIndex config  error caused exception: " + e.ToString());
            }
            catch (Exception)
            { }
            return null;
        }

        private static StatementSyntax GetDirectIfForReturnValueCheck(SyntaxNode call, SemanticModel semanticModel, String ReturnType, out bool Checked, out SyntaxNode BoolVar)
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
                    if (CodeAnalyzer.IsAssertLogging(temp.Parent.Parent))
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

        /// <summary>
        /// Main Entrance
        /// If Checked but not in a If Statement, return the smallest statement wrapping the call, or the call's parent if statement is not available.
        /// </summary>
        public static StatementSyntax GetIfStatementForReturnValueCheck(InvocationExpressionSyntax call, SemanticModel semanticModel, String ReturnType, out bool Checked)
        {
            try
            {
                SyntaxNode BoolVar;
                Checked = false;

                StatementSyntax ReturnVarCheckIf = GetDirectIfForReturnValueCheck(call, semanticModel, ReturnType, out Checked, out BoolVar);
                if ((ReturnVarCheckIf != null) && !(ReturnVarCheckIf is LocalDeclarationStatementSyntax)) return ReturnVarCheckIf;

                /////////////////////////////////// Find the Assigned Variable////////////////////////////////
                //Get Variable that's on the left side of "="
                //If it's assigned the value of AAA(), instead of BB(AAA()) or AAA().BB()
                //!!!!!!!!!!!!
                StatementSyntax statement = GetStatement(call);
                Symbol SymbolReturnVar = GetReturnVar(call, semanticModel, BoolVar, ref ReturnType);

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
                            (CheckScope, ifStatement, Condition, statement, semanticModel, SymbolReturnVar, ReturnType,
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
                            (CheckScope, AssertLogging, Condition, statement, semanticModel, SymbolReturnVar, ReturnType,
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
        #endregion

        private static void ProcessPossibleReturnValueCheckStatement
            (StatementSyntax CheckScope, StatementSyntax ifStatement, ExpressionSyntax Condition, StatementSyntax statement, SemanticModel semanticModel, Symbol SymbolReturnVar, String ReturnType, ref StatementSyntax ReturnVarCheckIf, ref bool Checked, ref SyntaxNode BoolVar, ref bool FirstCheckFound, ref bool ReturnVarNotChanged)
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

        public static StatementSyntax FindThrowFollowing(SyntaxNode node)
        {
            StatementSyntax statement = GetStatement(node);
            StatementSyntax followingThrow = null;
            try
            {
                //Find a throw in the same block with node
                //throw is located after node
                followingThrow = statement.Parent.ChildNodes().OfType<ThrowStatementSyntax>().Last(m => m.Span.Start > node.Span.End);
            }
            catch (Exception)
            {//ignore 
            }
            if (followingThrow == null)
            {
                try
                {
                    //A return is also good enough
                    followingThrow = statement.Parent.ChildNodes().OfType<ReturnStatementSyntax>().Last(m => m.Span.Start > node.Span.End);
                }
                catch (Exception)
                {//ignore
                }
            }
            return followingThrow;
        }

        /// <summary>
        /// Return null if not found
        /// </summary>
        /// <param name="throwNode"></param>
        /// <param name="ExcTypeToFind"></param>
        /// <param name="semanticModel"></param>
        /// <returns></returns>
        public static CatchClauseSyntax FindCatchForSpecificExceptionType(SyntaxNode throwNode, TypeSymbol ExcTypeToFind, SemanticModel semanticModel)
        {
            // Build Target Exception Type - All base types of the exception type in "throw"
            List<TypeSymbol> TargetExcTypeList = new List<TypeSymbol>();
            TypeSymbol tempType = ExcTypeToFind;
            while ((tempType != null) && (tempType.Kind != SymbolKind.ErrorType))
            {
                TargetExcTypeList.Add(tempType);
                tempType = tempType.BaseType;
            }
            try
            {
                var Try = throwNode.Ancestors().OfType<TryStatementSyntax>().First();
                while (!Try.Block.Span.Contains(throwNode.Span))
                {
                    Try = Try.Ancestors().OfType<TryStatementSyntax>().First();
                }
                var Catches = Try.Catches;
                foreach (CatchClauseSyntax Catch in Catches)
                {
                    try
                    {
                        Boolean Match = false;

                        /* try {...}
                         * catch
                         * {
                         * ...
                         * }
                         * */
                        if (Catch.Declaration == null)
                        {
                            Match = true;
                            return Catch;
                        }

                        /* catch (***Exception)
                         * {
                         * ...
                         * }
                         */
                        var CatchExcType = semanticModel.GetTypeInfo(Catch.Declaration.Type).Type;

                        if ((CatchExcType == TypeInfo.None.Type) || (TargetExcTypeList.Contains(CatchExcType)))
                        {
                            Match = true;
                            return Catch;
                        }
                        if (Match)
                        {
                            return Catch;
                        }
                    }
                    catch (NullReferenceException)
                    {
                        Console.Write("Can't understand Exception Type in Catch");
                        if (Catch.Declaration != null)
                        {
                            Console.Write(Catch.Declaration.Type);
                        }
                        Logger.Log("(In FindCatch();)");
                    }
                }
            }
            catch (InvalidOperationException) // Can't find Try
            {
                return null;
            }
            return null;
        }

        /// <summary>
        /// Throw an ArgumentException if not found.
        /// </summary>
        public static IDocument FindDocumentByPath(ISolution solution, String FilePath)
        {
            foreach (IProject project in solution.Projects)
            {
                foreach (IDocument document in project.Documents)
                {
                    if (document.FilePath == FilePath)
                        return document;
                }
            }
            throw new ArgumentException("Can't find document " + FilePath);
            //return null
        }

        static public String DeleteSpace(String s)
        {
            return s.Replace("\n", "").Replace("\r", "").Replace("\t", "")
                .Replace("    ", " ").Replace("    ", " ").Replace("   ", " ")
                .Replace("  ", " ");
        }
    }

    class PleaseIgnoreAndContinueException : ApplicationException
    {
        public PleaseIgnoreAndContinueException(String message)
            : base(message)
        {
        }
    }
    class NotInSourceCodeException : ApplicationException
    {
        public NotInSourceCodeException(String message)
            : base(message)
        {
        }
    }

    class DoesNotFitPatternException : ApplicationException
    { }
}
