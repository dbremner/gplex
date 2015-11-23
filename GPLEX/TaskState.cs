// Gardens Point Scanner Generator
// Copyright (c) K John Gough, QUT 2006-2014
// (see accompanying GPLEXcopyright.rtf)

using System;
using System.IO;
using System.Xml;
using System.Reflection;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using QUT.Gplex.Parser;
using QUT.GplexBuffers;

namespace QUT.Gplex.Automaton
{
	internal enum OptionState { clear, needUsage, needCodepageHelp, inconsistent, alphabetLocked, errors};

    /// <summary>
    /// A singleton of this type holds the main program state during
    /// processing a LEX file when run from the command line. It sets
    /// up the parser, scanner, errorHandler and AAST objects and calls
    /// Parse() on the parser.  When the parser is invoked by Visual
    /// Studio, by contrast, there is no task state and Parse is called
    /// from the managed babel wrapper.
    /// </summary>
	internal class TaskState : IDisposable
	{
        internal const int minGplexxVersion = 285;

        internal const int utf8CP = 65001;    // ==> Create a UTF-8 decoder buffer.
        internal const int defaultCP = 0;     // ==> Use the machine default codepage
        internal const int guessCP = -2;      // ==> Read the file to guess the codepage
        internal const int rawCP = -1;        // ==> Do not do any decoding of the binary.

        const string dotLexLC = ".lex";
        const string dotLexUC = ".LEX";
        const string dotLst = ".lst";
        const string dotCs = ".cs";
        const string bufferCodeName = "GplexBuffers.cs";

        const int notSet = -1;
        const int asciiCardinality = 256;
        const int unicodeCardinality = 0x110000;

        int targetSymCardinality = notSet;

        bool checkOnly;
        //bool utf8default;
        bool compressMapExplicit;
        //bool compressNextExplicit;
        bool compressMap;                  // No default compress of the class map
        string pathName;                   // Input file path string from user.
        string outName;                    // Output file path string (possibly empty)
        string baseName;                   // Base name of input file, without extension.
        // string exeDirNm;                   // Directory name from which program executed.
        string userFrame;                  // Usually null.  Set by the /frame:path options
        // string dfltFrame = "gplexx.frame"; // Default frame name.

        Stream inputFile;
        Stream listFile;
        Stream frameFile;
        Stream outputFile;

        StreamWriter listWriter;

        NFSA nfsa;
		DFSA dfsa;
        internal Partition partition;

		internal AAST aast;
        internal ErrorHandler handler;
        internal QUT.Gplex.Parser.Parser parser;
        internal QUT.Gplex.Lexer.Scanner scanner;

		internal TaskState() 
        {
            Assembly assm = Assembly.GetExecutingAssembly();
            object info = Attribute.GetCustomAttribute(assm, typeof(AssemblyFileVersionAttribute));
            this.VerString = ((AssemblyFileVersionAttribute)info).Version;
        }

        public void Dispose()
        {
            if (inputFile != null)
                inputFile.Close();
            if (listFile != null)
                listFile.Close();
            if (frameFile != null)
                frameFile.Close();
            if (outputFile != null)
                outputFile.Close();
            GC.SuppressFinalize(this);
        }

		// Support for various properties of the task
        internal bool Files { get; private set; } = true;
        internal bool Stack { get; private set; }
        internal bool Babel { get; private set; }
        internal bool Verbose { get; private set; }
        internal bool HasParser { get; private set; } = true;
        internal bool ChrClasses { get; private set; }
        internal bool EmbedBuffers { get; private set; } = true;
        internal bool CaseAgnostic { get; private set; }
        internal bool EmitInfoHeader { get; private set; } = true;

        internal bool Version { get; private set; } //emit version info
        internal bool Summary { get; private set; }
        internal bool Listing { get; private set; }

        internal bool ParseOnly { get; private set; }
        internal bool Persist { get; private set; } = true;
        internal bool Errors     { get { return handler.Errors; } }
        internal bool CompressMap  {
            // If useUnicode, we obey the compressMap Boolean.
            // If compressMapExplicit, we obey the compressMap Boolean.
            // Otherwise we return false.
            // 
            // The result of this is that the default for unicode
            // is to compress both map and next-state tables, while
            // for 8-bit scanners we compress next-state but not map.
            get {
                if (Unicode || compressMapExplicit)
                    return compressMap;
                else
                    return false;
            } 
        }
        /// <summary>
        /// Compress both map and next-state
        /// but do not use two-level compression
        /// to trade time for space.
        /// </summary>
        internal bool Squeeze { get; private set; }


