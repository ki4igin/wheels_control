using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using static WheelsControl;

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

    const int PORT_CMD = 2020;
    const int PORT_RTD = 2021;
    const int PORT_VIBR = 2022;

    const int MAX_PACKET = 2048;

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

    static readonly Channel<VibrPacket> vibrChannel = Channel.CreateUnbounded<VibrPacket>();
    static readonly Channel<RtdPacket> rtdChannel = Channel.CreateUnbounded<RtdPacket>();
    static readonly Channel<CmdPacket> cmdChannel = Channel.CreateUnbounded<CmdPacket>();
    static IPAddress deviceIp = IPAddress.Parse("192.168.0.10");

    static uint oldCnt;
    static uint errCnt;
    static uint totalCnt;

    static byte adcMask = 0x0F;

    public static async Task Main()
    {
        _ = Task.Run(ReceiveVibr);
        _ = Task.Run(ReceiveRtd);
        _ = Task.Run(ReceiveCmd);

        _ = Task.Run(() => CmdLoggerLoop());
        _ = Task.Run(() => WriterRtd());
        _ = Task.Run(() => WriterVibr());

        while (true)
        {
            var line = Console.ReadLine();
            if (line != null)
                await SendCommandAsync(line);
        }
    }

    static async Task ReceiveVibr()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 2022));

        while (true)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(2048);
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None);

            var pkt = ParseVibr(buffer.AsSpan(0, received));
            await vibrChannel.Writer.WriteAsync(pkt);

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task ReceiveRtd()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 2021));

        while (true)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(2048);
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None);

            var pkt = ParseRtd(buffer.AsSpan(0, received));
            await rtdChannel.Writer.WriteAsync(pkt);

            ArrayPool<byte>.Shared.Return(buffer);
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

    static VibrPacket ParseVibr(Span<byte> span)
    {
        var pkt = new VibrPacket
        {
            Counter = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4))
        };

        var dataSpan = span.Slice(8);

        for (int i = 0, j = 0; i < 8; i++)
        {
            if ((adcMask & (1 << i)) == 0)
                continue;

            int value = (dataSpan[j * 3] << 16) |
                        (dataSpan[j * 3 + 1] << 8) |
                         dataSpan[j * 3 + 2];
            pkt.Data[i] = value;
            j++;
        }
        return pkt;
    }

    static RtdPacket ParseRtd(Span<byte> span)
    {
        var pkt = new RtdPacket
        {
            Counter = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4))
        };

        var dataSpan = span.Slice(8);

        for (int i = 0; i < 8; i++)
        {
            pkt.Data[i] = span[i] >> 8;
        }
        return pkt;
    }

    static async Task CmdLoggerLoop(string fileName = "cmd.log")
    {
        var reader = cmdChannel.Reader;
        using var writer = new StreamWriter(fileName, append: true);

        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var cmd))
            {
                string line = $"[{cmd.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {(cmd.IsIncoming ? "IN " : "OUT")} {cmd.Message}";
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
    }

    static async Task WriterVibr()
    {
        using var bw = new BinaryWriter(File.Open("vibr.bin", FileMode.Append));
        var reader = vibrChannel.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var pkt))
            {
                bw.Write(pkt.Counter);
                foreach (var v in pkt.Data) bw.Write(v);
                if ((pkt.Counter % 1024) == 0)
                    PrintStats(pkt);
            }
        }
    }

    static async Task WriterRtd()
    {
        using var bw = new BinaryWriter(File.Open("rtd.bin", FileMode.Append));
        var reader = rtdChannel.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var pkt))
            {
                bw.Write(pkt.Counter);
                foreach (var v in pkt.Data) bw.Write(v);
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

    static void PrintStats(VibrPacket vibrPacket)
    {
        Console.Clear();

        Console.WriteLine($"Queue: {vibrChannel.Reader.Count}");
        Console.WriteLine($"Errors: {errCnt}");
        Console.WriteLine($"Packets: {oldCnt}");

        for (int i = 0; i < VIBR_ADC_CNT; i++)
            Console.WriteLine($"Vibr{i + 1}: {vibrPacket.Data[i]}");
    }
}