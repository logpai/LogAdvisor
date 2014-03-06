using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Roslyn.Compilers;
using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;
using System.IO;

namespace CatchBlockExtraction
{
    abstract class BasePatternMatches : IMatchListsForMultiplePatterns
    {
        protected List<IMatchListForOnePattern> PatternCollection;

        public void PrintMatchedResults()
        {
            foreach (IMatchListForOnePattern pattern in PatternCollection)
            {
                pattern.PrintToFile(IOFileProcessing.FolderPath);
            }
        }

        public void CheckMutiplePatternsAndRecordMatches(SyntaxNode node, SemanticModel semanticModel)
        {
            foreach (IMatchListForOnePattern pattern in PatternCollection)
            {
                try
                {
                    pattern.CheckThisPatternAndRecordMatch(node, semanticModel);

                    //Break only for Mutually exclusive Mode.
                    if (Config.Orthogonal) break;
                }
                catch (DoesNotFitPatternException)
                {
                    //Continue
                }
                catch (Exception e)
                {
                    Logger.Log("Unknown Problem for Pattern " + pattern);
                    Logger.Log(e);
                }
            }
        }
    }

    class MatchbyPatternCategory : BasePatternMatches, IMatchListsForMultiplePatterns
    {
        public MatchbyPatternCategory()
        {
            // PatternCollection in Class BasePatternMathes
            PatternCollection = new List<IMatchListForOnePattern>();
            // Add all the pattern definitions here
            PatternCollection.Add(new CatchDic("CatchBlock"));
            PatternCollection.Add(new SysCallDic("AllCall"));
        }
    }

    class CatchDic : Dictionary<String, CatchList>, IMatchListForOnePattern
    {
        String _PatternName;
        public static int sampleID = 0;
        public CatchDic(String PatternName) : base()
        {
            _PatternName = PatternName;
        }

        public void CheckThisPatternAndRecordMatch(SyntaxNode node, SemanticModel semanticModel)
        {
            if ((new CatchBlock()).NeedToSave(node, semanticModel))
            {
                CatchBlock newMatch = new CatchBlock();
                newMatch.SetNecessaryMembers(node, semanticModel);
                Add(newMatch);
            }
            else
            {
                throw new DoesNotFitPatternException();
            }
        }

        private void Add(CatchBlock catchBlock)
        {
            String exception = catchBlock.ExceptionType;
            //Add
            try
            {
                this[exception].Add(catchBlock);
            }
            catch (KeyNotFoundException)
            {
                //Expected when this is the first time this exception type is seen.
                //Create a new list for this type.
                this.Add(exception, new CatchList());
                this[exception].Add(catchBlock);
            }

            //Update Statistics
            if (catchBlock.Logged)
            {
                this[exception].LogTotal++;
            }
            if (catchBlock.Thrown)
            {
                this[exception].ThrowTotal++;
            }
            if (catchBlock.Logged || catchBlock.Thrown)
            {
                this[exception].LogOrThrowTotal++;
            }
        }

        public void PrintToFile(String FilePath)
        {
            Logger.Log("Writing File " + _PatternName);
            StreamWriter Out = new StreamWriter(FilePath + "_" + _PatternName + ".txt");
            Out.WriteLine("{0} Different Exception Types in Total", this.Count);
            foreach (String exception in this.Keys)
            {
                Out.WriteLine("--------------------------------------------------------");
                CatchList catchList = this[exception];
                Out.WriteLine("Exception Type [{0}]: {1} in total, {2}/{1} Log, {3}/{1} Throw.",
                    exception,
                    catchList.Count,
                    catchList.LogTotal,
                    catchList.ThrowTotal);
                foreach (BasePatternInformation catchClause in catchList)
                {
                    Out.WriteLine(catchClause.PrintStatement());
                }
                Out.WriteLine();
                Out.WriteLine();
                Out.Flush();
            }
            Out.Flush();

            //Print summary
            Out.WriteLine("------------------------Summary-------------------------");
            int CatchCount = 0;
            int CatchThrow = 0;
            int CatchTypes = 0;
            int CatchLogged = 0;
            int CatchLogOrThrow = 0;
            Out.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}",
                    "Exception Type",
                    "Total",
                    "Log Total",
                    "Throw Total",
                    "Log||Throw Total");

            foreach (String exception in this.Keys)
            {
                var CatchList = this[exception];
                CatchTypes++;
                CatchCount += CatchList.Count;
                CatchThrow += CatchList.ThrowTotal;
                CatchLogged += CatchList.LogTotal;
                CatchLogOrThrow += CatchList.LogOrThrowTotal;
                Out.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}",
                    exception,
                    CatchList.Count,
                    CatchList.LogTotal,
                    CatchList.ThrowTotal,
                    CatchList.LogOrThrowTotal
                    );
            }
            Out.WriteLine("--------------------------------------------------------");
            Out.WriteLine("{0} Catch Blocks of {1} Different Types in Total. ({2}/{0} Throw, {3}/{0} Logged, {4}/{0} LogOrThrow)",
                           CatchCount,
                           CatchTypes,
                           CatchThrow,
                           CatchLogged,
                           CatchLogOrThrow);
            Out.WriteLine("--------------------------------------------------------");
            Out.Close();
            Logger.Log("Completed Writing " + _PatternName);
        }
    }

    class CatchList : List<CatchBlock>
    {
        public int LogTotal = 0; 
        public int ThrowTotal = 0;
        public int LogOrThrowTotal = 0;
    }

}