        /// <summary>
        /// Default compress the next-state tables
        /// </summary>
        internal bool CompressNext { get; private set; } = true;

        /// <summary>
        /// Minimize automaton
        /// </summary>
        internal bool Minimize { get; private set; } = true;

        internal bool Warnings   { get { return handler.Warnings; } }

        /// <summary>
        /// use Unicode
        /// </summary>
        internal bool Unicode { get; private set; }


        internal bool ErrorsToConsole { get; private set; }

        /// <summary>
        /// default codepage
        /// </summary>
        internal int    CodePage { get; private set; }

        internal int    ErrNum     { get { return handler.ErrNum; } }
        internal int    WrnNum     { get { return handler.WrnNum; } }
        internal string VerString { get; }

        /// <summary>
        /// name of input file
        /// </summary>
        internal string FileName { get; private set; }

        /// <summary>
        /// Pathname plus last write DateTime.
        /// </summary>
        internal string InputInfo { get; private set; }

        /// <summary>
        /// Path of the frame file actually used
        /// </summary>
        internal string FrameName { get; private set; }

        internal TextWriter Msg { get; private set; } = Console.Out;

        internal int HostSymCardinality { get; } = asciiCardinality;

        internal int TargetSymCardinality { 
            get 
            {
                if (targetSymCardinality == notSet)
                    targetSymCardinality = asciiCardinality;
                return targetSymCardinality;
            }
        }

        internal TextWriter ListStream
        {
            get
            {
                if (listWriter == null) 
                    listWriter = ListingFile(baseName + dotLst);
                return listWriter;
            }
        }

