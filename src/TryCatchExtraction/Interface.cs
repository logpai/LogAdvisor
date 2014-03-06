using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Roslyn.Compilers;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;

namespace CatchBlockExtraction
{
    interface IPattern
    {
        void SetNecessaryMembers(SyntaxNode node, SemanticModel semanticModel);
        String PrintStatement();
        bool NeedToSave(SyntaxNode node, SemanticModel semanticModel);
    }

    interface IMatchListForOnePattern
    {
        void CheckThisPatternAndRecordMatch(SyntaxNode node, SemanticModel semanticModel);
        void PrintToFile(String FilePath);
    }

    interface IMatchListsForMultiplePatterns
    {
        void CheckMutiplePatternsAndRecordMatches(SyntaxNode node, SemanticModel semanticModel);
        void PrintMatchedResults();
    }

}
