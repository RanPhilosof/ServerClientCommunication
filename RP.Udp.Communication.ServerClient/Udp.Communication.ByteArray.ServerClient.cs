using RP.Communication.ServerClient.Interface;
using RP.Infra.TypeConverters;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

#if NET6_0
    using Int128 = System.Int64;
#endif

namespace Udp.Communication.ByteArray.ServerClient;

public enum MessageFlags
{
    Unreliable,
    MediumEffort,
    BestEffort,
    Reliable,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UdpMessageHeader
{
    public static readonly int Size = Marshal.SizeOf(typeof(UdpMessageHeader));

    public Int32    HeaderSize;
    public Int32    DataSize;
    public Guid     ServerClientId;
    public UInt32   MessageId;
    public UInt16   PacketNumber;
    public UInt16   TotalPackets;
    public int      MaxMtu;
    public UInt32   DataStartIndex;
    public UInt32   DataEndIndex;
    public bool     IsKeepAlive;

    public static void ToBytes(ref UdpMessageHeader structure, byte[] destinationArray)
    {
        structure.HeaderSize = Size;

        IntPtr ptr = Marshal.AllocHGlobal(Size);
        Marshal.StructureToPtr(structure, ptr, true);
        Marshal.Copy(ptr, destinationArray, 0, Size);
        Marshal.FreeHGlobal(ptr);        
    }

    public static UdpMessageHeader FromBytes(byte[] sourceArray)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Size);
        Marshal.Copy(sourceArray, 0, ptr, Size);
        UdpMessageHeader structure = Marshal.PtrToStructure<UdpMessageHeader>(ptr);
        Marshal.FreeHGlobal(ptr);

        return structure;
    }
}

public class ClientData
{
    public ClientBuffer ClientBuffer = new ClientBuffer();
    public ClientInfo ClientInfo = new ClientInfo();
}

public struct PacketInfo
{
    public bool Received;
    public int StartIndex;
    public int EndIndex;
}

public class ClientBuffer
{
    public uint? MessageId;
    public int TotalPackets;
    public int ActualPacketsReceived;
    public int ActualBytesReceived;

    public PacketInfo[] PacketsNumberReceived = new PacketInfo[50_000];
    public byte[] DataReadyToSent = new byte[10 * 1024 * 1024];

    public void CleanMarkers()
    {
        MessageId = null;
        ActualBytesReceived = 0;
        ActualPacketsReceived = 0;

        for (int i = 0; i < TotalPackets; i++)
            PacketsNumberReceived[i].Received = false;
    }

    public void MarkPacketReceived(int packetNumber, int bytes)
    {
        if (!PacketsNumberReceived[packetNumber].Received)
        {
            PacketsNumberReceived[packetNumber].Received = true;
            ActualPacketsReceived++;
            ActualBytesReceived += bytes;
        }
    }

    public bool IsMessageCompleted()
    {
        if (TotalPackets != ActualPacketsReceived)
            return false;

        //for (int i = 0; i < TotalPackets; i++)
        //    if (!PacketsNumberReceived[i].Received)
        //        return false;

        return true;

    }
}

public class UdpByteArrayServer : IServerCommunication
{
    private readonly Socket _udpSocket;
    private readonly ConcurrentDictionary<Guid, ClientData> _clientDatas = new();

    private readonly int _serverMtu;
    private readonly Guid _serverId;
    private readonly int _keepAliveTimeout;
    private          byte[] _receiveBuffer;
    private readonly byte[] _sendBuffer; // Pre-allocated send buffer
    private          byte[] _sendBufferForDebug;
    private readonly int _maxClients;
    private readonly int _port;

    public event Action<Int128> OnNewClient;
    public event Action<Int128, byte[], long> OnNewClientMessage;

    public event Action<IPEndPoint> ClientConnected;
    public event Action<IPEndPoint> ClientDisconnected;
    public event Action<IPEndPoint, byte[], int> MessageReceived;

    public bool isDisposed = false;

    public UdpByteArrayServer(int port, int serverMtu, int keepAliveTimeout, int bufferSize, int maxClients)
    {
        _serverMtu = serverMtu;
        _keepAliveTimeout = keepAliveTimeout;
        _receiveBuffer = new byte[bufferSize];
        _sendBuffer = new byte[64 * 1024 * 1024]; // Max Udp Size
        _sendBufferForDebug = new byte[bufferSize];
        _serverId = Guid.NewGuid();

        _maxClients = maxClients;

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        _udpSocket.ReceiveBufferSize = 10 * 1024 * 1024;
        _udpSocket.SendBufferSize = 10 * 1024 * 1024;

        _udpSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        _port = port;

        Task.Run(CheckTimeouts);
    }

