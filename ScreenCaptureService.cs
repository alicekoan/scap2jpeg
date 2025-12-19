namespace scap2jpeg
{
    internal class ScreenCaptureService
    {
        private enum CommandType { Start, Stop }

        private readonly Logger _logger;
        private readonly CancellationToken _token;
        private CancellationTokenSource? _currentTaskCts;
        private Task? _currentTask;
        private readonly Queue<CommandType> _commandQueue = new();
        private readonly SemaphoreSlim _commandSemaphore = new(0);
        private readonly object _queueLock = new();

        public ScreenCaptureService(Logger logger, CancellationToken token)
        {
            _logger = logger;
            _token = token;

            _ = ProcessCommandsAsync();
        }

        public void Start()
        {
            AddCommand(CommandType.Start);
        }

        public void Stop()
        {
            AddCommand(CommandType.Stop);
        }

        private void AddCommand(CommandType command)
        {
            if (_token.IsCancellationRequested) return;

            lock (_queueLock)
            {
                _commandQueue.Enqueue(command);
                _commandSemaphore.Release();
            }
        }

        private async Task ProcessCommandsAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    await _commandSemaphore.WaitAsync(_token);

                    CommandType? command;
                    lock (_queueLock)
                    {
                        if (_commandQueue.Count == 0) continue;
                        command = _commandQueue.Dequeue();
                    }
                    switch (command)
                    {
                        case CommandType.Start:
                            await StartCurrentTaskAsync();
                            break;
                        case CommandType.Stop:
                            await StopCurrentTaskAsync();
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ok
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                await StopCurrentTaskAsync();
            }
        }

        private async Task StartCurrentTaskAsync()
        {
            if (_token.IsCancellationRequested) return;

            if (_currentTask != null && !_currentTask.IsCompleted) return;

            _currentTaskCts = CancellationTokenSource.CreateLinkedTokenSource(_token);
            _currentTask = Capture(_currentTaskCts.Token);
        }

        private async Task StopCurrentTaskAsync()
        {
            if (_currentTask == null || _currentTask.IsCompleted) return;

            _currentTaskCts?.Cancel();

            try
            {
                await _currentTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ok
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
            finally
            {
                _currentTaskCts?.Dispose();
                _currentTaskCts = null;
                _currentTask = null;
            }
        }

        private async Task Capture(CancellationToken token)
        {
            const int STEP = 5000;
            const int MAX_DELAY = 60000;
            int delay = 5000;
            bool hasEnoughFreeSpace = HasEnoughFreeSpace();
            int interval = MAX_DELAY;

            using Shooter shooter = new(_logger);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    shooter.Start(token);
                    while (!token.IsCancellationRequested)
                    {
                        if (interval <= 0)
                        {
                            hasEnoughFreeSpace = HasEnoughFreeSpace();
                            interval = MAX_DELAY;
                        }
                        interval -= 1;

                        if (hasEnoughFreeSpace)
                        {
                            shooter.Capture(token);
                            delay = 1000;
                        }

                        await Task.Delay(1000, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ok
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex);
                    delay = Math.Min(delay + STEP, MAX_DELAY);
                    await Task.Delay(delay, token);
                }
            }
        }

        private static bool HasEnoughFreeSpace()
        {
            try
            {
                DriveInfo drive = new(Path.GetPathRoot(AppContext.BaseDirectory)!);
                if (drive.IsReady)
                {
                    double freeSpacePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;
                    return freeSpacePercent >= 5.0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}