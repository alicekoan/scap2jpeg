namespace scap2jpeg
{
    internal class Logger
    {
        private const string LOG_FILENAME = "scap2jpeg.log";

        private readonly string _logPath;
        private readonly object _lockObject = new();

        public Logger()
        {
            try
            {
                _logPath = Path.Combine(AppContext.BaseDirectory, LOG_FILENAME);
            }
            catch
            {
                _logPath = LOG_FILENAME;
            }
        }

        public void LogError(Exception ex)
        {
            try
            {
                string message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ERROR: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";
                LogMessage(message);
            }
            catch
            {
                // ok
            }
        }

        public void LogInfo(string message)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} INFO: {message}{Environment.NewLine}";
                LogMessage(logMessage);
            }
            catch
            {
                // ok
            }
        }

        private void LogMessage(string message)
        {
            lock (_lockObject)
            {
                File.AppendAllText(_logPath, message);
            }
        }
    }
}