    public void Init(long maxMessageSize)
    {
        _receiveBuffer = new byte[maxMessageSize];
        _sendBufferForDebug = new byte[maxMessageSize];
        StartAsync();
    }

    public async Task StartAsync()
    {
        //Console.WriteLine($"Server started with ID {_serverId}");
        await Task.Run(ReceiveLoop);
    }

    public int ResolveMtu(int clientMtu)
    {
        return clientMtu;
    }

    private static uint messageIdGenerator = 0;
     
    public void SendMessage(Guid clientId, byte[] data, int length) //, MessageFlags messageFlags)
    {
        if (!_clientDatas.TryGetValue(clientId, out ClientData clientData))
            return;

        if (clientData.ClientInfo.EndPoint == null)
            return;

        var msgId = Interlocked.Increment(ref messageIdGenerator);

        int mtu = _serverMtu;

        if (clientData.ClientInfo.Mtu.HasValue)
            mtu = ResolveMtu(clientData.ClientInfo.Mtu.Value);

        int maxPayloadSize = mtu - UdpMessageHeader.Size;

        int totalPackets = (int)Math.Ceiling((double)length / maxPayloadSize);        
        int remainingLength = length;
        uint startIndex = 0, endIndex = 0;

        int sendMessageMultipleTimesCount = 1; // messageFlags == MessageFlags.Unreliable
        
        //if (messageFlags == MessageFlags.MediumEffort)
        //    sendMessageMultipleTimesCount = 2;
        //else if (messageFlags == MessageFlags.BestEffort)
        //    sendMessageMultipleTimesCount = 3;

        for (int j=0; j< sendMessageMultipleTimesCount; j++)
        {
            for (int i = 0; i < totalPackets; i++)
            {
                int bytesToSend = Math.Min(remainingLength, maxPayloadSize);
                remainingLength -= bytesToSend;
                endIndex = (uint)(startIndex + bytesToSend - 1);

                var udpMessageHeader = new UdpMessageHeader();
                udpMessageHeader.MaxMtu = mtu;
                udpMessageHeader.ServerClientId = _serverId;
                udpMessageHeader.TotalPackets = (UInt16)totalPackets;
                udpMessageHeader.PacketNumber = (UInt16)i;
                udpMessageHeader.DataStartIndex = startIndex;
                udpMessageHeader.DataEndIndex = endIndex;
                udpMessageHeader.MessageId = msgId;
                udpMessageHeader.DataSize = bytesToSend;

                UdpMessageHeader.ToBytes(ref udpMessageHeader, _sendBuffer);
                Buffer.BlockCopy(data, (int)startIndex, _sendBuffer, udpMessageHeader.HeaderSize, bytesToSend);
                Buffer.BlockCopy(data, (int)startIndex, _sendBufferForDebug, (int)udpMessageHeader.DataStartIndex, bytesToSend);

                _udpSocket.SendTo(_sendBuffer, udpMessageHeader.HeaderSize + bytesToSend, SocketFlags.None, clientData.ClientInfo.EndPoint);

                startIndex += (uint)bytesToSend;
            }
        }

        //if (messageFlags == MessageFlags.BestEffort)
        //{
        //    // Wait For Response.  
        //}
    }

    public void SendDataToClient(Int128 clientId, byte[] data, long count)
    {
        SendMessage(GuidInt128Converter.Int128ToGuid(clientId), data, (int) count);
    }

    public List<Int128> GetClients()
    {
        return GetConnectedClients().Select(x => GuidInt128Converter.GuidToInt128(x)).ToList();
    }
    public List<Guid> GetConnectedClients()
    {
        return _clientDatas.Keys.ToList();
    }

    private void ReceiveLoop()
    {
        EndPoint remoteEndPoint = _udpSocket.LocalEndPoint; // new IPEndPoint(IPAddress.Any, 0); // _port); // 0);
        while (!isDisposed)
        {
            int receivedBytes = _udpSocket.ReceiveFrom(_receiveBuffer, ref remoteEndPoint);
            ProcessMessage((IPEndPoint)remoteEndPoint, receivedBytes);
        }
    }