		// parse the command line options
		internal OptionState ParseOption(string option)
		{
            string arg = option.ToUpperInvariant();
            if (arg.StartsWith("OUT:", StringComparison.Ordinal))
            {
                outName = option.Substring(4);
                if (outName.Equals("-"))
                    Msg = Console.Error;
            }
            else if (arg.StartsWith("FRAME:", StringComparison.Ordinal))
                userFrame = arg.Substring(6);
            else if (arg.Equals("HELP", StringComparison.Ordinal) || arg.Equals("?"))
                return OptionState.needUsage;
            else if (arg.Equals("ERRORSTOCONSOLE", StringComparison.Ordinal))
                ErrorsToConsole = true;
            else if (arg.Contains("CODEPAGE") && (arg.Contains("HELP") || arg.Contains("?")))
                return OptionState.needCodepageHelp;
            else if (arg.StartsWith("CODEPAGE:", StringComparison.Ordinal))
                CodePage = CodePageHandling.GetCodePage(option);
            else
            {
                bool negate = arg.StartsWith("NO", StringComparison.Ordinal);

                if (negate) 
                    arg = arg.Substring(2);
                if (arg.Equals("CHECK", StringComparison.Ordinal)) checkOnly = !negate;
                else if (arg.StartsWith("CASEINSEN", StringComparison.Ordinal)) CaseAgnostic = !negate;
                else if (arg.StartsWith("LIST", StringComparison.Ordinal)) Listing = !negate;
                else if (arg.Equals("SUMMARY", StringComparison.Ordinal)) Summary = !negate;
                else if (arg.Equals("STACK", StringComparison.Ordinal)) Stack = !negate;
                else if (arg.Equals("MINIMIZE", StringComparison.Ordinal)) Minimize = !negate;
                else if (arg.Equals("VERSION", StringComparison.Ordinal)) Version = !negate;
                else if (arg.Equals("PARSEONLY", StringComparison.Ordinal)) ParseOnly = !negate;
                else if (arg.StartsWith("PERSISTBUFF", StringComparison.Ordinal)) Persist = !negate;
                else if (arg.Equals("PARSER", StringComparison.Ordinal)) HasParser = !negate;
                else if (arg.Equals("BABEL", StringComparison.Ordinal)) Babel = !negate;
                else if (arg.Equals("FILES", StringComparison.Ordinal)) Files = !negate;
                else if (arg.StartsWith("EMBEDBUFF", StringComparison.Ordinal)) EmbedBuffers = !negate;
                else if (arg.Equals("INFO", StringComparison.Ordinal)) EmitInfoHeader = !negate;
                else if (arg.Equals("UTF8DEFAULT", StringComparison.Ordinal)) // Deprecated, compatability only.
                {
                    if (negate)
                        CodePage = rawCP;
                    else
                        CodePage = utf8CP;
                }
                else if (arg.Equals("COMPRESSMAP", StringComparison.Ordinal)) {
                    compressMap = !negate;
                    compressMapExplicit = true;
                }
                else if (arg.Equals("COMPRESSNEXT", StringComparison.Ordinal)) {
                    CompressNext = !negate;
                    //compressNextExplicit = true;
                }
                else if (arg.Equals("COMPRESS", StringComparison.Ordinal)) {
                    compressMap = !negate;
                    CompressNext = !negate;
                    compressMapExplicit = true;
                    //compressNextExplicit = true;
                }
                else if (arg.Equals("SQUEEZE", StringComparison.Ordinal)) {
                    // Compress both map and next-state
                    // but do not use two-level compression
                    // ==> trade time for space.
                    Squeeze = !negate;
                    compressMap = !negate;
                    CompressNext = !negate;
                    compressMapExplicit = true;
                    //compressNextExplicit = true;
                }
                else if (arg.Equals("UNICODE", StringComparison.Ordinal)) {
                    // Have to do some checks here. If an attempt is made to
                    // set (no)unicode after the alphabet size has been set
                    // it is a command line or inline option error.
                    int cardinality = (negate ? asciiCardinality : unicodeCardinality);
                    Unicode = !negate;
                    if (targetSymCardinality == notSet || targetSymCardinality == cardinality)
                        targetSymCardinality = cardinality;
                    else
                        return OptionState.alphabetLocked;
                    if (Unicode) {
                        ChrClasses = true;
                        if (!compressMapExplicit)
                            compressMap = true;
                    }
                }
                else if (arg.Equals("VERBOSE", StringComparison.Ordinal)) {
                    Verbose = !negate;
                    if (Verbose) Version = true;
                }
                else if (arg.Equals("CLASSES", StringComparison.Ordinal)) {
                    if (negate && Unicode)
                        return OptionState.inconsistent;
                    ChrClasses = !negate;
                }
                else
                    return OptionState.errors;
            }
            return OptionState.clear;
		}

		internal void ErrorReport()
		{
            try {
                if (ErrorsToConsole)
                    handler.DumpAll( scanner.Buffer, Msg );
                else
                    handler.DumpErrorsInMsbuildFormat( scanner.Buffer, Msg );
            }
            catch (IOException)
            {
                /* ignore exception, can't error-list it anyway */
                Console.Error.WriteLine("Failed to create error report");
            }
        }

        internal void MakeListing()
        {
            // list could be null, if this is an un-requested listing
            // for example after errors have been detected.
            try
            {
                if (listWriter == null)
                    listWriter = ListingFile(baseName + dotLst);
                handler.MakeListing(scanner.Buffer, listWriter, FileName, VerString);
            }
            catch (IOException) 
            { 
                /* ignore exception, can't error-list it anyway */
                Console.Error.WriteLine("Failed to create listing");
            }
        }

        internal static string ElapsedTime(DateTime start)
        {
            TimeSpan span = DateTime.Now - start;
            return String.Format(CultureInfo.InvariantCulture, "{0,4:D} msec", (int)span.TotalMilliseconds);
        }

        /// <summary>
        /// Set up file paths: called after options are processed
        /// </summary>
        /// <param name="path"></param>
        internal void GetNames(string path)
        {
            string xNam = Path.GetExtension(path).ToUpperInvariant();
            string flnm = Path.GetFileName(path);

            // string locn = System.Reflection.Assembly.GetExecutingAssembly().Location;
            // this.exeDirNm = Path.GetDirectoryName(locn);

            this.pathName = path;

            if (xNam.Equals(dotLexUC))
                this.FileName = flnm;
            else if (String.IsNullOrEmpty(xNam))
            {
                this.FileName = flnm + dotLexLC;
                this.pathName = path + dotLexLC;
            }
            else
                this.FileName = flnm;
            this.baseName = Path.GetFileNameWithoutExtension(this.FileName);

            if (this.outName == null) // do the default outfilename
                this.outName = this.baseName + dotCs;

        }

