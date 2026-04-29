using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    public class TerminalLifetime : IClassicDesktopStyleApplicationLifetime, IDisposable
    {
        private CancellationTokenSource _cts = new();
        private bool _disposed;
        private int _exitCode;
        private bool _isShuttingDown;

        public string[] Args { get; set; }

        public event EventHandler<ControlledApplicationLifetimeStartupEventArgs> Startup;
        public event EventHandler<ControlledApplicationLifetimeExitEventArgs> Exit;
        public event EventHandler<ShutdownRequestedEventArgs> ShutdownRequested;

        public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnExplicitShutdown;
        public Window MainWindow { get; set; }
        public IReadOnlyList<Window> Windows => new List<Window> { MainWindow };

        public void Shutdown(int exitCode = 0) => DoShutdown(new ShutdownRequestedEventArgs(), true, true, exitCode);

        public bool TryShutdown(int exitCode = 0) => DoShutdown(new ShutdownRequestedEventArgs(), true, false, exitCode);

        public int Start(string[] args)
        {
            Startup?.Invoke(this, new ControlledApplicationLifetimeStartupEventArgs(args));
            MainWindow.Closed += (_, _) => TryShutdown();
            MainWindow.Show();

            // Force a resize after show so Avalonia re-layouts at the correct terminal dimensions.
            // The initial layout may have used stale/zero ClientSize before PrepareConsole ran.
            if (MainWindow.PlatformImpl is TerminalWindow tw)
            {
                var size = tw.ClientSize;
                tw.Resized?.Invoke(size, WindowResizeReason.Unspecified);
            }

            try
            {
                Dispatcher.UIThread.MainLoop(_cts.Token);
                Environment.ExitCode = _exitCode;
                return _exitCode;
            }
            finally
            {
                Dispose();
            }
        }

        private bool DoShutdown(ShutdownRequestedEventArgs e, bool isProgrammatic, bool force, int exitCode = 0)
        {
            if (!force)
            {
                ShutdownRequested?.Invoke(this, e);
                if (e.Cancel) return false;
                if (_isShuttingDown) throw new InvalidOperationException("Already shutting down.");
            }

            _exitCode = exitCode;
            _isShuttingDown = true;

            try
            {
                var args = new ControlledApplicationLifetimeExitEventArgs(exitCode);
                Exit?.Invoke(this, args);
                _exitCode = args.ApplicationExitCode;
            }
            finally
            {
                _isShuttingDown = false;
                _cts?.Cancel();
                _cts = null;
                Dispatcher.UIThread.InvokeShutdown();
            }

            return true;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _cts?.Dispose();
                _cts = null;
                if (MainWindow?.PlatformImpl is TerminalWindow window)
                    window.Dispose();
            }
        }
    }
}