    private void ProcessMessage(IPEndPoint clientEndPoint, int receivedBytes)
    {
        if (receivedBytes < UdpMessageHeader.Size)
        {
            Console.WriteLine($"Message With Low Than Header Bytes Skipping! {receivedBytes}");
            return;
        }        

        bool messageReady = false;

        var udpMessageHeader = UdpMessageHeader.FromBytes(_receiveBuffer);

        if (receivedBytes != udpMessageHeader.DataSize + udpMessageHeader.HeaderSize)
        {
            Console.WriteLine($"Message Currupted Actual Bytes {receivedBytes} Expected Bytes {udpMessageHeader.DataSize + udpMessageHeader.HeaderSize}");
            return;
        }

        Console.WriteLine($"[Server] - Msg Id {udpMessageHeader.MessageId} - Packet {udpMessageHeader.PacketNumber} - Bytes {receivedBytes} : {udpMessageHeader.DataEndIndex - udpMessageHeader.DataStartIndex + 1}");

        #region Register New Client Or Update Last Seen Of Client
        if (!_clientDatas.ContainsKey(udpMessageHeader.ServerClientId))
        {
            if (_clientDatas.Count < _maxClients)
            {
                _clientDatas[udpMessageHeader.ServerClientId] = new ClientData()
                {
                    ClientInfo = new ClientInfo()
                    {
                        EndPoint = clientEndPoint,
                        ClientId = udpMessageHeader.ServerClientId,
                        Mtu = udpMessageHeader.MaxMtu,
                        LastSeen = DateTime.UtcNow,
                    }
                };

                OnNewClient?.Invoke(GuidInt128Converter.GuidToInt128(udpMessageHeader.ServerClientId));
                ClientConnected?.Invoke(clientEndPoint);
                //Console.WriteLine($"Client Connected. Client Info: {udpMessageHeader.ServerClientId}");
            }
            else
            {
                Console.WriteLine($"Max Clients Number Exceeded. Client Info: {udpMessageHeader.ServerClientId}");
                return;
            }            
        }
        else
        {
            _clientDatas[udpMessageHeader.ServerClientId].ClientInfo.LastSeen = DateTime.UtcNow;
        }
        #endregion Register New Client Or Update Last Seen Of Client

        if (udpMessageHeader.IsKeepAlive)
        {
            Console.WriteLine("KeepAliveMessage");
            return;
        }

        #region Update Message Info & Packet Info
        var clientData = _clientDatas[udpMessageHeader.ServerClientId];

        if (!clientData.ClientBuffer.MessageId.HasValue || clientData.ClientBuffer.MessageId.HasValue && clientData.ClientBuffer.MessageId.Value == udpMessageHeader.MessageId)
        {
            clientData.ClientBuffer.MessageId = udpMessageHeader.MessageId;
            clientData.ClientBuffer.TotalPackets = udpMessageHeader.TotalPackets;
        }
        else
        {
            if (clientData.ClientBuffer.MessageId.HasValue && clientData.ClientBuffer.MessageId.Value != udpMessageHeader.MessageId)
                Console.WriteLine($"Mixed Messages Packets Between Messages. Dropping Message {clientData.ClientBuffer.MessageId}");

            clientData.ClientBuffer.CleanMarkers();

            clientData.ClientBuffer.MessageId = udpMessageHeader.MessageId;
            clientData.ClientBuffer.TotalPackets = udpMessageHeader.TotalPackets;
        }
        var bytesLength = (int)(udpMessageHeader.DataEndIndex - udpMessageHeader.DataStartIndex + 1);
        Buffer.BlockCopy(_receiveBuffer, UdpMessageHeader.Size, clientData.ClientBuffer.DataReadyToSent, (int)udpMessageHeader.DataStartIndex, bytesLength);
        clientData.ClientBuffer.MarkPacketReceived(udpMessageHeader.PacketNumber, bytesLength);
        #endregion Update Message Info & Packet Info

        if (clientData.ClientBuffer.IsMessageCompleted())
        {
            OnNewClientMessage?.Invoke(GuidInt128Converter.GuidToInt128(clientData.ClientInfo.ClientId), clientData.ClientBuffer.DataReadyToSent, clientData.ClientBuffer.ActualBytesReceived);
            MessageReceived?.Invoke(clientEndPoint, clientData.ClientBuffer.DataReadyToSent, clientData.ClientBuffer.ActualBytesReceived);
            clientData.ClientBuffer.CleanMarkers();
        }
    }

    private async Task CheckTimeouts()
    {
        while (!isDisposed)
        {
            foreach (var client in _clientDatas.Values.ToList())
            {
                if ((DateTime.UtcNow - client.ClientInfo.LastSeen).TotalMilliseconds > _keepAliveTimeout)
                {
                    _clientDatas.TryRemove(client.ClientInfo.ClientId, out _);
                    ClientDisconnected?.Invoke(client.ClientInfo.EndPoint);
                    Console.WriteLine($"Client {client.ClientInfo.EndPoint} disconnected");
                }
            }
            await Task.Delay(2000);
        }
    }