        /// <summary>
        /// This method opens the source file.  The file is not disposed in this file.
        /// The mainline code (program.cs) can call MakeListing and/or ErrorReport, for 
        /// which the buffered stream needs to be open so as to interleave error messages 
        /// with the source.
        /// </summary>
        internal void OpenSource()
        {
            try
            {
                inputFile = new FileStream(this.pathName, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Verbose) Msg.WriteLine("GPLEX: opened input file <{0}>", pathName);
                InputInfo = this.pathName + " - " + File.GetLastWriteTime(this.pathName).ToString();
            }
            catch (IOException)
            {
                inputFile = null;
                handler = new ErrorHandler(); // To stop handler.ErrNum faulting!
                string message = String.Format(CultureInfo.InvariantCulture, 
                    "Source file <{0}> not found{1}", FileName, Environment.NewLine);
                handler.AddError(message, null); // aast.AtStart;
                throw new ArgumentException(message);
            }
        }

        FileStream FrameFile()
        {
            FileStream frameFile = null;
            string path1 = this.userFrame;
            try
            {
                // Try the user-specified path if there is one given.
                frameFile = new FileStream(path1, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Verbose) Msg.WriteLine("GPLEX: opened frame file <{0}>", path1);
                this.FrameName = path1;
                return frameFile;
            }
            catch (IOException)
            {
                // This is a fatal error.
                handler.AddError("GPLEX frame file <" + path1 + "> not found", aast.AtStart);
                return null;
            }
        }
        
        TextReader FrameReader()
        {
            if (this.userFrame != null)
            {
                frameFile = FrameFile();
                return new StreamReader(frameFile);
            }
            else // Use the embedded resource
            {
                string gplexxFrame = QUT.Gplex.IncludeResources.Content.GplexxFrame;
                if (Verbose) Msg.WriteLine("GPLEX: using frame from embedded resource");
                this.FrameName = "embedded resource";
                return new StringReader(gplexxFrame);
            }
        }

        FileStream OutputFile()
        {
            FileStream rslt = null;
            try
            {
                rslt = new FileStream(this.outName, FileMode.Create);
                if (Verbose) Msg.WriteLine("GPLEX: opened output file <{0}>", this.outName);
            }
            catch (IOException)
            {
                handler.AddError("GPLEX: output file <" + this.outName + "> not opened", aast.AtStart);
            }
            return rslt;
        }

        TextWriter OutputWriter()
        {
            TextWriter rslt = null;
            if (this.outName.Equals("-"))
            {
                rslt = Console.Out;
                if (Verbose) Msg.WriteLine("GPLEX: output sent to StdOut");
            }
            else
            {
                outputFile = OutputFile();
                rslt = new StreamWriter(outputFile);
            }
            return rslt;
        }

        StreamWriter ListingFile(string outName)
        {
            try
            {
                listFile = new FileStream(outName, FileMode.Create);
                if (Verbose) Msg.WriteLine("GPLEX: opened listing file <{0}>", outName);
                return new StreamWriter(listFile);
            }
            catch (IOException)
            {
                handler.AddError("GPLEX: listing file <" + outName + "> not created", aast.AtStart);
                return null;
            }
        }

        FileStream BufferCodeFile()
        {
            try
            {
                FileStream codeFile = new FileStream(bufferCodeName, FileMode.Create);
                if (Verbose) Msg.WriteLine("GPLEX: created file <{0}>", bufferCodeName);
                return codeFile;
            }
            catch (IOException)
            {
                handler.AddError("GPLEX: buffer code file <" + bufferCodeName + "> not created", aast.AtStart);
                return null;
            }
        }

