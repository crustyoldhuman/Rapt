using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using System.Linq;

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

			// Check if launched via double-clicking an associated file
			var appArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
			if (appArgs.Kind == ExtendedActivationKind.File &&
				appArgs.Data is IFileActivatedEventArgs fileArgs &&
				fileArgs.Files.Count > 0 &&
				fileArgs.Files[0] is IStorageFile storageFile)
			{
				((MainWindow)m_window).LoadInitialFile(storageFile.Path);
			}
		}
	}
}