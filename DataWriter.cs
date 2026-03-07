using System.Threading.Channels;

class DataWriter
{
    private readonly string _name;

    private FileStream? _fs;
    private volatile bool _recording;

    private readonly Channel<byte[]> _channel;

    public ChannelWriter<byte[]> Writer { get => _channel.Writer; }

    public DataWriter(string name)
    {
        _name = name;
        _channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(5000)
            {
                SingleReader = true,
                SingleWriter = true
            }); ;
    }

    public void StartRecording()
    {
        Directory.CreateDirectory("logs");

        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{_name}.bin";
        var path = Path.Combine("logs", name);

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        _recording = true;

        Console.WriteLine($"Recording {_name} -> {path}");
    }

    public void StopRecording()
    {
        _recording = false;
        _fs?.Dispose();
        _fs = null;
    }

    public async Task Run()
    {
        await foreach (var packet in _channel.Reader.ReadAllAsync())
        {
            if (_recording && _fs != null)
            {
                await _fs.WriteAsync(packet);
            }
        }
    }
}