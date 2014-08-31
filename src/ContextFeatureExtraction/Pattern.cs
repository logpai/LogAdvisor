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

namespace ContextFeatureExtraction
{
    class TreeStatistics
    {
        public Dictionary<String, int> CodeStats;
        public List<CatchBlock> CatchBlockList;
        public List<APICall> APICallList;

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
            CodeStats.Add("NumCallType", 0);
            CodeStats.Add("NumAPICall", 0);
            CodeStats.Add("NumLoggedAPICall", 0);
        }

        public static void Add<T>(ref Dictionary<T, int> dic1, Dictionary<T, int> dic2)
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

    }

    class CodeStatistics : TreeStatistics
    {
        public List<Tuple<SyntaxTree, TreeStatistics>> TreeStats;
        public CatchDic CatchBlocks;
        public CallDic APICalls;

        public CodeStatistics(List<Tuple<SyntaxTree, TreeStatistics>> treestats)
        {
            TreeStats = treestats;
            CatchBlocks = new CatchDic();
            APICalls = new CallDic();
            CodeStats = new Dictionary<String, int>();
            foreach (var treetuple in treestats)
            {
                if (treetuple == null) continue;
                CatchBlocks.Add(treetuple.Item2.CatchBlockList);
                APICalls.Add(treetuple.Item2.APICallList);
                CodeAnalyzer.MergeDic<String>(ref CodeStats, treetuple.Item2.CodeStats);               
            }
            CodeStats["NumExceptionType"] = CatchBlocks.Count;
            CodeStats["NumLoggedCatchBlock"] = CatchBlocks.NumLogged;
            CodeStats["NumCallType"] = APICalls.Count;
            CodeStats["NumAPICall"] = APICalls.NumAPICall;
            CodeStats["NumLoggedAPICall"] = APICalls.NumLogged;
        }

        public void PrintSatistics()
        {           
            foreach (var stat in CodeStats.Keys)
            {
                Logger.Log(stat + ": " + CodeStats[stat]);
            }
            CatchBlocks.PrintToFile();
            APICalls.PrintToFile();
        }
    }

    class CommonFeature
    {
        public Dictionary<String, int> OperationFeatures;
        public Dictionary<String, int> TextFeatures;
        public Dictionary<String, String> MetaInfo;

        public const String Splitter = "\t";

        public CommonFeature()
        {
            OperationFeatures = new Dictionary<String, int>();
            MetaInfo = new Dictionary<String, String>();
            OperationFeatures.Add("Logged", 0);
            OperationFeatures.Add("Thrown", 0);
            OperationFeatures.Add("SetLogicFlag", 0);
            OperationFeatures.Add("Return", 0);
            OperationFeatures.Add("LOC", 0);
            OperationFeatures.Add("NumMethod", 0);
            MetaInfo.Add("FilePath", null);
            MetaInfo.Add("Line", null);
            MetaInfo.Add("Logged", null);
            MetaInfo.Add("Thrown", null);
            MetaInfo.Add("SetLogicFlag", null);
            MetaInfo.Add("Return", null);
        }

    }

    class CatchBlock : CommonFeature
    {
        public String ExceptionType;
        public static List<String> MetaKeys;

        public CatchBlock() : base() 
        {
            OperationFeatures.Add("EmptyBlock", 0);
            OperationFeatures.Add("RecoverFlag", 0);
            OperationFeatures.Add("OtherOperation", 0);
            MetaInfo.Add("RecoverFlag", null);
            MetaInfo.Add("OtherOperation", null);
            MetaInfo.Add("CatchBlock", null);
            MetaKeys = MetaInfo.Keys.ToList();
        }

        public String PrintFeatures() 
        {
            String features = null;
            foreach (var key in OperationFeatures.Keys)
            {
                features += (key + ":" + OperationFeatures[key] + Splitter);
            }
            features += (ExceptionType + Splitter);
            foreach (var key in TextFeatures.Keys)
            {
                features += (key + ":" + TextFeatures[key] + Splitter);
            }
            return features;
        }

        public String PrintMetaInfo()
        {
            String metaInfo = null;
            foreach (var key in MetaInfo.Keys)
            {
                metaInfo += (IOFile.DeleteSpace(MetaInfo[key]) + Splitter);
            }
            return metaInfo;
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
                if (catchBlock == null) continue;
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
                if (catchBlock.OperationFeatures["Logged"] == 1)
                {
                    this[exception].NumLogged++;
                    NumLogged++;
                    if (catchBlock.OperationFeatures["Thrown"] == 1)
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
                if (catchBlock.OperationFeatures["Thrown"] == 1)
                {
                    this[exception].NumThrown++;
                    NumThrown++;
                }
            }
        }

        public void PrintToFile()
        {
            Logger.Log("Writing CatchBlock features into file...");
            StreamWriter sw = new StreamWriter(IOFile.CompleteFileName("CatchBlock.txt"));
            StreamWriter metaSW = new StreamWriter(IOFile.CompleteFileName("CatchBlock_Meta.txt"));
            int catchId = 0;
            String metaKey = CatchBlock.Splitter;
            foreach (var meta in CatchBlock.MetaKeys)
            {
                metaKey += (meta + CatchBlock.Splitter);
            }
            metaSW.WriteLine(metaKey);
            metaSW.WriteLine("--------------------------------------------------------");
            metaSW.WriteLine("NumExceptionType: {0}, NumCatchBlock: {1}, NumLogged: {2}, "
                    + "NumThrown: {3}, NumLoggedAndThrown: {4}, NumLoggedNotThrown: {5}.",
                    this.Count,
                    NumCatch,
                    NumLogged,
                    NumThrown,
                    NumLoggedAndThrown,
                    NumLoggedNotThrown);
            metaSW.WriteLine();

            foreach (String exception in this.Keys)
            {
                metaSW.WriteLine("--------------------------------------------------------");
                CatchList catchList = this[exception];
                metaSW.WriteLine("Exception Type [{0}]: NumCatchBlock: {1}, NumLogged: {2}, "
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
                    catchId++;
                    sw.WriteLine("ID:" + catchId + CatchBlock.Splitter + catchblock.PrintFeatures());
                    metaSW.WriteLine("ID:" + catchId + CatchBlock.Splitter + catchblock.PrintMetaInfo());
                }
                metaSW.WriteLine();
                metaSW.WriteLine();
                sw.Flush();
                metaSW.Flush();
            }

            //Print summary
            metaSW.WriteLine("------------------------ Summary -------------------------");
            metaSW.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                    "Exception Type",
                    "NumCatch",
                    "NumLogged",
                    "NumThrown",
                    "NumLoggedAndThrown",
                    "NumLoggedNotThrown");

            foreach (String exception in this.Keys)
            {
                var catchList = this[exception];
                metaSW.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                        exception,
                        catchList.Count,
                        catchList.NumLogged,
                        catchList.NumThrown,
                        catchList.NumLoggedAndThrown,
                        catchList.NumLoggedNotThrown
                        );
            }
            sw.Close();
            metaSW.Close();
            Logger.Log("Writing done.");
        }
    }

    class APICall : CommonFeature
    {
        public String CallType;
        public static List<String> MetaKeys;

        public APICall() : base()
        {
            OperationFeatures.Add("EmptyBlock", 0);
            OperationFeatures.Add("RecoverFlag", 0);
            OperationFeatures.Add("OtherOperation", 0);
            MetaInfo.Add("RecoverFlag", null);
            MetaInfo.Add("OtherOperation", null);
            MetaInfo.Add("CheckIfBlock", null);
            MetaKeys = MetaInfo.Keys.ToList();
        }

        public String PrintFeatures()
        {
            String features = null;
            foreach (var key in OperationFeatures.Keys)
            {
                features += (key + ":" + OperationFeatures[key] + Splitter);
            }
            features += (CallType + Splitter);
            foreach (var key in TextFeatures.Keys)
            {
                features += (key + ":" + TextFeatures[key] + Splitter);
            }
            return features;
        }

        public String PrintMetaInfo()
        {
            String metaInfo = null;
            foreach (var key in MetaInfo.Keys)
            {
                metaInfo += (IOFile.DeleteSpace(MetaInfo[key]) + Splitter);
            }
            return metaInfo;
        }
    }

    class CallList : List<APICall>
    {
        public int NumLogged = 0;
        public int NumThrown = 0;
        public int NumLoggedAndThrown = 0;
        public int NumLoggedNotThrown = 0;
    }

    class CallDic : Dictionary<String, CallList>
    {
        public int NumAPICall = 0;
        public int NumLogged = 0;
        public int NumThrown = 0;
        public int NumLoggedAndThrown = 0;
        public int NumLoggedNotThrown = 0;

        public void Add(List<APICall> callList)
        {
            foreach (var apiCall in callList)
            {
                if (apiCall == null) continue;
                NumAPICall++;
                String calltype = apiCall.CallType;
                if (this.ContainsKey(calltype))
                {
                    this[calltype].Add(apiCall);
                }
                else
                {
                    //Create a new list for this type.
                    this.Add(calltype, new CallList());
                    this[calltype].Add(apiCall);
                }

                //Update Statistics
                if (apiCall.OperationFeatures["Logged"] == 1)
                {
                    this[calltype].NumLogged++;
                    NumLogged++;
                    if (apiCall.OperationFeatures["Thrown"] == 1)
                    {
                        this[calltype].NumLoggedAndThrown++;
                        NumLoggedAndThrown++;
                    }
                    else
                    {
                        this[calltype].NumLoggedNotThrown++;
                        NumLoggedNotThrown++;
                    }
                }
                if (apiCall.OperationFeatures["Thrown"] == 1)
                {
                    this[calltype].NumThrown++;
                    NumThrown++;
                }
            }
        }

        public void PrintToFile()
        {
            Logger.Log("Writing APICall features into file...");
            StreamWriter sw = new StreamWriter(IOFile.CompleteFileName("APICall.txt"));
            StreamWriter metaSW = new StreamWriter(IOFile.CompleteFileName("APICall_Meta.txt"));
            int callId = 0;
            String metaKey = CatchBlock.Splitter;
            foreach (var meta in CatchBlock.MetaKeys)
            {
                metaKey += (meta + CatchBlock.Splitter);
            }
            metaSW.WriteLine(metaKey);
            metaSW.WriteLine("--------------------------------------------------------");
            metaSW.WriteLine("NumCallType: {0}, NumAPICall: {1}, NumLogged: {2}, "
                    + "NumThrown: {3}, NumLoggedAndThrown: {4}, NumLoggedNotThrown: {5}.",
                    this.Count,
                    NumAPICall,
                    NumLogged,
                    NumThrown,
                    NumLoggedAndThrown,
                    NumLoggedNotThrown);
            metaSW.WriteLine();

            foreach (String calltype in this.Keys)
            {
                metaSW.WriteLine("--------------------------------------------------------");
                CallList callList = this[calltype];
                metaSW.WriteLine("Call Type [{0}]: NumAPICall: {1}, NumLogged: {2}, "
                        + "NumThrown: {3}, NumLoggedAndThrown: {4}, NumLoggedNotThrown: {5}.",
                        calltype,
                        callList.Count,
                        callList.NumLogged,
                        callList.NumThrown,
                        callList.NumLoggedAndThrown,
                        callList.NumLoggedNotThrown
                        );
                foreach (var apicall in callList)
                {
                    callId++;
                    sw.WriteLine("ID:" + callId + APICall.Splitter + apicall.PrintFeatures());
                    metaSW.WriteLine("ID:" + callId + APICall.Splitter + apicall.PrintMetaInfo());
                }
                metaSW.WriteLine();
                metaSW.WriteLine();
                sw.Flush();
                metaSW.Flush();
            }

            //Print summary
            metaSW.WriteLine("------------------------ Summary -------------------------");
            metaSW.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                    "Call Type",
                    "NumAPICall",
                    "NumLogged",
                    "NumThrown",
                    "NumLoggedAndThrown",
                    "NumLoggedNotThrown");

            foreach (String exception in this.Keys)
            {
                var catchList = this[exception];
                metaSW.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                        exception,
                        catchList.Count,
                        catchList.NumLogged,
                        catchList.NumThrown,
                        catchList.NumLoggedAndThrown,
                        catchList.NumLoggedNotThrown
                        );
            }
            sw.Close();
            metaSW.Close();
            Logger.Log("Writing done.");
        }
    }
}

