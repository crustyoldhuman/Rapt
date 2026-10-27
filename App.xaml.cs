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