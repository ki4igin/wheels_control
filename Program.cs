using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

public class VibrPacket(int adc)
{
    public uint Counter;
    public int[] Data = new int[adc];
}

public struct Packet
{
    public byte[] Buffer;
    public int Length;
}

public class WheelsControl
{
    const int VIBR_ADC_CNT = 8;
    const int RTD_ADC_CNT = 8;

    const int PORT_CMD = 2020;
    const int PORT_RTD = 2021;
    const int PORT_VIBR = 2022;

    const int MAX_PACKET = 2048;

    const string emptyString = "\r                              \r";

    public sealed class VibrPacket
    {
        public uint Counter;
        public readonly int[] Data = new int[8];
    }

    public sealed class RtdPacket
    {
        public uint Counter;
        public readonly int[] Data = new int[8];
    }

    public sealed class CmdPacket
    {
        public DateTime Timestamp;
        public required string Message;
        public bool IsIncoming; // true = пришла, false = отправлена
    }

    static IPAddress deviceIp = IPAddress.Parse("192.168.0.10");

    static uint errVibrCnt;
    static uint errRtdCnt;

    static byte vibrChMask = 0x3F;

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


        _ = Task.Run(() => ReceiveData(PORT_VIBR, vibrChannel.Writer));
        _ = Task.Run(() => ReceiveData(PORT_RTD, rtdChannel.Writer));
        _ = Task.Run(() => ParseTask(vibrChannel.Reader, vibrWriter.Writer, 256, ParseVibr));
        _ = Task.Run(() => ParseTask(rtdChannel.Reader, rtdWriter.Writer, 1, ParseRtd));
        _ = Task.Run(ReceiveCmd);

        _ = Task.Run(() => CmdLoggerLoop());

        while (true)
        {
            var line = Console.ReadLine();
            if (line != null)
            {
                Console.CursorTop = 0;
                Console.CursorLeft = 0;
                Console.Write(emptyString);
                await SendCommandAsync(line);
            }

        }
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

