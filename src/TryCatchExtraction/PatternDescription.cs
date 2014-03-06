using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Roslyn.Compilers;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;
using System.IO;
using Roslyn.Services;

namespace CatchBlockExtraction
{
    class Feature
    {
        public bool Logged = false;

        public Dictionary<String, int> CodeStats;
        public Dictionary<String, int> ExceptionTypeDic;

        public CodeStatistics()
        {
        }
    }

    class CatchBlock : Feature, IPattern
    {
        public String ExceptionType { get { return _ExceptionType; } }
        private bool _Thrown = false;
        public bool Thrown { get { return _Thrown; } }
        private bool _SetLogicFlag = false;
        public bool SetLogicFlag { get { return _SetLogicFlag; } }
        private bool _Logged = false;
        public bool Logged { get { return _Logged; } }
        private bool _HasCriticalOperation = false;
        private bool _Return = false;
        private bool _EmptyBlock = false;
        private bool _RecoverFlag = false;
        private PatternKeyInformation _RecoverStatement;
        private PatternKeyInformation _LogInCatch;
        private PatternKeyInformation _ThrowInCatch;
        private PatternKeyInformation _SetFlagInCatch;
        private String _ExceptionType; 
        private FileClassMethodName _FileClassMethodName;
        private ContextAPI _ContextAPI;

        public CatchBlock() : base() 
        { 
        }

        public bool NeedToSave(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node is CatchClauseSyntax)
            {
                return true;
            }
            return false;
        }

        public void SetNecessaryMembers(SyntaxNode node, SemanticModel semanticModel)
        {
            CatchClauseSyntax catchClause = node as CatchClauseSyntax;
            // get ID and content of node
            base.SetNecessaryMembers(catchClause);

            InvocationExpressionSyntax logging = CodeAnalyzer.FindLoggingIn(catchClause);
            _Logged = !(logging == null);
            _LogInCatch = new PatternKeyInformation(logging);

            _ExceptionType = CodeAnalyzer.GetExceptionType(catchClause, semanticModel);
            
            ThrowStatementSyntax throwStatement = CodeAnalyzer.FindThrowIn(catchClause);
            _Thrown = !(throwStatement == null);
            _ThrowInCatch = new PatternKeyInformation(throwStatement, semanticModel);

            StatementSyntax setLogicFlagStatement = CodeAnalyzer.FindSetLogicFlagIn(catchClause);
            _SetLogicFlag = !(setLogicFlagStatement == null);
            _SetFlagInCatch = new PatternKeyInformation(setLogicFlagStatement, "Flag");

            _Return = CodeAnalyzer.IsReturnStatement(catchClause);

            StatementSyntax recoverStatement = CodeAnalyzer.FindRecoverStatement(catchClause, semanticModel);
            _RecoverFlag = !(recoverStatement == null);
            _RecoverStatement = new PatternKeyInformation(recoverStatement, "Recover");

            _HasCriticalOperation = CodeAnalyzer.HasCriticalOperation(catchClause, semanticModel);

            _EmptyBlock = !(_Thrown || _SetLogicFlag || _Return || _RecoverFlag || _HasCriticalOperation);

            _FileClassMethodName = new FileClassMethodName();
            _FileClassMethodName.SetNecessaryMembers(node, semanticModel);

            _ContextAPI = new ContextAPI();
            _ContextAPI.SetNecessaryMembers(catchClause, semanticModel);
        }

        /// <summary>
        /// print format:
        ///     "[Logged]/[Not Logged]" + _LogInCatch + "Throw/NoThrow" + _ThrowInCatch 
        ///     + _ExceptionType + ID(filePath:lineNumber) + Statement + _FileClassMethodName
        ///     + _ContextAPI 
        /// </summary>
        /// <returns></returns>
        public override String PrintStatement()
        { 
            var toString = base.PrintStatement();
            toString = _ExceptionType + split + toString;

            toString = PrintBoolFeature(_EmptyBlock, "Empty") + split + toString;
            toString = PrintBoolFeature(_HasCriticalOperation, "Operation") + split + toString;
            toString = PrintBoolFeature(_Return, "Return") + split + toString;
            toString = _RecoverStatement.PrintStatement() + split + toString; 
            toString = _SetFlagInCatch.PrintStatement() + split + toString;
            toString = _ThrowInCatch.PrintStatement() + split + toString;
            toString = PrintBoolFeature(_Thrown, "Throw") + split + toString;
            toString = _LogInCatch.PrintStatement() + split + toString;
            toString = PrintBoolFeature(_Logged, "Logged") + split + toString;       

            CatchDic.sampleID++;
            toString = "ID:" + CatchDic.sampleID.ToString() + split + toString;
            toString = toString + split + _FileClassMethodName.ToShortString();
            toString = toString + split + _ContextAPI.ToShortString();
            return toString;
        }
    }
}
