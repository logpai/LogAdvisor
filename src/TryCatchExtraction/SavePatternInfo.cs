using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Roslyn.Compilers;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;

namespace CatchBlockExtraction
{
    /// <summary>
    /// Get the key information of each pattern
    /// </summary>
    class PatternKeyInformation : BasePatternInformation
    {
        String Text = "";
        static public PatternKeyInformation Empty = new PatternKeyInformation("", "");

        PatternKeyInformation(String location, String text)
        {
            m_ID = location;
            Text = IOFileProcessing.DeleteSpace(text);
        }

        public PatternKeyInformation(SyntaxNode node)
        {
            if (node != null)
            {
                int line = node.SyntaxTree.GetLineSpan(node.Span, false).StartLinePosition.Line + 1; //0-base to 1-base
                m_ID = node.SyntaxTree.FilePath + ":" + line.ToString();
                Text = IOFileProcessing.DeleteSpace(node.ToString());
            }
        }

        public PatternKeyInformation(StatementSyntax node, String ID)
        {
            if (node != null)
            {
                m_ID = "[" + ID + "]";
                Text = IOFileProcessing.DeleteSpace(node.ToString());
            }
            else
                m_ID = "[No" + ID +"]";
        }

        public PatternKeyInformation(InvocationExpressionSyntax logging)
        {
            String tag;

            if (logging == null)
            {
                m_ID = "";
                Text = "";
            }
            else
            {
                // Set m_ID
                try
                {
                    Regex rx = new Regex(@"(?<= tag_)\S*"); // tag_g4ib 
                    tag = rx.Match(logging.ToFullString()).Value;
                }
                catch
                {
                    tag = logging.Expression.ToString();
                }
                m_ID = tag;

                // Set Text /* = Level */
                try
                {
                    String level = logging.ArgumentList.Arguments[Config.LogLevelArgPos].ToString();
                    Text = IOFileProcessing.DeleteSpace(level);
                }
                catch
                {
                    Text = "";
                }
            }
        }

        public PatternKeyInformation(ThrowStatementSyntax throwStatement, SemanticModel semanticModel)
        {
            // Use the _ExceptionType as m_ID here
            m_ID = "";
            Text = "";
            if ((throwStatement == null) || (semanticModel == null))
            {
                return;
            }
            var ExceptionType = CodeAnalyzer.GetExceptionType(throwStatement, semanticModel);
            if (ExceptionType != null)
                m_ID = ExceptionType.ToString();

            Text = IOFileProcessing.DeleteSpace(throwStatement.ToString());
        }

        public PatternKeyInformation(IfStatementSyntax ifStatement, Boolean InElse)
        {
            m_ID = "";
            Text = "";
            if (ifStatement == null)
            {
                return;
            }
            m_ID = GetCheckType(ifStatement, InElse).ToString();
            Text = IOFileProcessing.DeleteSpace(ifStatement.Condition.ToString());
        }

        CheckType GetCheckType(IfStatementSyntax wrappingIf, Boolean InElse)
        {
            if (wrappingIf == null) return CheckType.N_A;
            var Condition = wrappingIf.Condition;
            try
            {
                var Logic = Condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>()
                    .First(m => (m.Kind == SyntaxKind.LogicalAndExpression) || (m.Kind == SyntaxKind.LogicalOrExpression));
                return CheckType.ComplexLogic;
            }
            catch
            {
            }

            // Simple _Check
            Boolean LogicNot = InElse;
            if (Condition.Kind == SyntaxKind.LogicalNotExpression)
            {
                LogicNot = !LogicNot;
                Condition = (Condition as PrefixUnaryExpressionSyntax).Operand;
            }

            while (Condition.Kind == SyntaxKind.ParenthesizedExpression)
            {
                Condition = (Condition as ParenthesizedExpressionSyntax).Expression;
            }

            //Logger.Log(Condition.GetType().Name);
            if (Condition.GetType().Name == "BinaryExpressionSyntax")
            {
                var LogicCheck = Condition as BinaryExpressionSyntax;
                if ((LogicCheck.Kind != SyntaxKind.EqualsExpression) && (LogicCheck.Kind != SyntaxKind.NotEqualsExpression))
                {
                    return CheckType.IrregularExpression;
                }
                String Left = LogicCheck.Left.WithLeadingTrivia().WithLeadingTrivia().ToString();
                String Right = LogicCheck.Right.WithLeadingTrivia().WithLeadingTrivia().ToString();
                if (Left.StartsWith("null") || Right.StartsWith("null"))
                {
                    if (LogicCheck.Kind == SyntaxKind.NotEqualsExpression)
                        LogicNot = !LogicNot;
                    if (!LogicNot)
                        return CheckType.NullCheck;
                    else
                        return CheckType.NotNullCheck;
                }
                if (Left.StartsWith("false") || Right.StartsWith("false"))
                {
                    if (LogicCheck.Kind == SyntaxKind.NotEqualsExpression)
                        LogicNot = !LogicNot;
                    if (!LogicNot)
                        return CheckType.FalseCheck;
                    else
                        return CheckType.TrueCheck;
                }
                if (Left.StartsWith("true") || Right.StartsWith("true"))
                {
                    if (LogicCheck.Kind == SyntaxKind.NotEqualsExpression)
                        LogicNot = !LogicNot;
                    if (!LogicNot)
                        return CheckType.TrueCheck;
                    else
                        return CheckType.FalseCheck;
                }
                if (Left.StartsWith("0") || Right.StartsWith("0"))
                {
                    return CheckType.ZeroCheck;
                }
                if (Left.StartsWith("-1") || Right.StartsWith("-1"))
                {
                    return CheckType.MinusOneCheck;
                }
                if (Left.StartsWith("1") || Right.StartsWith("1"))
                {
                    return CheckType.OneCheck;
                }
                return CheckType.IrregularExpression;
            }
            else
            {
                if (!LogicNot)
                {
                    return CheckType.TrueCheck;
                }
                else
                    return CheckType.FalseCheck;
            }
        }