    static async Task ReceiveCmd()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 2020));

        while (true)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None);

            var msg = Encoding.ASCII.GetString(buffer, 0, received);

            await cmdChannel.Writer.WriteAsync(new CmdPacket
            {
                Timestamp = DateTime.Now,
                Message = msg,
                IsIncoming = true
            });

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task ParseTask(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer, uint decimator, Action<byte[], uint> parser)
    {
        uint cnt = 0;
        uint mask = decimator - 1;
        await foreach (var packet in reader.ReadAllAsync())
        {
            writer.TryWrite(packet);
            if ((cnt & mask) == 0)
            {
                parser(packet, decimator);
            }
        }
    }

    static uint oldCntVibrPac = 0;
    static uint oldCntRtdPac = 0;

    static void ParseVibr(byte[] data, uint decimator)
    {
        Span<byte> span = data;
        uint cntPac = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        
        uint diff = cntPac - oldCntVibrPac;
        if (diff > decimator)
        {
            errVibrCnt++;
        }
        oldCntVibrPac = cntPac;

        var dataSpan = span.Slice(8);
        int[] vibrData = new int[8];
        for (int i = 0, j = 0; i < 8; i++)
        {
            if ((vibrChMask & (1 << i)) == 0)
                continue;

            vibrData[i] = ReadInt24BigEndian(dataSpan.Slice(j * 3, 3));
            j++;
        }
        PrintVibr(errVibrCnt, cntPac, vibrData.AsSpan());

    }

    static void ParseRtd(byte[] data, uint decimator)
    {
        Span<byte> span = data;
        uint cntPac = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

        uint diff = cntPac - oldCntRtdPac;
        if (diff > decimator)
        {
            errRtdCnt++;
        }
        oldCntRtdPac = cntPac;
        var dataSpan = span.Slice(8);
        int[] rtdData = new int[8];
        for (int i = 0; i < 8; i++)
        {
            rtdData[i] = BinaryPrimitives.ReadInt32BigEndian(dataSpan.Slice(i * 4));
        }
        PrintRtd(errRtdCnt, cntPac, rtdData.AsSpan());
    }

    static async Task CmdLoggerLoop(string fileName = "cmd.log")
    {
        var reader = cmdChannel.Reader;
        using var writer = new StreamWriter(fileName, append: true);

        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var cmd))
            {
                string line = $"[{cmd.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {(cmd.IsIncoming ? "RESP: " : "REQ : ")} {cmd.Message}";
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
    }

    static async Task SendCommandAsync(string cmdStr)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // создаём локальный буфер 8 байт
        var buf = new byte[8];

        // cmd7 — смена IP устройства
        if (cmdStr.StartsWith("cmd7"))
        {
            deviceIp = IPAddress.Parse(cmdStr[4..].Trim());
            Encoding.ASCII.GetBytes("cmd7").CopyTo(buf, 0);
            deviceIp.GetAddressBytes().CopyTo(buf, 4);

            socket.SendTo(buf, new IPEndPoint(deviceIp, 2020));
        }
        else if (cmdStr.StartsWith("cmd5"))
        {
            uint val = Convert.ToUInt32(cmdStr[4..].Trim());
            Encoding.ASCII.GetBytes("cmd5").CopyTo(buf, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), val);

            socket.SendTo(buf, new IPEndPoint(deviceIp, 2020));
        }
        else if (cmdStr.StartsWith("cmd6"))
        {
            uint val = Convert.ToUInt32(cmdStr[4..].Trim(), 2);
            Encoding.ASCII.GetBytes("cmd6").CopyTo(buf, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), val);

            socket.SendTo(buf, new IPEndPoint(deviceIp, 2020));
        }
        else if (cmdStr.StartsWith("cmd2"))
        {
            oldCntVibrPac = 0;
            oldCntRtdPac = 0;
            errVibrCnt = 0;
            errRtdCnt = 0;
            var data = Encoding.ASCII.GetBytes(cmdStr);
            socket.SendTo(data, new IPEndPoint(deviceIp, 2020));
        }
        else
        {
            // если обычная строка
            var data = Encoding.ASCII.GetBytes(cmdStr);
            socket.SendTo(data, new IPEndPoint(deviceIp, 2020));
        }

        // логируем отправку команды
        await cmdChannel.Writer.WriteAsync(new CmdPacket
        {
            Timestamp = DateTime.Now,
            Message = cmdStr,
            IsIncoming = false
        });
    }

    private static readonly object consoleLock = new object();
    static void PrintVibr(uint errCnt, uint cntPac, ReadOnlySpan<int> data)
    {
        //Console.Clear();
        lock (consoleLock)
        {
            int cursorPosLeft = Console.CursorLeft;
            int cursorPosTop = Console.CursorTop;

            Console.CursorVisible = false;

            Console.CursorTop = 3;
            Console.WriteLine($"{emptyString}Errors  : {errCnt}");
            Console.WriteLine($"{emptyString}Packets : {cntPac}");
            for (int i = 0; i < VIBR_ADC_CNT; i++)
                Console.WriteLine($"{emptyString}Vibr{i + 1}: {data[i]}");

            Console.CursorLeft = cursorPosLeft;
            Console.CursorTop = cursorPosTop;
            Console.CursorVisible = true;
        }
    }

    static void PrintRtd(uint errCnt, uint cntPac, ReadOnlySpan<int> data)
    {
        //Console.Clear();
        lock (consoleLock)
        {
            int cursorPosLeft = Console.CursorLeft;
            int cursorPosTop = Console.CursorTop;

            Console.CursorVisible = false;

            Console.CursorTop = 14;

            Console.WriteLine();
            Console.WriteLine($"{emptyString}Errors  : {errCnt}");
            Console.WriteLine($"{emptyString}Packets : {cntPac}");

            for (int i = 0; i < RTD_ADC_CNT; i++)
                Console.WriteLine($"{emptyString}RTD{i + 1} : {AdcData2temperatyre(data[i])}");

            Console.CursorLeft = cursorPosLeft;
            Console.CursorTop = cursorPosTop;
            Console.CursorVisible = true;
        }
    }

    static double AdcData2temperatyre(int adcData)
    {
        double alpha = 0.00385;
        double R0 = 100;
        double Gain = 16;
        double Rref = 1.62e3;

        double temperature = 1 / (alpha * R0) * (adcData / 256 / ((1U << 22) * Gain) * Rref - R0);
        return temperature;
    }

    static int ReadInt24BigEndian(ReadOnlySpan<byte> span)
    {
        return (span[0] << 16) | (span[1] << 8) | span[2];
    }
}