        void CopyBufferCode()
        {
            string GplexBuffers = "";
            StreamWriter writer = new StreamWriter(BufferCodeFile());
            GplexBuffers = QUT.Gplex.IncludeResources.Content.GplexBuffers;

            writer.WriteLine(QUT.Gplex.IncludeResources.Content.ResourceHeader);
            writer.WriteLine("using System;");
            writer.WriteLine("using System.IO;");
            writer.WriteLine("using System.Text;");
            writer.WriteLine("using System.Collections.Generic;");
            writer.WriteLine("using System.Diagnostics.CodeAnalysis;");
            writer.WriteLine("using System.Runtime.Serialization;");
            writer.WriteLine("using System.Globalization;");
            writer.WriteLine();
            writer.WriteLine("namespace QUT.GplexBuffers");
            writer.WriteLine('{');
            writer.WriteLine("// Code copied from GPLEX embedded resource");
            writer.WriteLine(GplexBuffers);
            writer.WriteLine("// End of code copied from embedded resource");
            writer.WriteLine('}');
            writer.Flush();
            writer.Close();
        }

        internal static void EmbedBufferCode(TextWriter writer)
        {
            string GplexBuffers = "";
            GplexBuffers = QUT.Gplex.IncludeResources.Content.GplexBuffers;

            writer.WriteLine(QUT.Gplex.IncludeResources.Content.ResourceHeader);

            writer.WriteLine("// Code copied from GPLEX embedded resource");
            writer.WriteLine(GplexBuffers);
            writer.WriteLine("// End of code copied from embedded resource");
            // writer.WriteLine('}');
            writer.Flush();
        }

        internal void ListDivider()
        {
            ListStream.WriteLine(
            "============================================================================="); 
        }

        void Status(DateTime start)
        {
            Msg.Write("GPLEX: input parsed, AST built");
            Msg.Write((Errors ? ", errors detected" : " without error"));
            Msg.Write((Warnings ? "; warnings issued. " : ". "));
            Msg.WriteLine(ElapsedTime(start));
        }

        void ClassStatus(DateTime start, int len)
        {
            Msg.Write("GPLEX: {0} character classes found.", len);
            Msg.WriteLine(ElapsedTime(start));
        }

        void CheckOptions()
        {
            if (Babel && !Unicode)
                handler.ListError(aast.AtStart, 112);
        }

        internal void Process(string fileArg)
		{
            GetNames(fileArg);
            // check for file exists
            OpenSource();
            // parse source file
            if (inputFile != null)
            {
                DateTime start = DateTime.Now;
                try
                {
                    handler = new ErrorHandler();
                    scanner = new QUT.Gplex.Lexer.Scanner(inputFile);
                    parser = new QUT.Gplex.Parser.Parser(scanner);
                    scanner.yyhdlr = handler;
                    parser.Initialize(this, scanner, handler, new OptionParser2(ParseOption));
                    aast = parser.Aast;
                    parser.Parse();
                    // aast.DiagnosticDump();
                    if (Verbose) 
                        Status(start);
                    CheckOptions();
                    if (!Errors && !ParseOnly)
                    {	// build NFSA
                        if (ChrClasses) {
                            DateTime t0 = DateTime.Now;
                            partition = new Partition( TargetSymCardinality, this );
                            partition.FindClasses( aast );
                            partition.FixMap();
                            if (Verbose)
                                ClassStatus( t0, partition.Length );
                        }
                        else
                            CharRange.Init( TargetSymCardinality );
                        nfsa = new NFSA(this);
                        nfsa.Build(aast);
                        if (!Errors)
                        {	// convert to DFSA
                            dfsa = new DFSA(this);
                            dfsa.Convert(nfsa);
                            if (!Errors)
                            {	// minimize automaton
                                if (Minimize)
                                    dfsa.Minimize();
                                if (!Errors && !checkOnly)
                                {   // emit the scanner to output file
                                    TextReader frameRdr = FrameReader();
                                    TextWriter outputWrtr = OutputWriter();
                                    dfsa.EmitScanner(frameRdr, outputWrtr);

                                    if (!EmbedBuffers)
                                        CopyBufferCode();
                                    // Clean up!
                                    if (frameRdr != null) 
                                        frameRdr.Close();
                                    if (outputWrtr != null) 
                                        outputWrtr.Close();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    string str = ex.Message;
                    handler.AddError(str, aast.AtStart);
                    throw;
                }
            }
		}
	}
}
