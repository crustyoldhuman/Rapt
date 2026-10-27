using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace Rapt
{
	public partial class App : Application
	{
		public static void Log(string message)
		{
			try
			{
				string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rapt", "debug_log.txt");
				Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
				File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
			}
			catch { }
		}
		private Window? m_window;

		public App()
		{
			this.InitializeComponent();
			this.UnhandledException += App_UnhandledException;
		}

		private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
		{
			Log($"[GLOBAL CRASH] {e.Exception.Message}\n{e.Exception.StackTrace}");
			e.Handled = true; // Attempt to keep app alive
		}

		protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			m_window = new MainWindow();
			m_window.Activate();

			var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
			if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
			{
				if (activatedArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
				{
					((MainWindow)m_window).LoadInitialFile(fileArgs.Files[0].Path);
				}
			}
		}
	}
}