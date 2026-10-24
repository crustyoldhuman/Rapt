using Microsoft.UI.Xaml;

namespace SSVidPlayer
{
	public partial class App : Application
	{
		private Window? m_window;

		public App()
		{
			this.InitializeComponent();
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