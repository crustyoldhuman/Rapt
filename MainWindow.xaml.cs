using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace SSVidPlayer
{
	public sealed partial class MainWindow : Window
	{
		private string? _pendingFilePath;
		private bool _isPageReady = false;
		private readonly Dictionary<string, string> _fileMap = new(StringComparer.OrdinalIgnoreCase);
		private readonly string _playlistFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSVidPlayer", "playlist.json");

		public MainWindow()
		{
			this.InitializeComponent();

			string iconPath = Path.Combine(AppContext.BaseDirectory, "SSVidPlayerIcon.ico");
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

			InitializeWebViewAsync();
		}

		public void LoadInitialFile(string filePath)
		{
			_pendingFilePath = filePath;
			if (_isPageReady)
			{
				_ = ProcessExternalFileAsync(filePath);
			}
		}

		private async void InitializeWebViewAsync()
		{
			// Use Windows-native D3D11/MediaFoundation flags only (VaapiVideoDecoder is Linux-only and disrupts Windows D3D11 overlays).
			Environment.SetEnvironmentVariable(
				"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
				"--autoplay-policy=no-user-gesture-required --enable-gpu-rasterization --enable-clear-hevc-for-testing --enable-features=MediaFoundationD3D11VideoCapture,PlatformHEVCDecoderSupport"
			);

			string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSVidPlayer", "WebView2");
			Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

			await PlayerWebView.EnsureCoreWebView2Async();

			// REQUIRED FOR NVIDIA VSR: DWM rejects Hardware Overlays if the WebView2 surface has potential transparency.
			PlayerWebView.DefaultBackgroundColor = Colors.Black;

			// Let WebView2 handle file drops and pointer events natively

			var coreWv = PlayerWebView.CoreWebView2;
			coreWv.Settings.AreDefaultContextMenusEnabled = false;
			coreWv.Settings.AreDevToolsEnabled = false;

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
				"app.ssvidplayer",
				assetsPath,
				CoreWebView2HostResourceAccessKind.Allow
			);

			// Handle messages from JS
			coreWv.WebMessageReceived += async (s, e) =>
			{
				try
				{
					string msg = e.TryGetWebMessageAsString();
					if (msg != null && msg.StartsWith("LOG:"))
					{
						System.Diagnostics.Debug.WriteLine($"[JS Bridge] {msg[4..]}");
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
							File.WriteAllText(_playlistFile, saveArray.ToJsonString());
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
							savePicker.FileTypeChoices.Add("JSON Playlist", new List<string>() { ".json" });
							savePicker.SuggestedFileName = "SSVid_Playlist";

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
							// Phase 1: Synchronously map all files to ensure _fileMap is ready for SAVE_PLAYLIST
							foreach (var obj in e.AdditionalObjects)
							{
								if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
								{
									string fileName = Path.GetFileName(webFile.Path);
									_fileMap[fileName] = webFile.Path; // Map full path to survive session reloads
								}
							}

							// Phase 2: Asynchronously extract subtitles without blocking file ingestion
							foreach (var obj in e.AdditionalObjects)
							{
								if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
								{
									if (!webFile.Path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
									{
										string? extractedSrt = await SubtitleExtractor.ExtractSrtAsync(webFile.Path);
										if (!string.IsNullOrWhiteSpace(extractedSrt))
										{
											var srtPayload = new JsonObject
											{
												["type"] = "LOAD_SRT",
												["srtName"] = Path.ChangeExtension(Path.GetFileName(webFile.Path), ".srt"),
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

			// Inject the C# -> JS Bridge BEFORE index.html executes
			string bridgeScript = @"
                window._restoredFiles = new Set();

                // 0. Intercept URL.createObjectURL to detect size-0 synthetic restored files
                const originalCreateObjectURL = URL.createObjectURL;
                URL.createObjectURL = function(obj) {
                    if (obj instanceof File && obj.size === 0 && window._restoredFiles.has(obj.name)) {
                        window.chrome.webview.postMessage(JSON.stringify({ type: 'PLAY_RESTORED', name: obj.name }));
                        const playlistHandle = document.getElementById('playlistHandle');
                        if (playlistHandle) playlistHandle.classList.remove('stay-visible');
                        // Return empty base64 video to suppress HTML5 Unsupported Format error before C# pushes the actual virtual URL
                        return 'data:video/mp4;base64,AAAA';
                    }
                    return originalCreateObjectURL.apply(this, arguments);
                };
                
                // 1. Web Audio Mock Removed: The two-URL separation preserves VSR while allowing real GainNodes for volume boost.

                // 2. NVIDIA VSR requires output resolution > native video resolution (scale > 1.0x for 1080p video).
                window.addEventListener('DOMContentLoaded', () => {
                    const nativeControls = document.getElementById('playlistNativeControls');
                    if (nativeControls) nativeControls.style.display = 'flex';

                    const vp = document.getElementById('videoPlayer');
                    const slider = document.getElementById('sizeSlider');
                    if (vp && slider) {
                        vp.addEventListener('loadedmetadata', () => {
                            setTimeout(() => {
                                if (parseFloat(slider.value) <= 1.0) {
                                    slider.value = 1.05;
                                    slider.dispatchEvent(new Event('input'));
                                }
                            }, 50);
                        });
                    }

                    const playlistList = document.getElementById('playlistList');
                    if (playlistList) {
                        const observer = new MutationObserver(() => {
                            const items = Array.from(playlistList.querySelectorAll('.playlist-item-text'))
                                               .filter(span => !span.innerText.startsWith('* '))
                                               .map(span => span.innerText);
                            window.chrome.webview.postMessage(JSON.stringify({ type: 'SAVE_PLAYLIST', items: items }));
                        });
                        observer.observe(playlistList, { childList: true, subtree: true });
                    }
                });

                // 3. Passively listen for file selection/drop so index.html uses native 'blob:' URIs while C# extracts subtitles
                window.addEventListener('change', (e) => {
                    if (e.target && e.target._isRestoring) return;
                    if (e.target && (e.target.id === 'videoFileInput' || e.target.id === 'playlistAddInput') && e.target.files.length > 0) {
                        window.chrome.webview.postMessageWithAdditionalObjects('EXTRACT_SUBTITLES', e.target.files);
                    }
                }, true);

                window.addEventListener('drop', (e) => {
                    if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) {
                        window.chrome.webview.postMessageWithAdditionalObjects('EXTRACT_SUBTITLES', e.dataTransfer.files);
                    }
                }, true);

                window.chrome.webview.addEventListener('message', event => {
                    const msg = JSON.parse(event.data);

                    if (msg.type === 'RESTORE_PLAYLIST') {
                        const dt = new DataTransfer();
                        msg.items.forEach(itemName => {
                            dt.items.add(new File([''], itemName, { type: 'video/mp4' }));
                            window._restoredFiles.add(itemName);
                        });
                        const input = document.getElementById('playlistAddInput');
                        if (input && dt.items.length > 0) {
                            input._isRestoring = true;
                            input.files = dt.files;
                            input.dispatchEvent(new Event('change'));
                            input._isRestoring = false;
                        }
                    }

                    if (msg.type === 'LOAD_MEDIA') {
                        // Original C# launch support for App.xaml.cs double-click activation
                        filenameDisplay.innerText = msg.fileName;
                        removeManualTrack();
                        previousSubSelection = null;
                        cropControls.style.display = 'none';
                        cropTop.value = 0; cropBottom.value = 0; cropLeft.value = 0; cropRight.value = 0;
                        applyVideoCrop();

                        subSelect.innerHTML = '';
                        subSelect.value = '';
                        subSelect.style.display = 'none';
                        btnSubtitles.style.display = 'block';
                        seekSlider.disabled = true;
                        seekSlider.value = 0;
                        timeDisplay.innerText = '0:00 / 0:00';

                        playOverlay.classList.remove('ready');
                        playOverlay.classList.add('visible');
                        overlayContent.innerHTML = '<div class=""spinner""></div><div class=""play-text"">Loading video...</div>';

                        videoPlayer.src = msg.videoUrl;
                        audioPlayer.src = msg.audioUrl;
                        isFirstFrame = true;

                        if (!audioCtx) initAudio();
                        if (audioCtx && audioCtx.state === 'suspended') {
                            audioCtx.resume().catch(() => {});
                        }

                        videoPlayer.load();
                        audioPlayer.load();
                    }

                    if (msg.type === 'LOAD_SRT' && msg.srtContent) {
                        setTimeout(() => {
                            const blob = new Blob([msg.srtContent], { type: 'text/plain' });
                            const syntheticFile = new File([blob], msg.srtName || 'embedded.srt', { type: 'text/plain' });

                            if (videoPlayer.readyState >= 1) {
                                loadSrt(syntheticFile);
                            } else {
                                videoPlayer.addEventListener('loadedmetadata', () => {
                                    loadSrt(syntheticFile);
                                }, { once: true });
                            }
                        }, 500);
                    }
                });
            ";

			await coreWv.AddScriptToExecuteOnDocumentCreatedAsync(bridgeScript);

			coreWv.NavigationCompleted += async (s, e) =>
			{
				_isPageReady = true;

				if (File.Exists(_playlistFile))
				{
					try
					{
						if (JsonNode.Parse(File.ReadAllText(_playlistFile)) is JsonArray json)
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

				if (!string.IsNullOrEmpty(_pendingFilePath))
				{
					string path = _pendingFilePath;
					_pendingFilePath = null;
					await ProcessExternalFileAsync(path);
				}
			};

			// Intercept files dragged & dropped directly from Windows Explorer if you also want auto-demuxing on drop
			PlayerWebView.Source = new Uri("https://app.ssvidplayer/index.html");
		}

		public async Task ProcessExternalFileAsync(string filePath)
		{
			if (!File.Exists(filePath)) return;

			var coreWv = PlayerWebView.CoreWebView2;
			string ext = Path.GetExtension(filePath).ToLowerInvariant();
			string fileName = Path.GetFileName(filePath);

			// If the user double-clicked a standalone .srt file while a video is open
			if (ext == ".srt")
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

			// Map the directory of the opened media file so WebView2 can stream it directly with zero copy
			// We map TWO distinct virtual hostnames to the exact same folder. 
			// This forces Chromium to treat the video and audio streams as separate cross-origin resources,
			// guaranteeing two isolated media pipelines so WebAudio does not disable the VSR hardware overlay.
			string folderPath = Path.GetDirectoryName(filePath)!;
			coreWv.SetVirtualHostNameToFolderMapping(
				"video.ssvidplayer",
				folderPath,
				CoreWebView2HostResourceAccessKind.Allow
			);
			coreWv.SetVirtualHostNameToFolderMapping(
				"audio.ssvidplayer",
				folderPath,
				CoreWebView2HostResourceAccessKind.Allow
			);

			string encodedFileName = Uri.EscapeDataString(fileName);
			string virtualVideoUrl = $"https://video.ssvidplayer/{encodedFileName}";
			string virtualAudioUrl = $"https://audio.ssvidplayer/{encodedFileName}";

			// 1. Tell the player to load the video & audio streams
			var mediaPayload = new JsonObject
			{
				["type"] = "LOAD_MEDIA",
				["fileName"] = fileName,
				["videoUrl"] = virtualVideoUrl,
				["audioUrl"] = virtualAudioUrl
			};
			SendBridgeMessage(mediaPayload.ToJsonString());

			// 2. Demux embedded subtitles (or companion .srt) in the background and inject into loadSrt(file)
			string? extractedSrt = await SubtitleExtractor.ExtractSrtAsync(filePath);
			if (!string.IsNullOrWhiteSpace(extractedSrt))
			{
				var srtPayload = new JsonObject
				{
					["type"] = "LOAD_SRT",
					["srtName"] = Path.ChangeExtension(fileName, ".srt"),
					["srtContent"] = extractedSrt
				};
				SendBridgeMessage(srtPayload.ToJsonString());
			}
		}

		private void SendBridgeMessage(string json)
		{
			PlayerWebView.CoreWebView2.PostWebMessageAsString(json);
		}
	}
}