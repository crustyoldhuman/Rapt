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
		public static void LogStartup(string msg)
		{
			try
			{
				string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rapt");
				System.IO.Directory.CreateDirectory(dir);
				System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "Rapt_Startup.log"), $"[{DateTime.Now:O}] {msg}\n");
			}
			catch { }
		}

		[STAThread]
		static int Main()
		{
			LogStartup("=================================");
			LogStartup("Program.Main started. Command Line: " + Environment.CommandLine);
			try
			{
				return DoMain();
			}
			catch (Exception ex)
			{
				LogStartup($"CRITICAL CRASH: {ex}");
				return -1;
			}
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		static int DoMain()
		{
			WinRT.ComWrappersSupport.InitializeComWrappers();
			LogStartup("ComWrappers initialized.");

			bool isRedirect = DecideRedirection();
			LogStartup($"DecideRedirection returned: {isRedirect}");

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
			LogStartup("Fetching Activation Args...");
			AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
			LogStartup("Registering AppInstance...");
			AppInstance keyInstance = AppInstance.FindOrRegisterForKey("RaptMainInstance");

			if (keyInstance.IsCurrent)
			{
				LogStartup("This is the PRIMARY instance.");
				return false;
			}
			else
			{
				LogStartup("This is a SECONDARY instance. Sending payload via Pipe...");
				try
				{
					using var client = new System.IO.Pipes.NamedPipeClientStream(".", "RaptSingleInstancePipe", System.IO.Pipes.PipeDirection.Out);
					client.Connect(2000);
					using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
					writer.WriteLine(string.Join("|", Environment.GetCommandLineArgs()));
					LogStartup("Pipe payload sent successfully.");
				}
				catch (Exception ex)
				{
					LogStartup($"Pipe transmission failed: {ex.Message}");
				}

				LogStartup("Redirecting activation to foreground primary window...");
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