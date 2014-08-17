using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using Microsoft.Build.Framework;
using StyleCop;

namespace StyleCopRun
{
    internal class Options
    {
        [ValueList(typeof(List<string>))]
        public IList<string> Inputs { get; set; }

        [OptionList('i', "include", HelpText = "Include path pattern (regexp)")]
        public IList<string> Includes { get; set; }

        [OptionList('e', "exclude", HelpText = "Exclude path pattern (regexp)")]
        public IList<string> Excludes { get; set; }

        [Option('r', "recursive", HelpText = "Recurse subdirectories")]
        public bool Recursive { get; set; }

        [Option('s', "settings", HelpText = "Settings file for StyleCop")]
        public string Settings { get; set; }

        [Option("svnlook", HelpText = "Path for svnlook")]
        public string SvnLook { get; set; }

        [Option("revision", HelpText = "Revision for svnlook")]
        public string Revision { get; set; }

        [Option("transaction", HelpText = "Transaction for svnlook")]
        public string Transaction { get; set; }

        [Option("temp", HelpText = "Temporary path for subversion job")]
        public string TempDir { get; set; }

        [Option('v', "verbose", HelpText = "Prints all messages to standard output.")]
        public bool Verbose { get; set; }

        [HelpOption]
        public string GetUsage()
        {
            return HelpText.AutoBuild(this,
                (current) => HelpText.DefaultParsingErrorsHandler(this, current));
        }
    }

    class Program
    {
        private static Options _options;

        public static int Main(string[] args)
        {
            if (ParseOption(args) == false)
                return 1;

            if (string.IsNullOrEmpty(_options.Revision) && string.IsNullOrEmpty(_options.Transaction))
            {
                return AnalyzeLocal();
            }
            else
            {
                return AnalyzeSubversion();
            }
        }

        private static bool ParseOption(string[] args)
        {
            _options = new Options();
            if (Parser.Default.ParseArguments(args, _options) == false)
            {
                return false;
            }

            if (_options.Inputs == null || _options.Inputs.Count == 0)
            {
                Console.Write(_options.GetUsage());
                return false;
            }

            if (string.IsNullOrEmpty(_options.Settings))
            {
                var projectPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (projectPath != null)
                {
                    var settingsPath = Path.Combine(projectPath, "Settings.StyleCop");
                    if (File.Exists(settingsPath))
                    {
                        if (_options.Verbose)
                            Console.WriteLine("Use default settings: {0}", settingsPath);

                        _options.Settings = settingsPath;
                    }
                }
            }
            else
            {
                if (_options.Verbose)
                    Console.WriteLine("Use settings: {0}", _options.Settings);
            }

            if (string.IsNullOrEmpty(_options.SvnLook))
            {
                var checkPaths = new[]
                {
                    @"C:\Program Files\Subversion\bin\svnlook.exe",
                    @"C:\Program Files (x86)\Subversion\bin\svnlook.exe",
                    @"C:\Program Files\TortoiseSVN\bin\svnlook.exe",
                    @"C:\Program Files (x86)\TortoiseSVN\bin\svnlook.exe",
                    @"C:\Program Files\VisualSVN Server\bin\svnlook.exe",
                    @"C:\Program Files (x86)\VisualSVN Server\bin\svnlook.exe",
                };
                foreach (var checkPath in checkPaths)
                {
                    if (File.Exists(checkPath))
                    {
                        if (_options.Verbose)
                            Console.WriteLine("Use default svnlook: {0}", checkPath);

                        _options.SvnLook = checkPath;
                    }
                }
            }
            else
            {
                if (_options.Verbose)
                    Console.WriteLine("Use svnlook: {0}", _options.SvnLook);
            }

            if (string.IsNullOrEmpty(_options.TempDir))
            {
                _options.TempDir = Path.GetTempPath();
                if (_options.Verbose)
                    Console.WriteLine("Use default temp dir: {0}", _options.TempDir);
            }
            else
            {
                _options.TempDir = Path.GetFullPath(_options.TempDir);
                if (_options.Verbose)
                    Console.WriteLine("Use temp dir: {0}", _options.TempDir);
            }

            return true;
        }

        private static int AnalyzeLocal()
        {
            // Collect file list

            var files = new List<string>();
            var searchOption = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var input in _options.Inputs)
            {
                var isPattern = input.Contains("*") || input.Contains("?");
                if (isPattern)
                {
                    var pathPart = Path.GetDirectoryName(input);
                    var filePart = Path.GetFileName(input);
                    files.AddRange(Directory.GetFiles(string.IsNullOrEmpty(pathPart) ? "." : pathPart, filePart, searchOption).Select(Path.GetFullPath));
                }
                else
                {
                    try
                    {
                        var fileAttr = File.GetAttributes(input);
                        var isDirectory = (fileAttr & FileAttributes.Directory) == FileAttributes.Directory;
                        if (isDirectory)
                        {
                            files.AddRange(Directory.GetFiles(input, "*", searchOption).Select(Path.GetFullPath));
                        }
                        else
                        {
                            files.Add(Path.GetFullPath(input));
                        }
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Path not found: {0}", input);
                        return 1;
                    }
                }
            }

            files = files.Where(CheckFileFilter).ToList();

            // Run StyleCop

            var styleCop = new StyleCopConsole(_options.Settings, false, null, null, true);

