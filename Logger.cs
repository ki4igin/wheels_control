using System.Threading.Channels;

public static class Logger
{
    private static readonly Channel<LogMessage> _channel;
    private static readonly Task _processorTask;
    private static readonly string _logFile;

    private const int RequestLine = 2;
    private const int ResponseLine = 3;
    private const int InfoLine = 4;

    static Logger()
    {
        Directory.CreateDirectory("logs");
        _logFile = Path.Combine("logs", $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

        _channel = Channel.CreateUnbounded<LogMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        _processorTask = ProcessLogs();
    }

    public static void LogRequest(string message) =>
        _channel.Writer.TryWrite(new LogMessage(LogType.Request, message));

    public static void LogResponse(string message) =>
        _channel.Writer.TryWrite(new LogMessage(LogType.Response, message));

    public static void LogInfo(string message) =>
        _channel.Writer.TryWrite(new LogMessage(LogType.Info, message));

    private static async Task ProcessLogs()
    {
        await using var writer = new StreamWriter(_logFile, true);

        await foreach (var log in _channel.Reader.ReadAllAsync())
        {
            string time = DateTime.Now.ToString("HH:mm:ss");

            string typeStr = log.Type switch
            {
                LogType.Request => "REQ ",
                LogType.Response => "RESP",
                LogType.Info => "INFO",
                _ => "INFO"
            };

            string line = $"[{time}] [{typeStr}] {log.Message}";

            await writer.WriteLineAsync(line);
            await writer.FlushAsync();

            int consoleLine = log.Type switch
            {
                LogType.Request => RequestLine,
                LogType.Response => ResponseLine,
                LogType.Info => InfoLine,
                _ => InfoLine
            };

            WriteConsole(consoleLine, line);
        }
    }

    private static void WriteConsole(int line, string text)
    {
        lock (ConsoleSync.Lock)
        {
            var (left, top) = Console.GetCursorPosition();
            if (text.Length > Console.WindowWidth - 1)
                text = text[..(Console.WindowWidth - 1)];

            Console.SetCursorPosition(0, line);
            Console.Write($"\r\x1b[2K{text}");
            Console.SetCursorPosition(left, 0);
        }
    }

    private record LogMessage(LogType Type, string Message);

    private enum LogType
    {
        Request,
        Response,
        Info
    }
}
