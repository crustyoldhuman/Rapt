using Microsoft.UI.Xaml;
using System;
namespace Rapt
{
	public partial class App : Application
	{
		private Window? m_window;

		public App()
		{
			this.InitializeComponent();
			this.UnhandledException += App_UnhandledException;
		}

		private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
		{
			e.Handled = true; // Attempt to keep app alive
		}

		protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			System.Threading.Tasks.Task.Run(StartPipeServer);

			var mainInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("RaptMainInstance");
			mainInstance.Activated += OnAppActivated;

			m_window = new MainWindow();
			m_window.Activate();

			var activatedArgs = mainInstance.GetActivatedEventArgs();
			HandleActivation(activatedArgs, "Cold Start");
		}

		private async System.Threading.Tasks.Task StartPipeServer()
		{
			while (true)
			{
				try
				{
					using var server = new System.IO.Pipes.NamedPipeServerStream("RaptSingleInstancePipe", System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
					await server.WaitForConnectionAsync();
					using var reader = new System.IO.StreamReader(server);
					string? message = await reader.ReadLineAsync();

					if (!string.IsNullOrWhiteSpace(message))
					{
						string[] pipeArgs = message.Split('|');
						m_window?.DispatcherQueue.TryEnqueue(() =>
						{
							if (m_window != null)
							{
								var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(m_window);
								var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
								var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

								if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
								{
									presenter.Restore();
								}
								m_window.Activate();

								if (pipeArgs.Length > 1)
								{
									string possiblePath = pipeArgs[1].Trim('\"', '\'');
									if (System.IO.File.Exists(possiblePath) && !possiblePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
									{
										((MainWindow)m_window).LoadInitialFiles(new[] { possiblePath });
									}
									else if (pipeArgs.Length > 2)
									{
										string combinedPath = string.Join(" ", pipeArgs, 1, pipeArgs.Length - 1).Trim('\"', '\'');
										if (System.IO.File.Exists(combinedPath) && !combinedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
										{
											((MainWindow)m_window).LoadInitialFiles(new[] { combinedPath });
										}
									}
								}
							}
						});
					}
				}
				catch (Exception)
				{
					await System.Threading.Tasks.Task.Delay(100);
				}
			}
		}

		private void OnAppActivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments e)
		{
			m_window?.DispatcherQueue.TryEnqueue(() =>
			{
				HandleActivation(e, "Warm Start");

				if (m_window != null)
				{
					var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(m_window);
					var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
					var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

					if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
					{
						presenter.Restore();
					}
					m_window.Activate();
				}
			});
		}

		private void HandleActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments e, string startType)
		{
			bool fileLoaded = false;

			if (e.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
			{
				if (e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
				{
					var paths = new System.Collections.Generic.List<string>();
					foreach (var file in fileArgs.Files)
					{
						paths.Add(file.Path);
					}
					((MainWindow)m_window!).LoadInitialFiles(paths);
					fileLoaded = true;
				}
			}
			else if (e.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
			{
				if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
				{
					if (!string.IsNullOrWhiteSpace(launchArgs.Arguments))
					{
						string possiblePath = launchArgs.Arguments.Trim('\"', '\'');
						if (System.IO.File.Exists(possiblePath) && !possiblePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
						{
							((MainWindow)m_window!).LoadInitialFiles(new[] { possiblePath });
							fileLoaded = true;
						}
					}
				}
			}

			if (!fileLoaded && startType == "Cold Start")
			{
				var cmdArgs = System.Environment.GetCommandLineArgs();
				if (cmdArgs.Length > 1)
				{
					string possiblePath = cmdArgs[1].Trim('\"', '\'');
					if (System.IO.File.Exists(possiblePath) && !possiblePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					{
						((MainWindow)m_window!).LoadInitialFiles(new[] { possiblePath });
					}
					else if (cmdArgs.Length > 2)
					{
						string combinedPath = string.Join(" ", cmdArgs, 1, cmdArgs.Length - 1).Trim('\"', '\'');
						if (System.IO.File.Exists(combinedPath) && !combinedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
						{
							((MainWindow)m_window!).LoadInitialFiles(new[] { combinedPath });
						}
					}
				}
			}
		}
	}
}