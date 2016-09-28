﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestRunner
{
    static class Program
    {

        /// <summary>
        /// Program entry point
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Reliability",
            "CA2001:AvoidCallingProblematicMethods",
            MessageId = "System.Reflection.Assembly.LoadFrom",
            Justification = "Need to load assemblies in order to run tests")]
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                //
                // Route trace output to stdout
                //
                Trace.Listeners.Add(new ConsoleTraceListener());

                //
                // Print program banner
                //
                Banner();

                //
                // Parse arguments
                //
                var argumentParser = new ArgumentParser(args);
                if (!argumentParser.Success)
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine(ArgumentParser.Usage);
                    Console.Out.WriteLine();
                    Console.Out.WriteLine();
                    Console.Out.WriteLine(argumentParser.ErrorMessage);
                    return 1;
                }

                //
                // Resolve full path to test assembly
                //
                string fullAssemblyPath = GetFullAssemblyPath(argumentParser.AssemblyPath);
                if (!File.Exists(fullAssemblyPath))
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("Test assembly not found: {0}", fullAssemblyPath);
                    return 1;
                }

                //
                // Load test assembly
                //
                var assembly = Assembly.LoadFrom(fullAssemblyPath);
                Console.Out.WriteLine();
                Console.Out.WriteLine("Test Assembly:");
                Console.Out.WriteLine(assembly.Location);


                //
                // Pull in test assembly .config file if present
                //
                UseConfigFile(assembly);

                //
                // Run tests in assembly
                //
                var success = RunTestAssembly(assembly);
                return success ? 0 : 1;
            }

            //
            // Handle internal errors
            //
            catch (Exception e)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(
                    "An internal error occurred in {0}:",
                    Path.GetFileName(Assembly.GetExecutingAssembly().Location));
                Console.Out.WriteLine(FormatException(e));
                return 1;
            }
        }


        /// <summary>
        /// Print program information
        /// </summary>
        static void Banner()
        {
            var name = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductName;
            var major = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductMajorPart;
            var minor = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductMinorPart;
            var copyright = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).LegalCopyright;
            var description = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).Comments;
            WriteHeading(
                string.Format(CultureInfo.InvariantCulture, "{0} - {1}", name, description),
                string.Format(CultureInfo.InvariantCulture, "Version {0}.{1}", major, minor),
                copyright);
        }


        /// <summary>
        /// Resolve full path to test assembly
        /// </summary>
        static string GetFullAssemblyPath(string path)
        {
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Environment.CurrentDirectory, path);
        }


        /// <summary>
        /// Activate the test assembly's .config file, if one is present
        /// </summary>
        static void UseConfigFile(Assembly assembly)
        {
            string configPath = assembly.Location + ".config";
            if (!File.Exists(configPath)) return;
            AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", configPath);
            Console.Out.WriteLine();
            Console.Out.WriteLine("Configuration File:");
            Console.Out.WriteLine(configPath);
        }


        /// <summary>
        /// Run tests in a test assembly
        /// </summary>
        public static bool RunTestAssembly(Assembly testAssembly)
        {
            if (testAssembly == null) throw new ArgumentNullException("testAssembly");

            var testClasses =
                testAssembly.GetTypes()
                    .Where(t => t.GetCustomAttributes(typeof(TestClassAttribute), false).Any())
                    .OrderBy(t => t.Name);

            int failed = 0;
            foreach (var testClass in testClasses)
            {
                if (!RunTestClass(testClass))
                {
                    failed++;
                }
            }

            return (failed == 0);
        }


        /// <summary>
        /// Run tests in a [TestClass]
        /// </summary>
        /// <returns>
        /// Whether everything in <paramref name="testClass"/> succeeded
        /// </returns>
        static bool RunTestClass(Type testClass)
        {
            if (testClass == null) throw new ArgumentNullException("testClass");

            Console.Out.WriteLine();
            WriteHeading(testClass.FullName);

            bool ignore = testClass.GetCustomAttributes(typeof(IgnoreAttribute), false).Any();

            //
            // Locate methods
            //
            var classInitializeMethod = testClass.GetMethods()
                .FirstOrDefault(m => m.GetCustomAttributes(typeof(ClassInitializeAttribute), false).Any());

            var classCleanupMethod = testClass.GetMethods()
                .FirstOrDefault(m => m.GetCustomAttributes(typeof(ClassCleanupAttribute), false).Any());

            var testInitializeMethod = testClass.GetMethods()
                .FirstOrDefault(m => m.GetCustomAttributes(typeof(TestInitializeAttribute), false).Any());

            var testCleanupMethod = testClass.GetMethods()
                .FirstOrDefault(m => m.GetCustomAttributes(typeof(TestCleanupAttribute), false).Any());

            var testMethods = testClass.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(TestMethodAttribute), false).Any())
                .OrderBy(m => m.Name)
                .ToList();

            //
            // Run [ClassInitialize] method
            //
            var classInitializeSucceeded = RunMethod(classInitializeMethod, null, true, "[ClassInitialize]");

            //
            // Run [TestMethod]s
            //
            int ran = 0;
            int passed = 0;
            int failed = 0;
            int ignored = 0;
            if (classInitializeSucceeded && !ignore)
            {
                foreach (var testMethod in testMethods)
                {
                    switch(RunTest(testMethod, testInitializeMethod, testCleanupMethod))
                    {
                        case TestResult.Passed:
                            passed++;
                            ran++;
                            break;
                        case TestResult.Failed:
                            failed++;
                            ran++;
                            break;
                        case TestResult.Ignored:
                            ignored++;
                            break;
                    }
                }
            }
            else
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("Ignoring all tests because class is decorated with [Ignore]");
                ignored = testMethods.Count;
            }

            //
            // Run [ClassCleanup] method
            //
            var classCleanupSucceeded = RunMethod(classCleanupMethod, null, false, "[ClassCleanup]");

            //
            // Print results
            //
            WriteSubheading("Summary");
            Console.Out.WriteLine();
            Console.Out.WriteLine("ClassInitialize: {0}",
                classInitializeMethod == null ? "Not present" : classInitializeSucceeded ? "Succeeded" : "Failed");
            Console.Out.WriteLine("Total:           {0} tests", testMethods.Count);
            Console.Out.WriteLine("Ran:             {0} tests", ran);
            Console.Out.WriteLine("Ignored:         {0} tests", ignored);
            Console.Out.WriteLine("Passed:          {0} tests", passed);
            Console.Out.WriteLine("Failed:          {0} tests", failed);
            Console.Out.WriteLine("ClassCleanup:    {0}",
                classCleanupMethod == null ? "Not present" : classCleanupSucceeded ? "Succeeded" : "Failed");

            return
                classInitializeSucceeded &&
                failed == 0 &&
                classCleanupSucceeded;
        }


        /// <summary>
        /// Run a test method (plus its intialize and cleanup methods, if present)
        /// </summary>
        /// <remarks>
        /// If the test method is decorated with [Ignore], nothing is run
        /// </remarks>
        /// <returns>
        /// The results of the test
        /// </returns>
        static TestResult RunTest(
            MethodInfo testMethod,
            MethodInfo testInitializeMethod,
            MethodInfo testCleanupMethod)
        {
            WriteSubheading(testMethod.Name.Replace("_", " "));

            bool ignore = testMethod.GetCustomAttributes(typeof(IgnoreAttribute), false).Any();
            if (ignore)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("Ignored because method is decorated with [Ignore]");
                return TestResult.Ignored;
            }

            //
            // Construct an instance of the test class
            //
            var testClass = testMethod.DeclaringType;
            var testInstance = Activator.CreateInstance(testClass);

            if (
                RunMethod(testInitializeMethod, testInstance, false, "[TestInitialize]") &&
                RunMethod(testMethod, testInstance, false, "[TestMethod]") &&
                RunMethod(testCleanupMethod, testInstance, false, "[TestCleanup]"))
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("Passed");
                return TestResult.Passed;
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine("FAILED");
            return TestResult.Failed;
        }


        /// <summary>
        /// Run a test-related method using reflection
        /// </summary>
        /// <returns>
        /// Whether the method ran successfully
        /// </returns>
        static bool RunMethod(MethodInfo method, object instance, bool takesContext, string prefix)
        {
            prefix = prefix ?? "";

            if (method == null) return true;

            Console.Out.WriteLine();
            Console.Out.WriteLine(prefix + (string.IsNullOrEmpty(prefix) ? "" : " ") + method.Name + "()");

            var watch = new Stopwatch();
            watch.Start();
            bool success = false;
            var parameters = takesContext ? new object[] {null} : null;
            try
            {
                method.Invoke(instance, parameters);
                watch.Stop();
                success = true;
            }
            catch (TargetInvocationException tie)
            {
                watch.Stop();
                Console.Out.WriteLine(Indent(FormatException(tie.InnerException)));
            }

            Console.Out.WriteLine("  {0} ({1:N0} ms)", success ? "Succeeded" : "Failed", watch.ElapsedMilliseconds);

            return success;
        }


        static void WriteHeading(params string[] lines)
        {
            WriteHeading('=', lines);
        }


        static void WriteSubheading(params string[] lines)
        {
            WriteHeading('-', lines);
        }


        static void WriteHeading(char ruleCharacter, params string[] lines)
        {
            if (lines == null) return;
            if (lines.Length == 0) return;

            var longestLine = lines.Max(line => line.Length);
            var rule = new string(ruleCharacter, longestLine);

            Console.Out.WriteLine();
            Console.Out.WriteLine(rule);
            foreach (var line in lines)
            {
                Console.Out.WriteLine(line);
            }
            Console.Out.WriteLine(rule);
        }


        static string FormatException(Exception e)
        {
            if (e == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine(e.Message);
            sb.AppendLine("Type: " + e.GetType().FullName);
            if (e.Data != null)
            {
                foreach (var key in e.Data.Keys)
                {
                    sb.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "Data.{0}: {1}",
                        key.ToString(),
                        e.Data[key].ToString()));
                }
            }
            if (!string.IsNullOrWhiteSpace(e.Source))
            {
                sb.AppendLine("Source: " + e.Source);
            }
            if (!string.IsNullOrWhiteSpace(e.HelpLink))
            {
                sb.AppendLine("HelpLink: " + e.HelpLink);
            }
            if (!string.IsNullOrWhiteSpace(e.StackTrace))
            {
                sb.AppendLine("StackTrace:");
                sb.AppendLine(Indent(FormatStackTrace(e.StackTrace)));
            }
            if (e.InnerException != null)
            {
                sb.AppendLine("InnerException:");
                sb.AppendLine(Indent(FormatException(e.InnerException)));
            }
            return sb.ToString();
        }


        static string FormatStackTrace(string stackTrace)
        {
            return string.Join(
                Environment.NewLine,
                SplitLines(stackTrace)
                    .Select(line => line.Trim())
                    .SelectMany(line => {
                        var i = line.IndexOf(" in ", StringComparison.Ordinal);
                        if (i <= 0) return new[] {line};
                        var inPart = line.Substring(i + 1);
                        var atPart = line.Substring(0, i);
                        return new[] {atPart, Indent(inPart)};
                        }));
        }


        static string Indent(string theString)
        {
            if (theString == null) throw new ArgumentNullException("theString");
            var lines = SplitLines(theString);
            var indentedLines = lines.Select(s => "  " + s);
            return string.Join(Environment.NewLine, indentedLines);
        }


        static string[] SplitLines(string theString)
        {
            theString = theString.Replace("\r\n", "\n").Replace("\r", "\n");
            if (theString.EndsWith("\n", StringComparison.Ordinal))
            {
                theString = theString.Substring(0, theString.Length-1);
            }
            return theString.Split('\n');
        }


    }
}
