using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Rapt
{
	public static partial class RaptStartup
	{
		[STAThread]
		static int Main()
		{
			try
			{
				return DoMain();
			}
			catch (Exception)
			{
				return -1;
			}
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static int DoMain()
		{
			WinRT.ComWrappersSupport.InitializeComWrappers();

			bool isRedirect = DecideRedirection();

			if (!isRedirect)
			{
				Application.Start((p) =>
				{
					var context = new DispatcherQueueSynchronizationContext(
						DispatcherQueue.GetForCurrentThread());
					SynchronizationContext.SetSynchronizationContext(context);
					_ = new App();
				});
			}

			return 0;
		}

		private static bool DecideRedirection()
		{
			AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
			AppInstance keyInstance = AppInstance.FindOrRegisterForKey("RaptMainInstance");

			if (keyInstance.IsCurrent)
			{
				return false;
			}
			else
			{
				try
				{
					using var client = new System.IO.Pipes.NamedPipeClientStream(".", "RaptSingleInstancePipe", System.IO.Pipes.PipeDirection.Out);
					client.Connect(2000);
					using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
					writer.WriteLine(string.Join("|", Environment.GetCommandLineArgs()));
				}
				catch (Exception)
				{
				}

				RedirectActivationTo(args, keyInstance);
				return true;
			}
		}

		[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
		private static partial IntPtr CreateEvent(IntPtr _lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool _bManualReset, [MarshalAs(UnmanagedType.Bool)] bool _bInitialState, string? _lpName);

		[LibraryImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool SetEvent(IntPtr _hEvent);

		[LibraryImport("ole32.dll")]
		private static partial uint CoWaitForMultipleObjects(uint _dwFlags, uint _dwMilliseconds, ulong _nHandles, [In] IntPtr[] _pHandles, out uint _dwIndex);

		[LibraryImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool SetForegroundWindow(IntPtr _hWnd);

		private static IntPtr redirectEventHandle = IntPtr.Zero;

		public static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
		{
			redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
			Task.Run(() =>
			{
				keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
				SetEvent(redirectEventHandle);
			});

			uint CWMO_DEFAULT = 0;
			uint INFINITE = 0xFFFFFFFF;
			_ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirectEventHandle], out uint handleIndex);

			try
			{
				Process process = Process.GetProcessById((int)keyInstance.ProcessId);
				if (process.MainWindowHandle != IntPtr.Zero)
				{
					SetForegroundWindow(process.MainWindowHandle);
				}
			}
			catch { }
		}
	}
}