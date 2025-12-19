using Microsoft.Win32;

namespace scap2jpeg
{
    static class Program
    {
        private static readonly Logger _logger = new();
        private static readonly CancellationTokenSource _cts = new();
        private static ScreenCaptureService? _service;

        [STAThread]
        static void Main()
        {
            try
            {
                _logger.LogInfo("Application started.");

                using Mutex mutex = new(true, "scap2jpeg_mutex", out bool createdNew);
                if (!createdNew)
                {
                    _logger.LogInfo("Application already started. Stop.");
                    return;
                }

                ApplicationConfiguration.Initialize();

                _service = new(_logger, _cts.Token);

                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;

                _service.Start();

                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                _service?.Stop();
                _cts.Cancel();
                _cts.Dispose();
            }
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                switch (e.Reason)
                {
                    case SessionSwitchReason.SessionLock:
                        _logger.LogInfo("Session locking.");
                        _service?.Stop();
                        break;
                    case SessionSwitchReason.SessionUnlock:
                        _logger.LogInfo("Session unlocking.");
                        _service?.Start();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        _logger.LogInfo("System suspending.");
                        _service?.Stop();
                        break;
                    case PowerModes.Resume:
                        _logger.LogInfo("System resuming.");
                        _service?.Start();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
    }
}