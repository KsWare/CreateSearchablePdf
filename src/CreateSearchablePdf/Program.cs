using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using static System.Net.WebRequestMethods;
using File = System.IO.File;

namespace KsWare.CreateSearchablePdf;

static class Program {

	private static bool createTextFile = false;
	private static bool overwriteOriginal = true;
	private static bool createBackup = true;
	private static bool restoreDate = true;
	private static bool continueNextFileAfterError = true;
	private static bool skipSearchable = true;

	private static string originalWorkDir;
	private static string tempWorkDir;
	private static bool nonInteractive;
	private static List<string> files = [];
	private static List<string> folders = [];
	private static int countSkippedAlreadyReadable;
	private static int countErrors;
	private static int countConverted;
	private static bool showSummary = true;

	static void Main(string[] args) {
		for (var i = 0; i < args.Length; i++) {
			switch (args[i].ToLowerInvariant()) {
				case "--non-interactive": nonInteractive = true; break;
				default:
					if (File.Exists(args[i])) files.Add(args[i]);
					else if (Directory.Exists(args[i])) folders.Add(args[i]);
					else Exit(1, $"Unknown parameter #{i + 1}: {args[i]}");
					break;
			}
		}

		if (files.Count == 0) {
			Console.Error.WriteLine("Missing input file.");
			ShowHelp();
			Environment.Exit(1);
		}

		string[] requiredTools = ["pdftoppm", "tesseract", "pdftk", "gswin64c", "pdftotext"];
		foreach (var tool in requiredTools) {
			if (IsToolInstalled(tool)) continue;
			Console.Error.WriteLine($"{tool} is not installed or not in PATH.");
			Environment.Exit(1);
		}

		try {
			originalWorkDir = Directory.GetCurrentDirectory();
			tempWorkDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempWorkDir);
			Environment.CurrentDirectory = tempWorkDir;
			Console.WriteLine($"TempWorkingDir: {tempWorkDir}");

			foreach (var filename in args) {
				try {
					ProcessPdf(filename);
				}
				catch {
					countErrors++;
					if (continueNextFileAfterError == false) Exit(1);
					Pause();
				}
			}
			Directory.GetFiles(Environment.CurrentDirectory).ToList().ForEach(File.Delete);
			Console.WriteLine("Done.");
		}
		finally {
			Exit(0);
		}
	}

	private static void Pause() {
		if (nonInteractive) return; 
		Console.WriteLine($"Press any key to continue...");
		Console.ReadKey(true);
	}

	private static void ShowHelp() {
		//>|         |         |         |         |         |         |         |         |
		var s = $"""
			
			Usage:
			  CreateSearchablePdf [options] [<pdf-file> ...]
			
			Options:
			  -t   create text file                  {createTextFile}
			  -o   overwrite original                {overwriteOriginal}
			  -b   create backup                     {createBackup}
			  -r   restore date                      {restoreDate}
			  -c   continue next file after error    {continueNextFileAfterError}
			  -ss  skip file if already searchable   {skipSearchable}
			  --non-interactive
			""";
		//>|         |         |         |         |         |         |         |         |
		Console.Write(s);
	}

	private static void Exit(int exitCode = 0, string? message=null) {
		if(!string.IsNullOrEmpty(message) && exitCode!=0) Console.Error.WriteLine($"ERROR: {message}");
		if(!string.IsNullOrEmpty(message) && exitCode==0) Console.WriteLine(message);

		if (showSummary) {
			Console.WriteLine($"""
			                   Summary:
			                     Files:     {files.Count}
			                     Converted: {countConverted}
			                     Skipped:   {countSkippedAlreadyReadable}
			                     Errors:    {countErrors}
			                   """);
		}

		if (!string.IsNullOrEmpty(originalWorkDir)) Environment.CurrentDirectory = originalWorkDir;
		// if (exitCode != 0) Pause();
		Pause();
		Environment.Exit(exitCode);
	}

	private static bool IsToolInstalled(string tool) {
		var process = new Process {
			StartInfo = new ProcessStartInfo {
				FileName = "where",
				Arguments = tool,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		process.Start();
		process.WaitForExit();
		return process.ExitCode == 0;
	}

	static void ProcessPdf(string inputPdf) {
		inputPdf = Path.IsPathRooted(inputPdf) ? inputPdf : Path.Combine(originalWorkDir, inputPdf);
		Console.WriteLine($"\nprocessing \"{inputPdf}\"...");

		var inputPath = Path.GetDirectoryName(inputPdf);
		var basename = Path.GetFileNameWithoutExtension(inputPdf);
		var outputPdf = Path.Combine(inputPath, $"{basename}_searchable.pdf");

		var createDate = File.GetCreationTime(inputPdf);
		var changeData = File.GetLastWriteTime(inputPdf);

		if (IsSearchable(inputPdf)) {
			Console.WriteLine("INFO: PDF is already searchable. processing skipped...");
			countSkippedAlreadyReadable++;
			return;
		}

		if (overwriteOriginal) {
			var f = Path.Combine(inputPath, $"{basename}_original.pdf");
			// if (File.Exists(f)) throw new ApplicationException("Backup file already exists.");
			if (File.Exists(f)) {
				Console.WriteLine("ERROR: Backup file already exists. processing skipped...");
				countErrors++;
				return;
			}
		}
		else {
			// if (File.Exists(outputPdf)) throw new ApplicationException("Output file file already exists.");
			if (File.Exists(outputPdf)) {
				Console.WriteLine("ERROR: Output file already exists. processing skipped...");
				countErrors++;
				return;
			}
		}

		ClearTempWorkingDir();

		RunProcess("pdftoppm", $"-jpeg \"{inputPdf}\" page");

		var images = Directory.GetFiles(Environment.CurrentDirectory, "page-*.jpg");
		var page = 1;
		var outputPdfs = string.Empty;

		foreach (var image in images.Select(img=>Path.GetFileName(img))) {
			var pageOutputPdf = $"page-{page}.pdf";
			RunProcess("tesseract", $"{image} page-{page} -l deu pdf");
			outputPdfs += $" {pageOutputPdf}";
			page++;
		}

		RunProcess("pdftk", $"{outputPdfs} cat output temp.pdf");
		RunProcess("gswin64c", "-o no-bg.pdf -sDEVICE=pdfwrite -dFILTERIMAGE -dNOPAUSE -dBATCH -dQUIET temp.pdf");
		RunProcess("pdftk", $"\"{inputPdf}\" stamp no-bg.pdf output output.pdf");
		// RunProcess("pdftk", $"output.pdf update_info_utf8 metadata.txt output \"{outputPdf}\"");
		RunProcess("pdftk", $"output.pdf dump_data_utf8 output output.metadata.txt");

		if (overwriteOriginal) {
			File.Move(inputPdf, Path.Combine(inputPath, $"{basename}_original.pdf"));
			File.Move("output.pdf", inputPdf);
			if (restoreDate) {
				File.SetCreationTime(inputPdf, createDate);
				File.SetLastWriteTime(inputPdf, changeData);
			}
			Console.WriteLine($"Searchable PDF created: \"{inputPdf}\"");
		}
		else {
			File.Move("output.pdf", outputPdf);
			if (restoreDate) {
				File.SetCreationTime(outputPdf, createDate);
				File.SetLastWriteTime(outputPdf, createDate);
			}

			Console.WriteLine($"Searchable PDF created: \"{outputPdf}\"");
		}

		if (createTextFile) {
			RunProcess("pdftotext", $"{outputPdf} {Path.Combine(inputPath, $"{basename}_searchable.txt")}");
		}
		countConverted++;
	}

	private static void ClearTempWorkingDir() {
		Directory.GetFiles(Environment.CurrentDirectory).ToList().ForEach(File.Delete);
	}

	static bool IsSearchable(string pdfFile) {
		var process = new Process {
			StartInfo = new ProcessStartInfo {
				FileName = "pdftotext",
				Arguments = $"\"{pdfFile}\" -",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		process.Start();
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return !string.IsNullOrWhiteSpace(output);
	}

	static void RunProcess(string fileName, string arguments) {
		Console.Out.WriteLine($"{fileName} {arguments}");
		var processStartInfo = new ProcessStartInfo {
			FileName = fileName,
			Arguments = arguments,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Environment.CurrentDirectory
		};
		var output = new StringBuilder();
		using var process = new Process();
		process.StartInfo = processStartInfo;
		process.EnableRaisingEvents = true;

		void OnOutputDataReceived(string? data) {
			if (string.IsNullOrEmpty(data)) return;
			Console.Out.WriteLine(data);
			output.AppendLine(data);
		}

		process.OutputDataReceived += (s, e) => OnOutputDataReceived(e.Data);
		process.ErrorDataReceived += (s, e) => OnOutputDataReceived(e.Data);

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new ApplicationException(
				$"Process exist with code: {process.ExitCode}" + "\n" +
				$"Commandline: {fileName} {arguments}" + "\n" +
				$"Output:\n{output}");
	}

}

/* Use-cases/options:

	- single-file:
		- pause (options)
	
	- multiple files:
		- show summary before exit
        - pause (options)

    - pause after each file
		(a) never		
        (b) on error
        (c) always

	- pause before exit
		(a) never		
		(b) if summary is shown
		(c) on error
		(a) always
 */