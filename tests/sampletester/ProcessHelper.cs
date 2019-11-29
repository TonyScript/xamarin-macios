using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

using NUnit.Framework;

using Xamarin;
using Xamarin.Tests;
using Xamarin.Utils;

public static class ProcessHelper
{
	static int counter;

	static string log_directory;
	static string LogDirectory {
		get {
			if (log_directory == null)
				log_directory = Cache.CreateTemporaryDirectory ("execution-logs");
			return log_directory;
		}

	}

	public static void AssertRunProcess (string filename, string[] arguments, TimeSpan timeout, string workingDirectory, Dictionary<string, string> environment_variables, string message)
	{
		var exitCode = 0;
		var output = new List<string> ();

		Action<string> output_callback = (v) => {
			lock (output)
				output.Add ($"{DateTime.Now.ToString ("HH:mm:ss.fffffff")}: {v}");
		};

		if (environment_variables == null)
			environment_variables = new Dictionary<string, string> ();
		environment_variables ["XCODE_DEVELOPER_DIR_PATH"] = null;
		environment_variables ["DEVELOPER_DIR"] = Configuration.XcodeLocation;

		var watch = Stopwatch.StartNew ();
		exitCode = ExecutionHelper.Execute (filename, arguments, out var timed_out, workingDirectory, environment_variables, output_callback, output_callback, timeout);
		watch.Stop ();

		output_callback ($"Exit code: {exitCode} Timed out: {timed_out} Total duration: {watch.Elapsed.ToString ()}");

		// Write execution log to disk (and print the path)
		var logfile = Path.Combine (LogDirectory, $"{filename}-{Interlocked.Increment (ref counter)}.log");
		File.WriteAllLines (logfile, output);
		TestContext.AddTestAttachment (logfile, $"Execution log for {filename}");
		Console.WriteLine ("Execution log for {0}: {1}", filename, logfile);

		var errors = new List<string> ();
		var errorMessage = "";
		if ((!timed_out || exitCode != 0) && output.Count > 0) {
			var regex = new Regex (@"error\s*(MSB....)?(CS....)?(MT....)?(MM....)?:", RegexOptions.IgnoreCase | RegexOptions.Singleline);
			foreach (var line in output) {
				if (regex.IsMatch (line) && !errors.Contains (line))
					errors.Add (line);
			}
			if (errors.Count > 0)
				errorMessage = "\n\t[Summary of errors from the build output below]\n\t" + string.Join ("\n\t", errors);
		}
		Assert.IsFalse (timed_out, $"{message} timed out after {timeout.TotalMinutes} minutes{errorMessage}");
		Assert.AreEqual (0, exitCode, $"{message} failed (unexpected exit code){errorMessage}");
	}