        public override string PrintStatement()
        {
            return m_ID + split + Text;
        }


        public Boolean IsEmpty()
        {
            if (m_ID != "") return false;
            if (Text != "") return false;
            return true;
        }
    }

    enum CheckType
    {
        N_A,
        NullCheck,
        NotNullCheck,
        FalseCheck,
        TrueCheck,
        MinusOneCheck,
        ZeroCheck,
        OneCheck,
        ComplexLogic, // (A == null) && (B == -1)
        IrregularExpression, //(******* == @@@@@@)
    }

    enum ThrowType
    {
        N_A,
        ReThrow,
        ThrowNew,
        ReturnFalse,
        ReturnNothing
    }

    class ContextAPI : BasePatternInformation
    {
        private List<String> _APIs;

        /// <param name="CatchOrCall">CatchClauseSyntax or InvocationExpressionSyntax</param>
        /// <param name="semanticModel"></param>
        public void SetNecessaryMembers(SyntaxNode CatchOrCall, SemanticModel semanticModel)
        {
            _APIs = new List<String>();

            try
            {
                // keep method name and class members
                bool OnlyAPIinTry = true;
                if (OnlyAPIinTry && (CatchOrCall is CatchClauseSyntax))
                {
                    var TryBlock = CatchOrCall.Parent as TryStatementSyntax;

                    var APIList = TryBlock.Block.DescendantNodes().OfType<InvocationExpressionSyntax>();
                    Add_APIsFrom(APIList, semanticModel);
                    var memberList = TryBlock.Block.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
                    AddMembersFrom(memberList, semanticModel); // add class memeber
                    AddMembersFrom(CatchOrCall);
                    AddMembersFrom(TryBlock.Block);
                    return;
                }

                if ((CatchOrCall is InvocationExpressionSyntax) || (CatchOrCall is CatchClauseSyntax))
                {
                    SyntaxNode WholeMethod;
                    try
                    {
                        WholeMethod = CatchOrCall.Ancestors().OfType<BaseMethodDeclarationSyntax>().First();
                    }
                    catch
                    {
                        try
                        {
                            WholeMethod = CatchOrCall.Ancestors().OfType<PropertyDeclarationSyntax>().First();
                        }
                        catch
                        {
                            WholeMethod = CatchOrCall.Ancestors().OfType<IndexerDeclarationSyntax>().First();
                        }
                    }
                    var APIList = WholeMethod.DescendantNodes().OfType<InvocationExpressionSyntax>();
                    Add_APIsFrom(APIList, semanticModel);
                    var memberList = WholeMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
                    AddMembersFrom(memberList, semanticModel); // add class member
                    AddMembersFrom(WholeMethod);
                }
            }
            catch (InvalidOperationException) { }
            catch (Exception e)
            {
                Logger.Log(e);
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }
        }

        public void Add_APIsFrom(IEnumerable<InvocationExpressionSyntax> APIList, SemanticModel semanticModel)
        {
            foreach (var invocation in APIList)
            {
                try
                {
                    if (CodeAnalyzer.IsLoggingStatement(invocation))
                        continue; // ignore the logging method
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var symbol = symbolInfo.Symbol;
                    String newAPI = symbol.ToString();
                    if (!_APIs.Contains(newAPI))
                        _APIs.Add(newAPI);
                }
                catch { }
            }
        }

        public void AddMembersFrom(IEnumerable<MemberAccessExpressionSyntax> memberList, SemanticModel semanticModel)
        {
            foreach (var member in memberList)
            { 
                try
                {
                    if (!CodeAnalyzer.IsLoggingStatement(member.Parent))
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(member);
                        var symbol = symbolInfo.Symbol;
                        String newMember = symbol.ToString();
                        if (!_APIs.Contains(newMember))
                            _APIs.Add(newMember);
                    }
                }
                catch { }
            }
        }

