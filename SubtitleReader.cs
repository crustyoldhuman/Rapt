using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SSVidPlayer
{
	public static partial class SubtitleExtractor
	{
		public static async Task<string?> ExtractSrtAsync(string mediaFilePath)
		{
			// Extract embedded subtitle track (SubRip / ASS / Mov_Text) to raw SRT string in memory
			string? ffmpegExe = ResolveFfmpegPath();
			if (ffmpegExe == null)
			{
				Debug.WriteLine("[FFMPEG] ERROR: Executable not found in Assets, BaseDirectory, or system PATH.");
				return null;
			}
			Debug.WriteLine($"[FFMPEG] Initializing tool: {ffmpegExe}");

			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = ffmpegExe,
					// Temporarily change loglevel to 'info' to expose stream mapping errors in Stderr
					Arguments = $"-v info -i \"{mediaFilePath}\" -vn -an -c:s srt -f srt -",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8
				};

				Debug.WriteLine($"[FFMPEG] Executing Args: {psi.Arguments}");

				using var proc = Process.Start(psi);
				if (proc == null)
				{
					Debug.WriteLine("[FFMPEG] FATAL: Process.Start() returned null.");
					return null;
				}

				var stdOutTask = proc.StandardOutput.ReadToEndAsync();
				var stdErrTask = proc.StandardError.ReadToEndAsync();

				await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync());

				string srtOutput = stdOutTask.Result;
				string errOutput = stdErrTask.Result;

				Debug.WriteLine($"[FFMPEG] Process exited with code: {proc.ExitCode}");

				if (!string.IsNullOrWhiteSpace(errOutput))
				{
					Debug.WriteLine($"[FFMPEG] Standard Error Output:\n{errOutput}");
				}

				if (!string.IsNullOrWhiteSpace(srtOutput))
				{
					Debug.WriteLine($"[FFMPEG] Extraction successful. Byte length: {srtOutput.Length}");
					return StripAssTags(srtOutput);
				}
				else
				{
					Debug.WriteLine("[FFMPEG] Standard Output was entirely empty. No subtitles extracted.");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[FFMPEG] CRITICAL EXCEPTION: {ex.Message}");
			}

			return null;
		}

		private static string? ResolveFfmpegPath()
		{
			string[] candidates =
			{
				Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg.exe"),
				Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe")
			};

			foreach (var path in candidates)
			{
				if (File.Exists(path)) return path;
			}

			// Check system PATH
			string? pathEnv = Environment.GetEnvironmentVariable("PATH");
			if (!string.IsNullOrEmpty(pathEnv))
			{
				foreach (var dir in pathEnv.Split(Path.PathSeparator))
				{
					try
					{
						string fullPath = Path.Combine(dir.Trim(), "ffmpeg.exe");
						if (File.Exists(fullPath)) return fullPath;
					}
					catch { }
				}
			}

			return null;
		}

		[GeneratedRegex(@"\{\\[^}]*\}")]
		private static partial Regex AssTagsRegex();

		[GeneratedRegex(@"<\/?font[^>]*>", RegexOptions.IgnoreCase)]
		private static partial Regex FontTagsRegex();

		// Clean any leftover ASS/SSA override tags like {\an8} and FFmpeg-generated <font> tags
		private static string StripAssTags(string input)
		{
			string cleanedAss = AssTagsRegex().Replace(input, string.Empty);
			return FontTagsRegex().Replace(cleanedAss, string.Empty);
		}
	}
}