using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rapt
{
	public static partial class SubtitleExtractor
	{
		public static async Task<List<KeyValuePair<int, string>>> ExtractAllSrtAsync(string mediaFilePath, CancellationToken cancellationToken = default)
		{
			var results = new List<KeyValuePair<int, string>>();
			// Extract embedded subtitle track (SubRip / ASS / Mov_Text) to raw SRT string in memory
			string? ffmpegExe = ResolveFfmpegPath();
			if (ffmpegExe == null)
			{
				Debug.WriteLine("[FFMPEG] ERROR: Executable not found in Assets, BaseDirectory, or system PATH.");
				return results;
			}
			Debug.WriteLine($"[FFMPEG] Initializing tool: {ffmpegExe}");

			for (int trackIndex = 0; trackIndex < 4; trackIndex++)
			{
				try
				{
					var psi = new ProcessStartInfo
					{
						FileName = ffmpegExe,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true,
						StandardOutputEncoding = Encoding.UTF8,
						StandardErrorEncoding = Encoding.UTF8
					};
					psi.ArgumentList.Add("-v");
					psi.ArgumentList.Add("error");
					psi.ArgumentList.Add("-nostdin");
					psi.ArgumentList.Add("-y");
					psi.ArgumentList.Add("-i");
					psi.ArgumentList.Add(mediaFilePath);
					psi.ArgumentList.Add("-vn");
					psi.ArgumentList.Add("-an");
					psi.ArgumentList.Add("-map");
					psi.ArgumentList.Add($"0:s:{trackIndex}");
					psi.ArgumentList.Add("-c:s");
					psi.ArgumentList.Add("webvtt");
					psi.ArgumentList.Add("-f");
					psi.ArgumentList.Add("webvtt");
					psi.ArgumentList.Add("-");

					using var proc = Process.Start(psi);
					if (proc == null)
					{
						Debug.WriteLine("[FFMPEG] FATAL: Process.Start() returned null.");
						return results;
					}

					using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

					var stdOutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
					var stdErrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

					try
					{
						await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync(timeoutCts.Token));
					}
					catch (OperationCanceledException)
					{
						try { proc.Kill(entireProcessTree: true); } catch { }
						Debug.WriteLine("[FFMPEG] Process cancelled or timed out.");
						return results;
					}

					string srtOutput = await stdOutTask;
					string errOutput = await stdErrTask;

					Debug.WriteLine($"[FFMPEG] Process exited with code: {proc.ExitCode} for track {trackIndex}");

					if (proc.ExitCode != 0)
					{
						if (errOutput.Contains("matches no streams"))
						{
							Debug.WriteLine($"[FFMPEG] Track {trackIndex} does not exist. Halting extraction loop.");
							break;
						}
						else
						{
							Debug.WriteLine($"[FFMPEG] Track {trackIndex} failed (likely image-based format). Skipping. StdErr: {errOutput.Trim()}");
							continue;
						}
					}

					if (!string.IsNullOrWhiteSpace(srtOutput))
					{
						Debug.WriteLine($"[FFMPEG] Extraction successful on track {trackIndex}. Byte length: {srtOutput.Length}");
						results.Add(new KeyValuePair<int, string>(trackIndex, StripAssTags(srtOutput)));
					}
					else
					{
						Debug.WriteLine($"[FFMPEG] Standard Output empty for track {trackIndex}.");
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[FFMPEG] EXCEPTION on track {trackIndex}: {ex.Message}");
				}
			}

			Debug.WriteLine($"[FFMPEG] Extraction complete. Found {results.Count} embedded text tracks.");
			return results;
		}

		public static string? ResolveFfmpegPath()
		{
			string[] candidates =
			[
				Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg.exe"),
				Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe")
			];

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

		public static async Task<List<KeyValuePair<int, string>>> ExtractAudioTracksAsync(string mediaFilePath, CancellationToken cancellationToken = default)
		{
			var results = new List<KeyValuePair<int, string>>();
			string? ffmpegExe = ResolveFfmpegPath();
			if (ffmpegExe == null) return results;

			var psi = new ProcessStartInfo
			{
				FileName = ffmpegExe,
				Arguments = $"-hide_banner -i \"{mediaFilePath}\"",
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardErrorEncoding = Encoding.UTF8
			};

			try
			{
				using var proc = Process.Start(psi);
				if (proc == null) return results;

				using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

				string errOutput = await proc.StandardError.ReadToEndAsync(timeoutCts.Token);

				int audioIndex = 0;
				string[] lines = errOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
				foreach (var line in lines)
				{
					if (line.Contains(": Audio:"))
					{
						var langMatch = Regex.Match(line, @"Stream #0:\d+(?:\[0x[a-fA-F0-9]+\])?(?:\((?<lang>[a-zA-Z]+)\))?: Audio: (?<codec>[a-zA-Z0-9_]+)");
						string lang = langMatch.Groups["lang"].Success ? langMatch.Groups["lang"].Value : "und";
						string codec = langMatch.Groups["codec"].Success ? langMatch.Groups["codec"].Value : "unknown";

						results.Add(new KeyValuePair<int, string>(audioIndex, $"Track {audioIndex + 1} ({lang}, {codec})"));
						audioIndex++;
					}
				}
			}
			catch { }
			return results;
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