    public void Dispose()
    {
        isDisposed = true;
    }

    public uint GetTotalSentMessages()
    {
        return messageIdGenerator;
    }
}

public class UdpByteArrayClient : IClientCommunication
{
    private readonly Socket _udpSocket;
    private readonly IPEndPoint _serverEndPoint;
    private readonly int _clientMtu;
    private readonly Guid _clientId;
    private          byte[] _receiveBuffer;
    private          byte[] _sendBuffer;
    private          byte[] _sendBufferForDebug;

    public event Action<IPEndPoint, byte[], int> MessageReceived;
    public event Action<byte[], long> NewMessageArrived;

    private readonly ConcurrentDictionary<Guid, ClientData> _serverDatas = new();

    public UdpByteArrayClient(string serverAddress, int serverPort, int clientMtu, int bufferSize)
    {
        _clientMtu = clientMtu;
        _clientId = Guid.NewGuid();
        _receiveBuffer = new byte[bufferSize];
        _sendBuffer = new byte[bufferSize]; // Pre-allocated send buffer
        _sendBufferForDebug = new byte[bufferSize]; // Pre-allocated send buffer
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        
        _udpSocket.ReceiveBufferSize = 10 * 1024 * 1024;
        _udpSocket.SendBufferSize = 10 * 1024 * 1024;

        _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverAddress), serverPort);
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        //_udpSocket.Bind(_serverEndPoint);
        _udpSocket.Bind(remoteEndPoint);
    }

    public void Init(long maxMessageSize)
    {
        _receiveBuffer = new byte[maxMessageSize];
        _sendBuffer = new byte[maxMessageSize];
        _sendBufferForDebug = new byte[maxMessageSize]; // Pre-allocated send buffer
    }

    public void ConnectToServer()
    {
        StartAsync();
    }
    
    public async Task StartAsync()
    {
        Task.Run(ReceiveLoop);
        Task.Run(SendKeepAliveLoop);
    }

    private void ReceiveLoop()
    {
        EndPoint remoteEndPoint = _udpSocket.LocalEndPoint; // new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int receivedBytes = _udpSocket.ReceiveFrom(_receiveBuffer, ref remoteEndPoint);
            ProcessMessage((IPEndPoint)remoteEndPoint, receivedBytes);
        }
    }

    private void ProcessMessage(IPEndPoint clientEndPoint, int receivedBytes)
    {
        if (receivedBytes < UdpMessageHeader.Size)
        {
            Console.WriteLine($"Message With Low Than Header Bytes Skipping! {receivedBytes}");
            return;
        }

        bool messageReady = false;

        var udpMessageHeader = UdpMessageHeader.FromBytes(_receiveBuffer);

        if (receivedBytes != udpMessageHeader.DataSize + udpMessageHeader.HeaderSize)
        {
            Console.WriteLine($"Message Currupted Actual Bytes {receivedBytes} Expected Bytes {udpMessageHeader.DataSize + udpMessageHeader.HeaderSize}");
            return;
        }        

        #region Register New Client Or Update Last Seen Of Client
        if (!_serverDatas.ContainsKey(udpMessageHeader.ServerClientId))
        {
            //if (_serverDatas.Count < _maxClients)
            {
                _serverDatas[udpMessageHeader.ServerClientId] = new ClientData()
                {
                    ClientInfo = new ClientInfo()
                    {
                        EndPoint = clientEndPoint,
                        ClientId = udpMessageHeader.ServerClientId,
                        Mtu = udpMessageHeader.MaxMtu,
                        LastSeen = DateTime.UtcNow,
                    }
                };

                //ClientConnected?.Invoke(clientEndPoint);
                //Console.WriteLine($"Client Connected. Client Info: {udpMessageHeader.ServerClientId}");
            }
            //else
            //{
            //    Console.WriteLine($"Max Clients Number Exceeded. Client Info: {udpMessageHeader.ServerClientId}");
            //    return;
            //}
        }
        else
        {
            _serverDatas[udpMessageHeader.ServerClientId].ClientInfo.LastSeen = DateTime.UtcNow;
        }
        #endregion Register New Client Or Update Last Seen Of Client

        #region Update Message Info & Packet Info
        var clientData = _serverDatas[udpMessageHeader.ServerClientId];

        if (!clientData.ClientBuffer.MessageId.HasValue || clientData.ClientBuffer.MessageId.HasValue && clientData.ClientBuffer.MessageId.Value == udpMessageHeader.MessageId)
        {
            clientData.ClientBuffer.MessageId = udpMessageHeader.MessageId;
            clientData.ClientBuffer.TotalPackets = udpMessageHeader.TotalPackets;
        }
        else
        {
            if (clientData.ClientBuffer.MessageId.HasValue && clientData.ClientBuffer.MessageId.Value != udpMessageHeader.MessageId)
                Console.WriteLine($"Mixed Messages Packets Between Messages. Dropping Message {clientData.ClientBuffer.MessageId}");

            clientData.ClientBuffer.CleanMarkers();

            clientData.ClientBuffer.MessageId = udpMessageHeader.MessageId;
            clientData.ClientBuffer.TotalPackets = udpMessageHeader.TotalPackets;
        }
        var bytesLength = (int)(udpMessageHeader.DataEndIndex - udpMessageHeader.DataStartIndex + 1);
        Buffer.BlockCopy(_receiveBuffer, UdpMessageHeader.Size, clientData.ClientBuffer.DataReadyToSent, (int)udpMessageHeader.DataStartIndex, bytesLength);
        clientData.ClientBuffer.MarkPacketReceived(udpMessageHeader.PacketNumber, bytesLength);
        #endregion Update Message Info & Packet Info

        if (clientData.ClientBuffer.IsMessageCompleted())
        {
            NewMessageArrived?.Invoke(clientData.ClientBuffer.DataReadyToSent, clientData.ClientBuffer.ActualBytesReceived);
            MessageReceived?.Invoke(clientEndPoint, clientData.ClientBuffer.DataReadyToSent, clientData.ClientBuffer.ActualBytesReceived);
            clientData.ClientBuffer.CleanMarkers();
            receivedMessageCounter++;
        }
    }

    private static uint receivedMessageCounter = 0;

    private static uint messageIdGenerator = 0;

    public void SendDataToServer(byte[] data, long count)
    {
        SendMessage(data, (int)count);
    }

    public void SendMessage(byte[] data, int length, bool isKeepAlive = false)
    {
        var msgId = Interlocked.Increment(ref messageIdGenerator);

        int mtu = _clientMtu;

        int maxPayloadSize = mtu - UdpMessageHeader.Size;

        int totalPackets = (int)Math.Ceiling((double)length / maxPayloadSize);
        int remainingLength = length;
        uint startIndex = 0, endIndex = 0;

        for (int i = 0; i < totalPackets; i++)
        {
            int bytesToSend = Math.Min(remainingLength, maxPayloadSize);
            remainingLength -= bytesToSend;
            endIndex = (uint)(startIndex + bytesToSend - 1);

            var udpMessageHeader = new UdpMessageHeader();
            
            udpMessageHeader.MaxMtu = mtu;
            udpMessageHeader.ServerClientId = _clientId;
            udpMessageHeader.TotalPackets = (UInt16)totalPackets;
            udpMessageHeader.PacketNumber = (UInt16)i;
            udpMessageHeader.DataStartIndex = startIndex;
            udpMessageHeader.DataEndIndex = endIndex;
            udpMessageHeader.MessageId = msgId;
            udpMessageHeader.IsKeepAlive = isKeepAlive;
            udpMessageHeader.DataSize = bytesToSend;

            UdpMessageHeader.ToBytes(ref udpMessageHeader, _sendBuffer);
            Buffer.BlockCopy(data, (int)startIndex, _sendBuffer, udpMessageHeader.HeaderSize, bytesToSend);
            Buffer.BlockCopy(data, (int)startIndex, _sendBufferForDebug, (int)udpMessageHeader.DataStartIndex, bytesToSend);

            _udpSocket.SendTo(_sendBuffer, udpMessageHeader.HeaderSize + bytesToSend, SocketFlags.None, _serverEndPoint);
                //serverDataValue.ClientInfo.EndPoint);

            startIndex += (uint)bytesToSend;
            Console.WriteLine($"[Client] - Msg Id {msgId} - Packet {i} - Bytes {udpMessageHeader.HeaderSize + bytesToSend}");
        }
    }

    private async Task SendKeepAliveLoop()
    {
        while (true)
        {
            byte[] keepAliveMessage = Encoding.UTF8.GetBytes("KEEP_ALIVE");
            SendMessage(keepAliveMessage, keepAliveMessage.Length, true);
            //_udpSocket.SendTo(keepAliveMessage, _serverEndPoint);
            await Task.Delay(2000);
        }
    }

    public uint GetTotalReceivedMessages()
    {
        return receivedMessageCounter;
    }
}

public class ClientInfo
{
    public IPEndPoint EndPoint { get; set; }
    public int? Mtu { get; set; }
    public Guid ClientId { get; set; }
    public DateTime LastSeen { get; set; }
}