	public static void BuildSolution (string solution, string platform, string configuration, Dictionary<string, string> environment_variables, string target = "")
	{
		// nuget restore
		var solution_dir = string.Empty;
		var solutions = new string [] { solution };
		var nuget_args = new List<string> ();
		nuget_args.Add ("restore");
		nuget_args.Add ("sln"); // replaced later
		nuget_args.Add ("-Verbosity");
		nuget_args.Add ("detailed");
		var slndir = Path.GetDirectoryName (solution);
		if (!solution.EndsWith (".sln", StringComparison.Ordinal)) {
			while ((solutions = Directory.GetFiles (slndir, "*.sln", SearchOption.TopDirectoryOnly)).Length == 0 && slndir.Length > 1)
				slndir = Path.GetDirectoryName (slndir);
			nuget_args.Add ("-SolutionDir");
			nuget_args.Add (slndir);
		}

		foreach (var sln in solutions) {
			nuget_args [1] = sln; // replacing here
			AssertRunProcess ("nuget", nuget_args.ToArray (), TimeSpan.FromMinutes (2), Configuration.SampleRootDirectory, environment_variables, "nuget restore");
		}

		// msbuild
		var sb = new List<string> ();
		sb.Add ("/verbosity:diag");
		if (!string.IsNullOrEmpty (platform))
			sb.Add ($"/p:Platform={platform}");
		if (!string.IsNullOrEmpty (configuration))
			sb.Add ($"/p:Configuration={configuration}");
		sb.Add (solution);
		if (!string.IsNullOrEmpty (target))
			sb.Add ($"/t:{target}");

		var watch = Stopwatch.StartNew ();
		var failed = false;
		try {
			AssertRunProcess ("msbuild", sb.ToArray (), TimeSpan.FromMinutes (5), Configuration.SampleRootDirectory, environment_variables, "build");
		} catch {
			failed = true;
			throw;
		} finally {
			watch.Stop ();
		}

		// Write performance data to disk
		var subdirs = Directory.GetDirectories (slndir, "*", SearchOption.AllDirectories);
		var apps = subdirs.Where ((v) => {
			var names = v.Split (Path.DirectorySeparatorChar);
			if (names.Length < 2)
				return false;
			var bin_idx = Array.IndexOf (names, "bin");
			var conf_idx = Array.IndexOf (names, configuration);
			if (bin_idx < 0 || conf_idx < 0)
				return false;
			if (bin_idx > conf_idx)
				return false;
			if (platform.Length > 0) {
				var platform_idx = Array.IndexOf (names, platform);
				if (platform_idx < 0)
					return false;
				if (bin_idx > platform_idx)
					return false;
			}
			var app_idx = Array.FindIndex (names, (v2) => v2.EndsWith (".app", StringComparison.Ordinal));
			if (!names [names.Length - 1].EndsWith (".app", StringComparison.Ordinal))
				return false;
			return true;
		}).ToArray ();

		if (apps.Length > 1) {
			apps = apps.Where ((v) => {
				// If one .app is a subdirectory of another .app, we don't care about the former.
				if (apps.Any ((v2) => v2.Length < v.Length && v.StartsWith (v2, StringComparison.Ordinal)))
					return false;

				// If one .app is contained within another .app, we don't care about the former.
				var vname = Path.GetFileName (v);
				var otherApps = apps.Where ((v2) => v != v2);
				if (otherApps.Any ((v2) => {
					var otherSubdirs = subdirs.Where ((v3) => v3.StartsWith (v2, StringComparison.Ordinal));
					return otherSubdirs.Any ((v3) => Path.GetFileName (v3) == vname);
				}))
					return false;

				return true;
			}).ToArray ();
		}

		if (apps.Length > 1) {
			Assert.Fail ($"More than one app directory????\n\t{string.Join ("\n\t", apps)}");
		} else if (apps.Length == 0) {
			Assert.Fail ($"No app directory????\n\t{string.Join ("\n\t", subdirs)}");
		} else {
			var logfile = Path.Combine (LogDirectory, $"{Path.GetFileNameWithoutExtension (solution)}-perfdata-{Interlocked.Increment (ref counter)}.xml");

			var xmlSettings = new XmlWriterSettings {
				Indent = true,
			};
			var xml = XmlWriter.Create (logfile, xmlSettings);
			xml.WriteStartDocument (true);
			xml.WriteStartElement ("perf-data");

			foreach (var app in apps) {
				xml.WriteStartElement ("test");
				xml.WriteAttributeString ("name", TestContext.CurrentContext.Test.FullName);
				xml.WriteAttributeString ("result", failed ? "failed" : "success");
				if (platform.Length > 0)
					xml.WriteAttributeString ("platform", platform);
				xml.WriteAttributeString ("configuration", configuration);
				if (!failed) {
					xml.WriteAttributeString ("duration", watch.ElapsedTicks.ToString ());
					xml.WriteAttributeString ("duration-formatted", watch.Elapsed.ToString ());

					var files = Directory.GetFiles (app, "*", SearchOption.AllDirectories).OrderBy ((v) => v).ToArray ();
					var lengths = files.Select ((v) => new FileInfo (v).Length).ToArray ();
					var total_size = lengths.Sum ();

					xml.WriteAttributeString ("total-size", total_size.ToString ());
					var appstart = Path.GetDirectoryName (app).Length;
					for (var i = 0; i < files.Length; i++) {
						xml.WriteStartElement ("file");
						xml.WriteAttributeString ("name", files [i].Substring (appstart + 1));
						xml.WriteAttributeString ("size", lengths [i].ToString ());
						xml.WriteEndElement ();
					}
				}

				xml.WriteEndElement ();
			}

			xml.WriteEndElement ();
			xml.WriteEndDocument ();
			xml.Dispose ();

			TestContext.AddTestAttachment (logfile, $"Performance data");
			Console.WriteLine ("Performance data: {0}:\n\t{1}", logfile, string.Join ("\n\t", File.ReadAllLines (logfile)));
		}
	}
}