            var rootPath = GetRootPath(files);
            var project = new CodeProject(0, rootPath, new Configuration(null));
            foreach (var file in files)
                styleCop.Core.Environment.AddSourceCode(project, file, null);

            styleCop.OutputGenerated += OnOutputGenerated;

            var violationCount = 0;
            styleCop.ViolationEncountered += (sender, e) =>
            {
                Console.WriteLine("{0}({1}): {2} {3}",
                    e.SourceCode.Path, e.LineNumber, e.Violation.Rule.CheckId, e.Message);

                violationCount += 1;
            };
            styleCop.Start(new[] { project }, true);

            Console.WriteLine("");
            Console.WriteLine("{0} Violations found", violationCount);

            return violationCount > 0 ? 2 : 0;
        }

        private static int AnalyzeSubversion()
        {
            var fileMap = SaveCommittedFilesToTemp();

            // Run StyleCop

            var styleCop = new StyleCopConsole(_options.Settings, false, null, null, true);

            var rootPath = GetRootPath(fileMap.Keys.ToList());
            var project = new CodeProject(0, rootPath, new Configuration(null));
            foreach (var file in fileMap.Keys.ToList())
                styleCop.Core.Environment.AddSourceCode(project, file, null);

            styleCop.OutputGenerated += OnOutputGenerated;

            var violationCount = 0;
            styleCop.ViolationEncountered += (sender, e) =>
            {
                Console.WriteLine("{0}({1}): {2} {3}",
                    fileMap[e.SourceCode.Path], e.LineNumber, e.Violation.Rule.CheckId, e.Message);

                violationCount += 1;
            };

            styleCop.Start(new[] { project }, true);

            Console.WriteLine("");
            Console.WriteLine("{0} Violations found", violationCount);

            return violationCount > 0 ? 2 : 0;
        }

        private static void OnOutputGenerated(object sender, OutputEventArgs e)
        {
            if (_options.Verbose == false && e.Importance <= MessageImportance.Low)
                return;

            Console.WriteLine(e.Output);
        }

        private static bool CheckFileFilter(string path)
        {
            if (_options.Includes != null && _options.Includes.Count > 0)
            {
                if (_options.Includes.All(i => Regex.Match(path, i, RegexOptions.IgnoreCase).Success == false))
                    return false;
            }
            
            if (_options.Excludes != null && _options.Excludes.Count > 0)
            {
                if (_options.Excludes.Any(i => Regex.Match(path, i, RegexOptions.IgnoreCase).Success))
                    return true;
            }
            
            return true;
        }

        private static string GetRootPath(IList<string> filePaths)
        {
            if (filePaths.Count > 0)
            {
                var testAgainst = filePaths[0].Split('/', '\\');
                var noOfLevels = testAgainst.Length;
                foreach (var filePath in filePaths)
                {
                    var current = filePath.Split('/', '\\');
                    int level;
                    for (level = 0; level <= Math.Min(noOfLevels, current.Length) - 1; level++)
                    {
                        if (testAgainst[level] != current[level])
                            break;
                    }
                    noOfLevels = Math.Min(noOfLevels, level);
                }
                return (testAgainst.Take(noOfLevels).Aggregate((m, n) => m + '\\' + n));
            }
            return string.Empty;
        }

        private static Dictionary<string, string> SaveCommittedFilesToTemp()
        {
            var svnLookPsi = new ProcessStartInfo(_options.SvnLook);
            svnLookPsi.UseShellExecute = svnLookPsi.ErrorDialog = false;
            svnLookPsi.RedirectStandardOutput = true;
            
            var svnLookProcess = new Process();
            svnLookProcess.StartInfo = svnLookPsi;

            var revOrTran = !string.IsNullOrEmpty(_options.Revision) ? string.Format("-r {0}", _options.Revision) : string.Format("-t {0}", _options.Transaction);
            svnLookPsi.Arguments = string.Format("changed \"{0}\" {1}", _options.Inputs[0], revOrTran);

            svnLookProcess.Start();

            var result = svnLookProcess.StandardOutput.ReadToEnd();
            var committedFiles = result.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var fileNameExtractor = new Regex("^[AU] *(.*)");
            
            var tempFolder = _options.TempDir;
            if (Directory.Exists(tempFolder) == false)
                Directory.CreateDirectory(tempFolder);

            var teapFileIndex = 0;
            var tempFileMap = new Dictionary<string, string>();
            foreach (string committedRow in committedFiles)
            {
                var committedFile = fileNameExtractor.Match(committedRow).Groups[1].Value;
                if (committedFile.EndsWith("\\"))
                    continue;

                if (CheckFileFilter(committedFile) == false)
                    continue;

                svnLookPsi.Arguments = string.Format("cat \"{0}\" \"{1}\" {2}", _options.Inputs[0], committedFile, revOrTran);
                svnLookProcess.Start();

                var fileName = Path.GetFileName(committedFile);
                var tempFileName = Path.Combine(tempFolder, string.Format("__{0}_{1}", teapFileIndex, fileName));
                tempFileMap[tempFileName] = committedFile;

                var outFile = new StreamWriter(tempFileName, false, Encoding.UTF8);
                outFile.Write(svnLookProcess.StandardOutput.ReadToEnd());
                outFile.Close();

                teapFileIndex += 1;
            }

            return tempFileMap;
        }
    }
}
