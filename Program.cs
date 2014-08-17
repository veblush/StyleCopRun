using System;
using System.Collections.Generic;
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

        [OptionList('i', "include", HelpText = "Include path pattern (regexp)", MutuallyExclusiveSet = "filter")]
        public IList<string> Includes { get; set; }

        [OptionList('e', "exclude", HelpText = "Exclude path pattern (regexp)", MutuallyExclusiveSet = "filter")]
        public IList<string> Excludes { get; set; }

        [Option('r', "recursive", HelpText = "Recurse subdirectories")]
        public bool Recursive { get; set; }

        [Option('s', "settings", HelpText = "Settings file for StyleCop")]
        public string Settings { get; set; }

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
        private static bool _violationFound;

        public static int Main(string[] args)
        {
            // Parse options

            _options = new Options();
            if (Parser.Default.ParseArguments(args, _options) == false)
            {
                return 1;
            }

            if (_options.Inputs == null || _options.Inputs.Count == 0)
            {
                Console.Write(_options.GetUsage());
                return 1;
            }

            var settingsPath = _options.Settings;
            if (string.IsNullOrEmpty(settingsPath))
            {
                var projectPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (projectPath != null)
                {
                    settingsPath = Path.Combine(projectPath, "Settings.StyleCop");
                    if (File.Exists(settingsPath))
                    {
                        if (_options.Verbose)
                            Console.WriteLine("Use alternative settings: {0}", settingsPath);
                    }
                }
            }
            else
            {
                Console.WriteLine("Use settings: {0}", settingsPath);
            }

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
                    catch (Exception e)
                    {
                        Console.WriteLine("Path not found: {0}", input);
                        return 1;
                    }
                }
            }

            if (_options.Includes != null && _options.Includes.Count > 0)
            {
                files = files.Where(f => _options.Includes.Any(i => Regex.Match(f, i, RegexOptions.IgnoreCase).Success)).ToList();
            }
            else if (_options.Excludes != null && _options.Excludes.Count > 0)
            {
                files = files.Where(f => _options.Excludes.All(i => Regex.Match(f, i, RegexOptions.IgnoreCase).Success == false)).ToList();
            }

            // Run StyleCop

            var styleCop = new StyleCopConsole(settingsPath, false, null, null, true);

            var rootPath = GetRootPath(files);
            var project = new CodeProject(0, rootPath, new Configuration(null));
            foreach (var file in files)
                styleCop.Core.Environment.AddSourceCode(project, file, null);

            styleCop.OutputGenerated += OnOutputGenerated;
            styleCop.ViolationEncountered += OnViolationEncountered;
            styleCop.Start(new[] { project }, true);

            return _violationFound ? 2 : 0;
        }

        private static void OnOutputGenerated(object sender, OutputEventArgs e)
        {
            if (_options.Verbose == false && e.Importance <= MessageImportance.Low)
                return;

            Console.WriteLine(e.Output);
        }

        private static void OnViolationEncountered(object sender, ViolationEventArgs e)
        {
            Console.WriteLine("{0}({1}): {2} {3}", 
                e.SourceCode.Path, e.LineNumber, e.Violation.Rule.CheckId, e.Message);

            _violationFound = true;
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
    }
}