        public void AddMembersFrom(SyntaxNode codeSnippet)
        {
            var memberList = codeSnippet.DescendantNodes().OfType<StatementSyntax>();
            foreach (var member in memberList)
            {
                try
                {
                    //get each leaf statement node
                    if (!member.DescendantNodes().OfType<StatementSyntax>().Any() && member.Kind != SyntaxKind.Block 
                        && !CodeAnalyzer.IsLoggingStatement(member))
                    {
                        String newMember = IOFileProcessing.DeleteSpace(member.ToString());
                        if (!_APIs.Contains(newMember))
                            _APIs.Add(newMember);
                    }
                }
                catch { }
            }

            var commentList = codeSnippet.DescendantTrivia()
                .Where(childNode => childNode.Kind == SyntaxKind.SingleLineCommentTrivia || childNode.Kind == SyntaxKind.MultiLineCommentTrivia);
            foreach (var comment in commentList)
            {
                try
                {
                    String newMember = IOFileProcessing.DeleteSpace(comment.ToString());
                    if (!_APIs.Contains(newMember))
                        _APIs.Add(newMember);
                }
                catch { }
            }


        }

        public override string PrintStatement()
        {
            String toString = base.PrintStatement();
            toString = toString + ToShortString();
            return toString;
        }

        public string ToShortString()
        {
            String toString = "";
            foreach (var invocation in _APIs)
            {
                toString = toString + invocation + split;
            }
            return toString;
        }
    }

    class MethodName : BasePatternInformation
    {
        private String _ReturnType = "";
        private String _MethodName = "";
        private String _FirstWord = "";
        private String _LastWord = "";

        public void SetNecessaryMembers(SyntaxNode method, SemanticModel semanticModel)
        {
            base.SetNecessaryMembers(method);
            if (!(method is BaseMethodDeclarationSyntax))
            {
                try
                {
                    method = method.Ancestors().OfType<BaseMethodDeclarationSyntax>().First();
                }
                catch
                {
                    return;
                }
            }

            try
            {
                if (method is ConstructorDeclarationSyntax)
                {
                    _MethodName = (method as ConstructorDeclarationSyntax).Identifier.ValueText;
                    _ReturnType = _MethodName;
                }
                else if (method is MethodDeclarationSyntax)
                {
                    var methodDeclaration = method as MethodDeclarationSyntax;
                    SetNecessaryMembers(methodDeclaration.ParameterList);

                    _ReturnType = methodDeclaration.ReturnType.ToString();
                    _MethodName = methodDeclaration.Identifier.ToString();
                }
                else
                {
                    Logger.Log("Skip method type: " + method.GetType().ToString().Split('.').Last());
                }

                Regex regex = new Regex("^[A-Z]*[a-z]*(?=[^a-z]|$)");
                Match match = regex.Match(_MethodName);
                _FirstWord = match.Value;

                regex = new Regex("[^a-z][a-z]+(?=[0-9_]*$)|(?<=[^A-Z])[A-Z]+(?=[0-9_]*$)");
                match = regex.Match(_MethodName);
                _LastWord = match.Value;
                if (!Char.IsLetter(_LastWord[0]))
                {
                    _LastWord = _LastWord.Substring(1);
                }
            }
            catch { }
        }

        public override string PrintStatement()
        {
            String toString = base.PrintStatement();
            toString = toString + split
                + _ReturnType + split
                + _MethodName + split
                + _FirstWord + split
                + _LastWord;
            return toString;
        }

        public string ToShortString()
        {
            String toString = _ReturnType + split + _MethodName;
            return toString;
        }
    }

    class FileClassMethodName : MethodName
    {
        private String _ClassName = "";
        private String _FileName = "";

        public new void SetNecessaryMembers(SyntaxNode node, SemanticModel semanticModel)
        {
            //Method Name
            base.SetNecessaryMembers(node, semanticModel);

            //File Name
            string FilePath = node.SyntaxTree.FilePath;
            _FileName = FilePath.Split('\\').Last();

            //Class Name
            ClassDeclarationSyntax classDeclaration = null;
            if (node is ClassDeclarationSyntax)
            {
                classDeclaration = node as ClassDeclarationSyntax;
            }
            else
            {
                try
                {
                    var classDecl = node.Ancestors().OfType<ClassDeclarationSyntax>().First();
                    classDeclaration = classDecl as ClassDeclarationSyntax;
                }
                catch {}
            }

            if (classDeclaration != null)
                _ClassName = classDeclaration.Identifier.ToString();
        }

        public override string PrintStatement()
        {
            return base.PrintStatement() + split + _ClassName + split + _FileName;
        }

        public new string ToShortString()
        {
            return base.ToShortString() + split + _ClassName + split + _FileName;
        }

    }
}
