using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

public class Program
{
    const int VIBR_ADC_CNT = 8;
    const int RTD_ADC_CNT = 8;

    const int PORT_CMD = 2020;
    const int PORT_RTD = 2021;
    const int PORT_VIBR = 2022;

    static readonly IPAddress deviceIp = IPAddress.Parse("192.168.50.42");

    static uint _errVibrCnt;
    static uint _errRtdCnt;
    static uint _vibrChMask = 0x3F;
    static uint _oldCntVibrPac = 0;
    static uint _oldCntRtdPac = 0;

    public static async Task Main()
    {
        var vibrChannel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(5000)
            {
                SingleReader = true,
                SingleWriter = true
            });
        var rtdChannel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(5000)
            {
                SingleReader = true,
                SingleWriter = true
            });

        DataWriter vibrWriter = new("vibr");
        DataWriter rtdWriter = new("rtd");

        var cli = new ConsoleInput([
            "connect",
            "vibr_on",
            "rtd_on",
            "off",
            "reset",
            "ping",
            "vibr_set_div",
            "vibr_set_ch",
            "exit",
            "quit",
            ]);

        _ = Task.Run(() => ReceiveData(PORT_VIBR, vibrChannel.Writer));
        _ = Task.Run(() => ReceiveData(PORT_RTD, rtdChannel.Writer));
        _ = Task.Run(() => ParseTask(vibrChannel.Reader, vibrWriter.Writer, 256, ParseVibr));
        _ = Task.Run(() => ParseTask(rtdChannel.Reader, rtdWriter.Writer, 1, ParseRtd));
        _ = Task.Run(rtdWriter.Run);
        _ = Task.Run(vibrWriter.Run);
        _ = Task.Run(ReceiveCmd);

        while (true)
        {
            lock (ConsoleSync.Lock)
            {
                Console.SetCursorPosition(0, 0);
                Console.Write("\r\x1b[2K> ");
            }
            var input = cli.ReadLine();

            if (input is not null)
            {
                if (string.IsNullOrWhiteSpace(input))
                    continue;
                string[] inputParts = input
                    .Trim()
                    .ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (inputParts.Length == 0)
                    continue;
                if (inputParts[0] == "exit" || inputParts[0] == "quit")
                    break;

                var (cmd, arg) = ParseInput(inputParts);
                if (cmd != string.Empty)
                {
                    switch (cmd)
                    {
                        case "cmd2":
                            _errVibrCnt = 0;
                            _oldCntVibrPac = 0;
                            vibrWriter.StartRecording();
                            break;
                        case "cmd4":
                            _errRtdCnt = 0;
                            _oldCntRtdPac = 0;
                            rtdWriter.StartRecording();
                            break;
                        case "cmd3":
                            vibrWriter.StopRecording();
                            rtdWriter.StopRecording();
                            break;
                        case "cmd5":
                            if (arg < 1 || arg > 5)
                            {
                                Logger.LogInfo("Аргумент должен быть одним из: 1, 2, 3, 4, 5");
                                continue;
                            }
                            break;
                        case "cmd6":
                            if (arg > 0xFF || arg < 1)
                            {
                                Logger.LogInfo("Аргумент должен быть в диапазоне 1-255");
                                continue;
                            }
                            _vibrChMask = (uint)arg;
                            break;
                        case "cmdr":
                            _vibrChMask = 0x3F;
                            break;
                        default:
                            break;
                    }

                    var (pac, logMsg) = cmd switch
                    {
                        "cmd5" or "cmd6" => (
                           CreatePac(cmd, arg),
                           $"{cmd} {inputParts[1]}"),
                        _ => (
                            CreatePac(cmd),
                            cmd)
                    };

                    await SendCommandAsync(pac, pac.Length);
                    Logger.LogRequest(logMsg);
                }
            }
        }
    }

    static byte[] CreatePac(string cmd, int arg)
    {
        byte[] pac = new byte[8];
        Encoding.ASCII.GetBytes(cmd, 0, 4, pac, 0);
        BinaryPrimitives.WriteInt32LittleEndian(pac.AsSpan(4, 4), arg);
        return pac;
    }

    static byte[] CreatePac(string cmd)
    {
        return Encoding.ASCII.GetBytes(cmd);
    }

    static void ReceiveData(int port, ChannelWriter<byte[]> writer)
    {
        using UdpClient udp = new(port);
        udp.Client.ReceiveBufferSize = 16 * 1024 * 1024;
        IPEndPoint ep = new(IPAddress.Any, port);
        while (true)
        {
            var data = udp.Receive(ref ep);
            writer.TryWrite(data);
        }
    }

    static void ReceiveCmd()
    {
        using UdpClient udp = new(PORT_CMD);
        IPEndPoint ep = new(IPAddress.Any, PORT_CMD);

        while (true)
        {
            var data = udp.Receive(ref ep);
            if (data.Length < 4)
            {
                Logger.LogInfo($"Неверный ответ: {BitConverter.ToString(data)}");
                continue;
            }
            string cmd = Encoding.ASCII.GetString(data, 0, 4);
            uint val = data.Length switch
            {
                8 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)),
                _ => 0
            };
            string mes = $"{cmd} {val}";
            Logger.LogResponse(mes);
        }
    }

    static async Task ParseTask(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer, uint decimator, Action<byte[], uint> parser)
    {
        uint cnt = 0;
        uint mask = decimator - 1;
        await foreach (var packet in reader.ReadAllAsync())
        {
            writer.TryWrite(packet);
            if ((++cnt & mask) == 0)
            {
                parser(packet, decimator);
            }
        }
    }

    static void ParseVibr(byte[] data, uint decimator)
    {
        Span<byte> span = data;
        uint cntPac = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

        uint diff = cntPac - _oldCntVibrPac;
        if (diff > decimator)
        {
            _errVibrCnt++;
        }
        _oldCntVibrPac = cntPac;

        var dataSpan = span[8..];
        Span<int> adcData = stackalloc int[8];
        for (int i = 0, j = 0; i < 8; i++)
        {
            if ((_vibrChMask & (1 << i)) == 0)
                continue;

            adcData[i] = ReadInt24BigEndian(dataSpan.Slice(j * 3, 3));
            j++;
        }
        PrintVibr(_errVibrCnt, cntPac, adcData);

    }

    static void ParseRtd(byte[] data, uint decimator)
    {
        Span<byte> span = data;
        uint cntPac = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

        uint diff = cntPac - _oldCntRtdPac;
        if (diff > decimator)
        {
            _errRtdCnt++;
        }
        _oldCntRtdPac = cntPac;
        var dataSpan = span[8..];
        Span<int> adcData = stackalloc int[8]; ;
        for (int i = 0; i < 8; i++)
        {
            adcData[i] = ReadInt24BigEndian(dataSpan[(i * 4)..]);
        }
        PrintRtd(_errRtdCnt, cntPac, adcData);
    }

    static (string, int) ParseInput(string[] cmdParts)
    {
        if (cmdParts.Length > 2)
        {
            Logger.LogInfo("Слишком много аргументов. Формат: 'cmd [arg]'");
            return (string.Empty, 0);
        }

        string cmd = cmdParts[0] switch
        {
            "connect" => "cmd0",
            "vibr_on" => "cmd2",
            "rtd_on" => "cmd4",
            "off" => "cmd3",
            "vibr_set_div" => "cmd5",
            "vibr_set_ch" => "cmd6",
            "ping" => "cmd9",
            "reset" => "cmdr",
            _ => cmdParts[0]
        };

        if (cmd.Length != 4)
        {
            Logger.LogInfo("Команда должна быть 4 символа, например 'cmd0'");
            return (string.Empty, 0);
        }
        int arg = 0;
        if (cmdParts.Length == 2)
        {
            string argStr = cmdParts[1].Replace("_", "");

            try
            {
                if (argStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    arg = Convert.ToInt32(argStr[2..], 16); // hex
                else if (argStr.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    arg = Convert.ToInt32(argStr[2..], 2); // binary
                else
                    arg = Convert.ToInt32(argStr, 10); // decimal
            }
            catch
            {
                Logger.LogInfo("Аргумент должен быть числом (дес/hex/bin)");
                return (string.Empty, 0);
            }
        }
        return (cmd, arg);
    }

    static async Task SendCommandAsync(byte[] pac, int length)
    {
        using var udp = new UdpClient();
        IPEndPoint ep = new(deviceIp, PORT_CMD);
        await udp.SendAsync(pac, length, ep);
    }

    static void PrintVibr(uint errCnt, uint cntPac, ReadOnlySpan<int> adcData)
    {
        lock (ConsoleSync.Lock)
        {
            var (left, top) = Console.GetCursorPosition();

            Console.CursorVisible = false;

            Console.CursorTop = 6;
            Console.WriteLine($"\r\x1b[2KErrors  : {errCnt}");
            Console.WriteLine($"\r\x1b[2KPackets : {cntPac}");
            for (int i = 0; i < VIBR_ADC_CNT; i++)
                Console.WriteLine($"\r\x1b[2KVibr{i + 1}   : {adcData[i]}");

            Console.SetCursorPosition(left, 0);
            Console.CursorVisible = true;
        }
    }

    static void PrintRtd(uint errCnt, uint cntPac, ReadOnlySpan<int> adcData)
    {
        lock (ConsoleSync.Lock)
        {
            var (left, top) = Console.GetCursorPosition();
            Console.CursorVisible = false;
            Console.CursorTop = 17;

            Console.WriteLine($"\r\x1b[2KErrors  : {errCnt}");
            Console.WriteLine($"\r\x1b[2KPackets : {cntPac}");

            for (int i = 0; i < RTD_ADC_CNT; i++)
                Console.WriteLine($"\r\x1b[2KRTD{i + 1}    : {Rtd100.AdcDataToTemperature(adcData[i]):F2}");

            Console.SetCursorPosition(left, 0);
            Console.CursorVisible = true;
        }
    }

    static int ReadInt24BigEndian(ReadOnlySpan<byte> span)
    {
        int val = (span[0] << 24) | (span[1] << 16) | (span[2] << 8);
        return val >> 8;
    }
}
