using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;



public struct VibrPacket
{

    private uint id;
    public uint cnt;
    public int[] data;

    public VibrPacket(uint adcCnt)
    {
        id = 1;
        cnt = 0;
        data = new int[adcCnt];
    }
}

public class WheelsControl
{

    const int VIBR_ADC_CNT = 8;

    const int PORT_CMD = 2020;
    const int PORT_RTD = 2021;
    const int PORT_VIBR = 2022;

    static VibrPacket vibrPacket = new VibrPacket(VIBR_ADC_CNT);

    static int[] vibrData = new int[VIBR_ADC_CNT];

    static UdpClient udpClientCmd;
    static UdpClient udpClientRtd;
    static UdpClient udpClientVibr;

    static IPEndPoint groupEPCmd;
    static IPEndPoint groupEPRtd;
    static IPEndPoint groupEPVibr;
    static IPAddress ipAdd;

    static uint[] vibrAdc = new uint[8];
    static uint vibrCnt = 0;

    public static void Main()
    {
        udpClientCmd = new UdpClient(PORT_CMD);
        udpClientRtd = new UdpClient(PORT_RTD);
        udpClientVibr = new UdpClient(PORT_VIBR);


        byte[] ipDefault = { 192, 168, 0, 10 };

        ipAdd = new(ipDefault);
        groupEPCmd = new IPEndPoint(ipAdd, PORT_CMD);
        groupEPRtd = new IPEndPoint(ipAdd, PORT_RTD);
        groupEPVibr = new IPEndPoint(ipAdd, PORT_VIBR);
        //udpClientCmd.Connect(ipAdd, PORT_CMD);


        Task.Run(() => ReceiveProcessCmd());
        Task.Run(() => ReceiveProcessVibr());
        Task.Run(() => ReceiveProcessRtd());
        Task.Run(() => ParseProcess());

        while (true)
        {
            var str = Console.ReadLine();
            Send(str!);
        }
    }

    public static void Send(string str)
    {
        if (str.StartsWith("cmd7"))
        {
            string ipStr = str.Substring(4).Trim();
            IPAddress ip = IPAddress.Parse(ipStr);
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] bytes4 = Encoding.ASCII.GetBytes("cmd7").Concat(ipBytes).ToArray();
            udpClientCmd.Send(bytes4);
            udpClientCmd.Close();

            udpClientCmd = new UdpClient();
            ipAdd = ip;
            groupEPCmd = new IPEndPoint(ipAdd, 0);
            udpClientCmd.Connect(ipAdd, PORT_CMD);

            return;

        }

        if (str.StartsWith("cmd5"))
        {
            string dataStr = str.Substring(4).TrimStart();
            var dataInt = Convert.ToUInt32(dataStr);
            var dataByte = BitConverter.GetBytes(dataInt);
            byte[] bytes4 = Encoding.ASCII.GetBytes("cmd5").Concat(dataByte).ToArray();
            udpClientCmd.Send(bytes4);
            return;
        }

        if (str.StartsWith("cmd6"))
        {
            string dataStr = str.Substring(4).TrimStart();
            var dataInt = Convert.ToUInt32(dataStr, 2);
            var dataByte = BitConverter.GetBytes(dataInt);
            byte[] bytes4 = Encoding.ASCII.GetBytes("cmd6").Concat(dataByte).ToArray();
            udpClientCmd.Send(bytes4);
            return;
        }


        byte[] bytes = Encoding.ASCII.GetBytes(str);
        udpClientCmd.Send(bytes, bytes.Length, groupEPCmd);
    }

    static ConcurrentQueue<byte[]> fifo = new();

    public static void ReceiveProcessCmd()
    {

        while (true)
        {
            byte[] bytes = udpClientCmd.Receive(ref groupEPCmd);
            groupEPCmd = new IPEndPoint(ipAdd, PORT_CMD);
            fifo.Enqueue(bytes);
        }
    }

    public static void ReceiveProcessVibr()
    {

        while (true)
        {
            byte[] bytes = udpClientVibr.Receive(ref groupEPVibr);
            fifo.Enqueue(bytes);
        }
    }

    public static void ReceiveProcessRtd()
    {

        while (true)
        {
            byte[] bytes = udpClientRtd.Receive(ref groupEPRtd);
            fifo.Enqueue(bytes);
        }
    }

    public static void ParseProcess()
    {
        uint oldCnt = 0;
        uint cntErr = 0;
        uint cnt = 0;
        while (true)
        {
            if (fifo.TryDequeue(out byte[]? bytes))
            {
                uint id = BitConverter.ToUInt32(bytes, 0);
                char[] empty = Enumerable.Repeat(' ', Console.WindowWidth - 1).ToArray();
                if (id == 0x01)
                {
                    ParsePacketVibr(bytes);
                    if (vibrPacket.cnt - oldCnt > 1)
                    {
                        cntErr++;
                    }
                    oldCnt = vibrPacket.cnt;

                    if (++cnt % 256 == 0)
                    {
                        Console.CursorVisible = false;
                        int cursorPosLeft = Console.CursorLeft;
                        int cursorPosTop = Console.CursorTop;

                        Console.WriteLine();
                        Console.Write(empty);
                        Console.WriteLine();
                        Console.Write(empty);
                        Console.WriteLine();
                        Console.Write(empty);
                        Console.Write("\r");
                        Console.WriteLine($"Queue len {fifo.Count}");
                        Console.WriteLine($"Err cnt: {cntErr}");
                        Console.WriteLine($"Cnt packet: {oldCnt}");
                        for (int i = 0; i < VIBR_ADC_CNT; i++)
                        {
                            Console.Write(empty);
                            Console.Write("\r");
                            Console.WriteLine($"Vibr{i + 1}: {vibrPacket.data[i]}");
                        }
                        Console.CursorTop = cursorPosTop;
                        Console.CursorLeft = cursorPosLeft;
                        Console.CursorVisible = true;
                    }
                }
                else if (id == 0x02)
                {

                }
                else
                {
                    Console.Write(empty);
                    Console.Write("\r");
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"Response: ");
                    Console.ResetColor();
                    Console.WriteLine($"{Encoding.ASCII.GetString(bytes)}");
                    Console.Write(empty);
                    Console.Write("\r");
                }
            }
            else
            {
                Thread.Sleep(10);
            }
        }

    }

    public static void ParsePacketVibr(byte[] packet)
    {
        vibrPacket.cnt = BitConverter.ToUInt32(packet, 4);

        byte[] data = new byte[packet.Length - 8];
        Array.Copy(packet, 8, data, 0, packet.Length - 8);

        for (int i = 0; i < data.Length / 24; i += 3)
        {
            byte[] temp = new byte[4] { 0, data[i + 2], data[i + 1], data[i] };
            vibrPacket.data[i / 3] = BitConverter.ToInt32(temp, 0) / 256;
        }
    }


}

public class MovingAverage
{
    private Queue<double> samples = new Queue<double>();
    private int _windowSize = 16;
    private double sampleAccumulator;

    public MovingAverage(int windowSize)
    {
        _windowSize = windowSize;
    }

    /// <summary>
    /// Computes a new windowed average each time a new sample arrives
    /// </summary>
    /// <param name="newSample"></param>
    public double Calc(double newSample)
    {
        sampleAccumulator += newSample;
        samples.Enqueue(newSample);

        if (samples.Count > _windowSize)
        {
            sampleAccumulator -= samples.Dequeue();
        }

        return sampleAccumulator / samples.Count;
    }
}
