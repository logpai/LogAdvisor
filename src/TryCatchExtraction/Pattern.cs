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
    class TreeStatistics
    {
        public Dictionary<String, int> CodeStats;
        public List<CatchBlock> CatchList;

        public TreeStatistics()
        {
            CodeStats = new Dictionary<String, int>();
            CodeStats.Add("NumLOC", 0);
            CodeStats.Add("NumLoggedLOC", 0);
            CodeStats.Add("NumCall", 0);
            CodeStats.Add("NumLogging", 0);
            CodeStats.Add("NumClass", 0);
            CodeStats.Add("NumLoggedClass", 0);
            CodeStats.Add("NumMethod", 0);
            CodeStats.Add("NumLoggedMethod", 0);
            CodeStats.Add("NumLoggedFile", 0);
            CodeStats.Add("NumCatchBlock", 0);
            CodeStats.Add("NumLoggedCatchBlock", 0);
            CodeStats.Add("NumExceptionType", 0);
        }

        public static void Add<T>(ref Dictionary<T, int> dic1, Dictionary<T, int> dic2)
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

    }

    class CodeStatistics : TreeStatistics
    {
        public List<Tuple<SyntaxTree, TreeStatistics>> TreeStats;
        public CatchDic CatchBlocks;

        public CodeStatistics(List<Tuple<SyntaxTree, TreeStatistics>> treestats)
        {
            TreeStats = treestats;
            CatchBlocks = new CatchDic();
            CodeStats = new Dictionary<String, int>();
            foreach (var treetuple in treestats)
            {
                CatchBlocks.Add(treetuple.Item2.CatchList);
                CodeAnalyzer.MergeDic<String>(ref CodeStats, treetuple.Item2.CodeStats);               
            }
            CodeStats["NumExceptionType"] = CatchBlocks.Count;
            CodeStats["NumLoggedCatchBlock"] = CatchBlocks.NumLogged;
        }

        public void PrintSatistics()
        {           
            foreach (var stat in CodeStats.Keys)
            {
                Logger.Log(stat + ": " + CodeStats[stat]);
            }
            CatchBlocks.PrintToFile(IOFile.CompleteFileName("CatchBlock.txt"));
        }
    }

    class CommonFeature
    {
        public Dictionary<String, int> BoolFeatures;
        public Dictionary<String, int> MethodFeatures;
        public Dictionary<String, int> VariableFeatures;
        public Dictionary<String, String> MetaInfo;
        public const String Splitter = "\t";

        public CommonFeature()
        {
            BoolFeatures = new Dictionary<String, int>();
            MetaInfo = new Dictionary<String, String>();
            BoolFeatures.Add("Logged", 0);
            BoolFeatures.Add("Thrown", 0);
            MetaInfo.Add("Thrown", null);
            BoolFeatures.Add("SetLogicFlag", 0);
            MetaInfo.Add("SetLogicFlag", null);
            BoolFeatures.Add("Return", 0);
            MetaInfo.Add("Return", null);
        }

    }

    class CatchBlock : CommonFeature
    {
        public String ExceptionType;

        public CatchBlock() : base() 
        {
            BoolFeatures.Add("EmptyBlock", 0);
            BoolFeatures.Add("RecoverFlag", 0);
            MetaInfo.Add("RecoverFlag", null);
            BoolFeatures.Add("OtherOperation", 0);
            MetaInfo.Add("OtherOperation", null);
        }

        public String PrintFeatures() 
        {
            String features = null;
            foreach (var key in BoolFeatures.Keys)
            {
                features += (key + ":" + BoolFeatures[key] + Splitter);
            }
            features += (ExceptionType + Splitter);
            foreach (var key in MethodFeatures.Keys)
            {
                features += (key + ":" + MethodFeatures[key] + Splitter);
            }
            return features;
        }
    }

    class CatchList : List<CatchBlock>
    {
        public int NumLogged = 0;
        public int NumThrown = 0;
        public int NumLoggedAndThrown = 0;
        public int NumLoggedNotThrown = 0;
    }

    class CatchDic : Dictionary<String, CatchList>
    {
        public int NumCatch = 0;
        public int NumLogged = 0;
        public int NumThrown = 0;
        public int NumLoggedAndThrown = 0;
        public int NumLoggedNotThrown = 0;

        public void Add(List<CatchBlock> catchList)
        {
            foreach (var catchBlock in catchList)
            {
                NumCatch++;
                String exception = catchBlock.ExceptionType;
                if (this.ContainsKey(exception))
                {
                    this[exception].Add(catchBlock);
                }
                else
                {
                    //Create a new list for this type.
                    this.Add(exception, new CatchList());
                    this[exception].Add(catchBlock);
                }

                //Update Statistics
                if (catchBlock.BoolFeatures["Logged"] == 1)
                {
                    this[exception].NumLogged++;
                    NumLogged++;
                    if (catchBlock.BoolFeatures["Thrown"] == 1)
                    {
                        this[exception].NumLoggedAndThrown++;
                        NumLoggedAndThrown++;
                    }
                    else
                    {
                        this[exception].NumLoggedNotThrown++;
                        NumLoggedNotThrown++;
                    }
                }
                if (catchBlock.BoolFeatures["Thrown"] == 1)
                {
                    this[exception].NumThrown++;
                    NumThrown++;
                }
            }
        }

        public void PrintToFile(String filePath)
        {
            Logger.Log("Writing CatchBlock feature into file...");
            StreamWriter sw = new StreamWriter(filePath);

            foreach (String exception in this.Keys)
            {
                sw.WriteLine("--------------------------------------------------------");
                CatchList catchList = this[exception];
                sw.WriteLine("Exception Type [{0}]: NumCatchBlock: {1}, NumLogged: {2}, "
                    + "NumThrown: {3}, NumLoggedAndThrown: {4}, NumLoggedNotThrown: {5}.",
                    exception,
                    catchList.Count,
                    catchList.NumLogged,
                    catchList.NumThrown,
                    catchList.NumLoggedAndThrown,
                    catchList.NumLoggedNotThrown
                    );
                foreach (var catchblock in catchList)
                {
                    sw.WriteLine(catchblock.PrintFeatures());
                }
                sw.WriteLine();
                sw.WriteLine();
                sw.Flush();
            }
            sw.Flush();

            //Print summary
            sw.WriteLine("------------------------ Summary -------------------------");
            sw.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                    "Exception Type",
                    "NumCatch",
                    "NumLogged",
                    "NumThrown",
                    "NumLoggedAndThrown",
                    "NumLoggedNotThrown");

            foreach (String exception in this.Keys)
            {
                var catchList = this[exception];
                sw.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                    exception,
                    catchList.Count,
                    catchList.NumLogged,
                    catchList.NumThrown,
                    catchList.NumLoggedAndThrown,
                    catchList.NumLoggedNotThrown
                    );
            }
            sw.WriteLine("--------------------------------------------------------");
            sw.WriteLine("NumExceptionType: {0}, NumCatchBlock: {1}, NumLogged: {2}, "
                + "NumThrown: {3}, NumLoggedAndThrown: {4}, NumLoggedNotThrown: {5}.",
                           this.Count,
                           NumCatch,
                           NumLogged,
                           NumThrown,
                           NumLoggedAndThrown,
                           NumLoggedNotThrown);
            sw.WriteLine("--------------------------------------------------------");
            sw.Close();
            Logger.Log("CatchBlock features done.");
        }
    }
}
