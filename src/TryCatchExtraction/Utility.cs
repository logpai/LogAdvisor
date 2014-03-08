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
    /// <summary>
    /// Set up Logger class
    /// </summary>
    public static class Logger
    {
        static string LogFileName;
        static StreamWriter LogWriter;
        public static void Initialize()
        {
            LogFileName = IOFile.CompleteFileName(
                DateTime.Today.Date.ToShortDateString().Replace("/", "") + ".log");
            LogWriter = File.AppendText(LogFileName);
            Log("-------------------------------------------------------");
            Log("-------------------------------------------------------");
            Log("New Task.");
        }
        public static void Log(String message)
        {
            Console.WriteLine("[{0}] {1}", DateTime.Now.ToString(), message);
            LogWriter.WriteLine("[{0}] {1}", DateTime.Now.ToString(), message);
            LogWriter.Flush();
        }
        public static void Log(int number)
        {
            Log(number.ToString());
        }
        public static void Log(Exception e)
        {
            Console.WriteLine(e);
            LogWriter.WriteLine(e);
            LogWriter.Flush();
        }
        public static void Close()
        {
            LogWriter.Close();
        }
    }

    /// <summary>
    /// Set up Config class to process the config file
    /// </summary>
    static class Config
    {
        static public String[] LogMethods { get; private set; } // "WriteError"
        static public String[] NotLogMethods { get; private set; } // "TraceUtil.If"
        static public int LogLevelArgPos { get; private set; } // ="2"
        static public int AssertConditionIndex { get; private set; }
        static public bool Orthogonal;

        static public void Load(String FileName)
        {
            StreamReader Input = new StreamReader(FileName);

            try
            {
                LogMethods = GetOneParameter(Input).Split(',');
                NotLogMethods = GetOneParameter(Input).Split(',');
                LogLevelArgPos = Convert.ToInt32(GetOneParameter(Input));
                String temp = GetOneParameter(Input);
                if (temp == "O") Orthogonal = true;
                else if (temp == "N") Orthogonal = false;
                else throw new IOException();
                AssertConditionIndex = Convert.ToInt32(GetOneParameter(Input));
            }
            catch
            {
                Logger.Log("Illegal Configure File Format.");
            }
            finally
            {
                Input.Close();
            }
        }

        static private String GetOneParameter(StreamReader Input)
        {
            String Parameter = Input.ReadLine();
            Parameter = Parameter.Split('%')[0];
            return Parameter;
        }
    }

    /// <summary>
    /// File name processing
    /// </summary>
    static class IOFile
    {
        public static String FolderPath;
       
        public static String CompleteFileName(String tail)
        {
            return (FolderPath + "\\" + FolderPath.Split('\\').Last() + "_" + tail);
        }

        static public String DeleteSpace(String str)
        {
            return str.Replace("\n", "").Replace("\r", "").Replace("\t", "")
                .Replace("    ", " ").Replace("    ", " ").Replace("   ", " ")
                .Replace("  ", " ");
        }

        static public String TokenizeMethodName(String str)
        {
            try
            {
                String methodName = str.Split('(').First();
                return methodName;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Set up the FileInfo class, with source file name and line number
    /// </summary>
    class FileInfo // While Inputing By Folder, To Recognize Line Number in Original Source Files
    {
        public String FileName;
        public int Lines;

        public FileInfo(String fileName, int totalLines)
        {
            FileName = fileName;
            Lines = totalLines;
        }
    }

    class FolderInfo : List<FileInfo>
    {
        public static /*readonly*/ String InputMode;

        public String GetFileNameAndLine(SyntaxNode node)
        {
            String ID;
            int Line = node.SyntaxTree.GetLineSpan(node.Span, false).StartLinePosition.Line + 1; //0-base to 1-base
            if (InputMode == "ByFolder")
            {
                FileInfo realPos = FindFile(Line);
                ID = realPos.FileName + ":" + realPos.Lines.ToString();
            }
            else
            {
                ID = node.SyntaxTree.FilePath + ":" + Line.ToString();
            }
            return ID;
        }

        public String GetFileName(SyntaxNode node)
        {
            String filename;
            int Line = node.SyntaxTree.GetLineSpan(node.Span, false).StartLinePosition.Line + 1; //0-base to 1-base
            if (InputMode == "ByFolder")
            {
                FileInfo realPos = FindFile(Line);
                filename = realPos.FileName;
            }
            else
            {
                filename = node.SyntaxTree.FilePath;
            }
            return filename;
        }

        private FileInfo FindFile(int MixedFileLineNumber)
        {
            foreach (FileInfo file in this)
            {
                if (MixedFileLineNumber > file.Lines)
                {
                    MixedFileLineNumber -= file.Lines;
                }
                else
                {
                    return new FileInfo(file.FileName, MixedFileLineNumber);
                }
            }
            return null;
        }
    }

}