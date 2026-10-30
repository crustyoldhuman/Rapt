using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Rapt
{
	[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763.0")]
	public sealed partial class MainWindow : Window
	{
		private readonly List<string> _pendingFilePaths = [];
		private bool _isPageReady = false;
		private CancellationTokenSource? _subtitleCts;
		private readonly Dictionary<string, string> _fileMap = new(StringComparer.OrdinalIgnoreCase);
		private readonly string _playlistFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rapt", "playlist.json");

		[System.Runtime.InteropServices.LibraryImport("user32.dll")]
		private static partial int ShowCursor([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool bShow);

		[System.Runtime.InteropServices.LibraryImport("user32.dll")]
		[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
		private static partial bool GetCursorPos(out POINT lpPoint);

		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
		public struct POINT { public int X; public int Y; }

		private bool _isNativeCursorHidden = false;
		private DateTime _cursorHideTime = DateTime.MinValue;
		private POINT _hidePosition;

		public MainWindow()
		{
			this.InitializeComponent();

			string iconPath = Path.Combine(AppContext.BaseDirectory, "RaptIcon.ico");
			if (File.Exists(iconPath))
			{
				this.AppWindow.SetIcon(iconPath);
				AppIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
			}

			this.ExtendsContentIntoTitleBar = true;
			this.SetTitleBar(AppTitleBar);

			var titleBar = this.AppWindow.TitleBar;
			titleBar.ButtonBackgroundColor = Colors.Black;
			titleBar.ButtonForegroundColor = Colors.White;
			titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
			titleBar.ButtonHoverForegroundColor = Colors.White;
			titleBar.ButtonInactiveBackgroundColor = Colors.Black;
			titleBar.ButtonInactiveForegroundColor = Colors.Gray;

			PlayerWebView.AllowDrop = true;
			PlayerWebView.DragEnter += PlayerWebView_DragEnter;
			PlayerWebView.DragOver += PlayerWebView_DragOver;
			PlayerWebView.Drop += PlayerWebView_Drop;

			DragOverlay.DragLeave += DragOverlay_DragLeave;
			DragOverlay.DragOver += PlayerWebView_DragOver;
			DragOverlay.Drop += PlayerWebView_Drop;

			DragOverlay.PointerMoved += WakeCursor;
			DragOverlay.PointerPressed += WakeCursor;
			DragOverlay.PointerWheelChanged += WakeCursor;
			AppTitleBar.PointerEntered += WakeCursor;

			InitializeWebViewAsync();
		}

		private void PlayerWebView_DragEnter(object sender, Microsoft.UI.Xaml.DragEventArgs e)
		{
			DragOverlay.Visibility = Visibility.Visible;
			if (PlayerWebView.CoreWebView2 != null)
				SendBridgeMessage("{\"type\":\"DRAG_ENTER\"}");
		}

		private void DragOverlay_DragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e)
		{
			DragOverlay.Visibility = Visibility.Collapsed;
			if (PlayerWebView.CoreWebView2 != null)
				SendBridgeMessage("{\"type\":\"DRAG_LEAVE\"}");
		}

		private void PlayerWebView_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = "Play media";
			e.DragUIOverride.IsCaptionVisible = true;
			e.DragUIOverride.IsContentVisible = true;
			e.DragUIOverride.IsGlyphVisible = true;
			e.Handled = true;
		}

		private async void PlayerWebView_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
		{
			var deferral = e.GetDeferral();
			var droppedFiles = new JsonArray();

			try
			{
				if (e.DataView.Contains(StandardDataFormats.StorageItems))
				{
					var items = await e.DataView.GetStorageItemsAsync();
					if (items.Count > 0)
					{
						foreach (var item in items)
						{
							if (item is Windows.Storage.StorageFile file)
							{
								_fileMap[file.Name] = file.Path;
								string ext = Path.GetExtension(file.Name);

								if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
								{
									string srtContent = await File.ReadAllTextAsync(file.Path);
									droppedFiles.Add(new JsonObject
									{
										["name"] = file.Name,
										["type"] = "text/plain",
										["srtContent"] = srtContent
									});
								}
								else
								{
									string mimeType = "application/octet-stream";
									if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) mimeType = "video/mp4";
									else if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)) mimeType = "video/webm";
									else if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) mimeType = "video/x-matroska";
									else if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/mpeg";
									else if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/wav";
									else if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/ogg";
									else if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/flac";

									string encodedFileName = Uri.EscapeDataString(file.Name);
									string cacheBuster = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

									droppedFiles.Add(new JsonObject
									{
										["name"] = file.Name,
										["type"] = mimeType,
										["isVirtual"] = true,
										["videoUrl"] = $"https://video.rapt/{encodedFileName}{cacheBuster}",
										["audioUrl"] = $"https://audio.rapt/{encodedFileName}{cacheBuster}"
									});
								}
							}
						}
					}
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				e.Handled = true;
				deferral.Complete();
				DragOverlay.Visibility = Visibility.Collapsed;
				if (PlayerWebView.CoreWebView2 != null)
					SendBridgeMessage("{\"type\":\"DRAG_LEAVE\"}");
			}

			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(100);

			if (droppedFiles.Count > 0)
			{
				var payload = new JsonObject
				{
					["type"] = "BATCH_PROCESS_FILES",
					["files"] = droppedFiles
				};
				SendBridgeMessage(payload.ToJsonString());
			}
		}

		public void LoadInitialFiles(IEnumerable<string> filePaths)
		{
			foreach (var path in filePaths)
			{
				string cleanPath = path.Trim('\"', '\'');
				if (!string.IsNullOrWhiteSpace(cleanPath))
				{
					_pendingFilePaths.Add(cleanPath);
				}
			}

			if (_isPageReady && _pendingFilePaths.Count > 0)
			{
				_ = ProcessBatchAsync([.. _pendingFilePaths]);
				_pendingFilePaths.Clear();
			}
		}

		private async Task ProcessBatchAsync(List<string> filePaths)
		{
			var droppedFiles = new JsonArray();

			foreach (string filePath in filePaths)
			{
				if (!File.Exists(filePath)) continue;

				string fileName = Path.GetFileName(filePath);
				_fileMap[fileName] = filePath;
				string ext = Path.GetExtension(fileName);

				if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
				{
					string srtContent = await File.ReadAllTextAsync(filePath);
					droppedFiles.Add(new JsonObject
					{
						["name"] = fileName,
						["type"] = "text/plain",
						["srtContent"] = srtContent
					});
				}
				else
				{
					string mimeType = "application/octet-stream";
					if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) mimeType = "video/mp4";
					else if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)) mimeType = "video/webm";
					else if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) mimeType = "video/x-matroska";
					else if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/mpeg";
					else if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/wav";
					else if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/ogg";
					else if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/flac";

					string encodedFileName = Uri.EscapeDataString(fileName);
					string cacheBuster = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

					droppedFiles.Add(new JsonObject
					{
						["name"] = fileName,
						["type"] = mimeType,
						["isVirtual"] = true,
						["videoUrl"] = $"https://video.rapt/{encodedFileName}{cacheBuster}",
						["audioUrl"] = $"https://audio.rapt/{encodedFileName}{cacheBuster}"
					});
				}
			}

			if (droppedFiles.Count > 0)
			{
				var payload = new JsonObject
				{
					["type"] = "BATCH_PROCESS_FILES",
					["files"] = droppedFiles
				};
				SendBridgeMessage(payload.ToJsonString());

				string? firstVideoName = null;
				foreach (var f in droppedFiles)
				{
					string fn = f?["name"]?.ToString() ?? "";
					if (!fn.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
					{
						firstVideoName = fn;
						break;
					}
				}

				if (firstVideoName != null && _fileMap.TryGetValue(firstVideoName, out string? videoPath))
				{
					_subtitleCts?.Cancel();
					_subtitleCts?.Dispose();
					_subtitleCts = new CancellationTokenSource();
					var token = _subtitleCts.Token;

					string? extractedSrt = await SubtitleExtractor.ExtractSrtAsync(videoPath, token);
					if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(extractedSrt))
					{
						var srtPayload = new JsonObject
						{
							["type"] = "LOAD_SRT",
							["videoName"] = firstVideoName,
							["srtName"] = Path.ChangeExtension(firstVideoName, ".srt"),
							["srtContent"] = extractedSrt
						};
						SendBridgeMessage(srtPayload.ToJsonString());
					}
				}
			}
		}

		private async void InitializeWebViewAsync()
		{
			try
			{
				// Use Windows-native D3D11/MediaFoundation flags only (VaapiVideoDecoder is Linux-only and disrupts Windows D3D11 overlays).
				Environment.SetEnvironmentVariable(
					"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
					"--autoplay-policy=no-user-gesture-required --enable-gpu-rasterization --enable-clear-hevc-for-testing --enable-features=MediaFoundationD3D11VideoCapture,PlatformHEVCDecoderSupport"
				);

				string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rapt", "WebView2");
				Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

				await PlayerWebView.EnsureCoreWebView2Async();

				// REQUIRED FOR NVIDIA VSR: DWM rejects Hardware Overlays if the WebView2 surface has potential transparency.
				PlayerWebView.DefaultBackgroundColor = Colors.Black;

				// Let WebView2 handle file drops and pointer events natively
				var coreWv = PlayerWebView.CoreWebView2;
				coreWv.Settings.AreDefaultContextMenusEnabled = false;
				coreWv.Settings.AreDevToolsEnabled = false;

				coreWv.NewWindowRequested += async (s, e) =>
				{
					e.Handled = true;
					if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? targetUri))
					{
						await Windows.System.Launcher.LaunchUriAsync(targetUri);
					}
				};

				// Sync WebView2 HTML Fullscreen API (btnFullscreen) with the native WinUI 3 Window
				coreWv.ContainsFullScreenElementChanged += (s, e) =>
				{
					bool isFullScreen = coreWv.ContainsFullScreenElement;
					this.AppWindow.SetPresenter(
						isFullScreen
							? AppWindowPresenterKind.FullScreen
							: AppWindowPresenterKind.Default
					);
					AppTitleBar.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
				};

				// Map the app's Assets folder to a virtual domain
				string assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
				coreWv.SetVirtualHostNameToFolderMapping(
					"app.rapt",
					assetsPath,
					CoreWebView2HostResourceAccessKind.Allow
				);

				// --- THE FIX: MANUAL MEDIA INTERCEPTION WITH FULL CORS & RANGE SUPPORT ---
				coreWv.AddWebResourceRequestedFilter("https://video.rapt/*", CoreWebView2WebResourceContext.All);
				coreWv.AddWebResourceRequestedFilter("https://audio.rapt/*", CoreWebView2WebResourceContext.All);

				coreWv.WebResourceRequested += async (s, e) =>
				{
					var deferral = e.GetDeferral();
					try
					{
						// 1. CORS Preflight for Cross-Origin Audio/Video pipelines
						if (e.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
						{
							e.Response = coreWv.Environment.CreateWebResourceResponse(
								null, 204, "No Content",
								"Access-Control-Allow-Origin: *\nAccess-Control-Allow-Methods: GET, OPTIONS\nAccess-Control-Allow-Headers: Range\nAccess-Control-Expose-Headers: Content-Range, Accept-Ranges, Content-Length"
							);
							return;
						}

						Uri uri = new(e.Request.Uri);
						string fileName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

						if (_fileMap.TryGetValue(fileName, out string? fullPath) && File.Exists(fullPath))
						{
							string ext = Path.GetExtension(fullPath);
							string mimeType = "application/octet-stream";

							if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) mimeType = "video/mp4";
							else if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)) mimeType = "video/webm";
							else if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) mimeType = "video/x-matroska";
							else if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/mpeg";
							else if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/wav";
							else if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/ogg";
							else if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)) mimeType = "audio/flac";

							// Use standard FileStream to allow concurrent FileAccess.Read bypassing WinRT locking overhead
							using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);

							ulong totalSize = (ulong)fs.Length;
							ulong start = 0;
							ulong end = totalSize - 1;
							int statusCode = 200;
							string statusText = "OK";
							string rangeHeader = "";

							try
							{
								if (e.Request.Headers.Contains("Range"))
								{
									rangeHeader = e.Request.Headers.GetHeader("Range");
								}
							}
							catch { }

							const ulong maxChunkSize = 2 * 1024 * 1024; // 2 MB chunk cap to prevent full-file RAM buffering

							if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
							{
								var range = rangeHeader[6..].Split('-');
								if (ulong.TryParse(range[0], out ulong sVal)) start = sVal;
								if (range.Length > 1 && ulong.TryParse(range[1], out ulong eVal))
								{
									end = Math.Min(eVal, totalSize - 1);
								}
								else
								{
									end = Math.Min(start + maxChunkSize - 1, totalSize - 1);
								}

								statusCode = 206;
								statusText = "Partial Content";
							}

							ulong length = end - start + 1;

							// Create an IRandomAccessStream wrapper for the specific chunk Chromium requested
							var memoryStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
							using (var dataWriter = new Windows.Storage.Streams.DataWriter(memoryStream))
							{
								byte[] buffer = new byte[length];
								fs.Seek((long)start, SeekOrigin.Begin);
								int totalRead = 0;
								while (totalRead < (int)length)
								{
									int bytesRead = await fs.ReadAsync(buffer.AsMemory(totalRead, (int)length - totalRead));
									if (bytesRead == 0) break;
									totalRead += bytesRead;
								}
								dataWriter.WriteBytes(buffer);
								await dataWriter.StoreAsync();
								await dataWriter.FlushAsync();
								dataWriter.DetachStream();
							}
							memoryStream.Seek(0);

							// Expose the precise byte ranges Chromium requested so it does not pause the stream engine
							string headers = $"Content-Type: {mimeType}\nAccept-Ranges: bytes\nContent-Length: {length}\nAccess-Control-Allow-Origin: *\nAccess-Control-Expose-Headers: Content-Range, Accept-Ranges, Content-Length";
							if (statusCode == 206)
							{
								headers += $"\nContent-Range: bytes {start}-{end}/{totalSize}";
							}

							e.Response = coreWv.Environment.CreateWebResourceResponse(
								memoryStream,
								statusCode,
								statusText,
								headers
							);
						}
						else
						{
							e.Response = coreWv.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
						}
					}
					catch (Exception)
					{
					}
					finally
					{
						deferral.Complete();
					}
				};

				// Handle messages from JS
				coreWv.WebMessageReceived += async (s, e) =>
				{
					try
					{
						string msg = e.TryGetWebMessageAsString();

						if (msg != null && msg.StartsWith("LOG:"))
						{
							System.Diagnostics.Debug.WriteLine($"[JS] {msg[4..]}");
							return;
						}

						if (msg != null && msg.Contains("\"type\":\"HIDE_CURSOR\""))
						{
							System.Diagnostics.Debug.WriteLine("[C#] HIDE_CURSOR message received from JS.");
							if (!_isNativeCursorHidden)
							{
								GetCursorPos(out _hidePosition);
								_cursorHideTime = DateTime.UtcNow;

								// WinUI 3 intercepts OS ShowCursor commands with its own input stack.
								// The native workaround to hide a cursor is assigning a Disposed InputSystemCursor to the ProtectedCursor property.
								var hiddenCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
								hiddenCursor.Dispose();
								typeof(Microsoft.UI.Xaml.UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(DragOverlay, hiddenCursor);

								DragOverlay.Visibility = Visibility.Visible;
								_isNativeCursorHidden = true;
								System.Diagnostics.Debug.WriteLine("[C#] DragOverlay shown with Disposed ProtectedCursor.");
							}
							else
							{
								System.Diagnostics.Debug.WriteLine("[C#] HIDE_CURSOR ignored: _isNativeCursorHidden is already true.");
							}
							return;
						}

						if (msg != null && msg.Contains("\"type\":\"BRIDGE_READY\""))
						{
							_isPageReady = true;

							if (_pendingFilePaths.Count > 0)
							{
								_ = ProcessBatchAsync([.. _pendingFilePaths]);
								_pendingFilePaths.Clear();
							}
							return;
						}

						if (msg != null && msg.Contains("\"type\":\"SAVE_PLAYLIST\""))
						{
							if (JsonNode.Parse(msg)?["items"] is JsonArray items)
							{
								var saveArray = new JsonArray();
								foreach (var item in items)
								{
									string? name = (string?)item;
									if (name != null && _fileMap.TryGetValue(name, out string? fullPath) && fullPath != null)
									{
										saveArray.Add(new JsonObject { ["name"] = name, ["path"] = fullPath });
									}
								}
								Directory.CreateDirectory(Path.GetDirectoryName(_playlistFile)!);
								await File.WriteAllTextAsync(_playlistFile, saveArray.ToJsonString());
							}
						}

						if (msg != null && msg.Contains("\"type\":\"PLAY_RESTORED\""))
						{
							string? name = (string?)JsonNode.Parse(msg)?["name"];
							if (name != null && _fileMap.TryGetValue(name, out string? fullPath) && fullPath != null)
							{
								_ = ProcessExternalFileAsync(fullPath);
							}
						}

						if (msg != null && msg.Contains("\"type\":\"EXPORT_PLAYLIST\""))
						{
							if (JsonNode.Parse(msg)?["items"] is JsonArray items)
							{
								var saveArray = new JsonArray();
								foreach (var item in items)
								{
									string? name = (string?)item;
									if (name != null && _fileMap.TryGetValue(name, out string? fullPath) && fullPath != null)
									{
										saveArray.Add(new JsonObject { ["name"] = name, ["path"] = fullPath });
									}
								}

								var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
								var savePicker = new Windows.Storage.Pickers.FileSavePicker();
								WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
								savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
								savePicker.FileTypeChoices.Add("JSON Playlist", [".json"]);
								savePicker.SuggestedFileName = "Rapt_Playlist";

								var file = await savePicker.PickSaveFileAsync();
								if (file != null)
								{
									await Windows.Storage.FileIO.WriteTextAsync(file, saveArray.ToJsonString());
								}
							}
						}

						if (msg != null && msg.Contains("IMPORT_PLAYLIST"))
						{
							if (e.AdditionalObjects != null)
							{
								foreach (var obj in e.AdditionalObjects)
								{
									if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
									{
										try
										{
											if (JsonNode.Parse(File.ReadAllText(webFile.Path)) is JsonArray json)
											{
												var restoreNames = new JsonArray();
												foreach (var item in json)
												{
													string? name = (string?)item?["name"];
													string? path = (string?)item?["path"];

													if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path) && File.Exists(path))
													{
														_fileMap[name] = path;
														restoreNames.Add(name);
													}
												}
												if (restoreNames.Count > 0)
												{
													SendBridgeMessage(new JsonObject { ["type"] = "RESTORE_PLAYLIST", ["items"] = restoreNames }.ToJsonString());
												}
											}
										}
										catch { }
									}
								}
							}
						}

						// Passively extract subtitles when the HTML player loads local files natively
						if (msg != null && msg.Contains("EXTRACT_SUBTITLES"))
						{
							if (e.AdditionalObjects != null)
							{
								foreach (var obj in e.AdditionalObjects)
								{
									if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
									{
										string fileName = Path.GetFileName(webFile.Path);
										_fileMap[fileName] = webFile.Path;
									}
								}

								_subtitleCts?.Cancel();
								_subtitleCts?.Dispose();
								_subtitleCts = new CancellationTokenSource();
								var token = _subtitleCts.Token;

								foreach (var obj in e.AdditionalObjects)
								{
									if (token.IsCancellationRequested) break;
									if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
									{
										if (!webFile.Path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
										{
											string videoFileName = Path.GetFileName(webFile.Path);
											string? extractedSrt = await SubtitleExtractor.ExtractSrtAsync(webFile.Path, token);
											if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(extractedSrt))
											{
												var srtPayload = new JsonObject
												{
													["type"] = "LOAD_SRT",
													["videoName"] = videoFileName,
													["srtName"] = Path.ChangeExtension(videoFileName, ".srt"),
													["srtContent"] = extractedSrt
												};
												SendBridgeMessage(srtPayload.ToJsonString());
											}
										}
									}
								}
							}
						}
					}
					catch { }
				};

				// Intercept files dragged & dropped directly from Windows Explorer if you also want auto-demuxing on drop
				PlayerWebView.Source = new Uri("https://app.rapt/index.html?isNative=1");

				// _isPageReady is deferred until BRIDGE_READY is received from JS
			}
			catch (Exception)
			{
			}
		}

		public async Task ProcessExternalFileAsync(string filePath)
		{
			if (!File.Exists(filePath))
			{
				return;
			}

			string ext = Path.GetExtension(filePath);
			string fileName = Path.GetFileName(filePath);

			if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
			{
				string srtText = await File.ReadAllTextAsync(filePath);
				var srtPayload = new JsonObject
				{
					["type"] = "LOAD_SRT",
					["srtName"] = fileName,
					["srtContent"] = srtText
				};
				SendBridgeMessage(srtPayload.ToJsonString());
				return;
			}

			_fileMap[fileName] = filePath;
			string encodedFileName = Uri.EscapeDataString(fileName);
			string cacheBuster = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			string virtualVideoUrl = $"https://video.rapt/{encodedFileName}{cacheBuster}";
			string virtualAudioUrl = $"https://audio.rapt/{encodedFileName}{cacheBuster}";

			var mediaPayload = new JsonObject
			{
				["type"] = "LOAD_MEDIA",
				["fileName"] = fileName,
				["videoUrl"] = virtualVideoUrl,
				["audioUrl"] = virtualAudioUrl
			};
			SendBridgeMessage(mediaPayload.ToJsonString());
		}

		private void SendBridgeMessage(string json)
		{
			PlayerWebView.CoreWebView2.PostWebMessageAsString(json);
		}

		private void WakeCursor(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
		{
			if (_isNativeCursorHidden)
			{
				double elapsedMs = (DateTime.UtcNow - _cursorHideTime).TotalMilliseconds;
				if (elapsedMs > 150)
				{
					bool isSenderDragOverlay = object.ReferenceEquals(sender, DragOverlay) || (sender is FrameworkElement fe && fe.Name == "DragOverlay");
					System.Diagnostics.Debug.WriteLine($"[C#] WakeCursor evaluating > 150ms. Sender: {sender?.GetType().Name}. isSenderDragOverlay: {isSenderDragOverlay}");

					if (isSenderDragOverlay && e != null)
					{
						bool gotPos = GetCursorPos(out POINT currentPos);
						if (gotPos)
						{
							int diffX = Math.Abs(currentPos.X - _hidePosition.X);
							int diffY = Math.Abs(currentPos.Y - _hidePosition.Y);
							System.Diagnostics.Debug.WriteLine($"[C#] WakeCursor position check. Current: {currentPos.X},{currentPos.Y}. Hidden at: {_hidePosition.X},{_hidePosition.Y}. Diff: {diffX},{diffY}");

							if (diffX < 5 && diffY < 5)
							{
								System.Diagnostics.Debug.WriteLine("[C#] WakeCursor BLOCKED: Mouse is physically stationary (<5px). Returning.");
								return; // Block synthetic layout events when mouse is physically stationary
							}
						}
						else
						{
							System.Diagnostics.Debug.WriteLine("[C#] WakeCursor GetCursorPos failed.");
						}
					}

					System.Diagnostics.Debug.WriteLine($"[C#] WakeCursor executing unhide. Elapsed: {elapsedMs:F1}ms");

					var arrowCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
					typeof(Microsoft.UI.Xaml.UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(DragOverlay, arrowCursor);

					_isNativeCursorHidden = false;
					DragOverlay.Visibility = Visibility.Collapsed;
					System.Diagnostics.Debug.WriteLine("[C#] DragOverlay.Visibility set to Collapsed.");
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"[C#] WakeCursor ignored: Triggered too soon ({elapsedMs:F1}ms < 150ms) after hide event.");
				}
			}
		}
	}
}