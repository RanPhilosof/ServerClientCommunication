using RP.Communication.Message.Interface;
using RP.Communication.ServerClient.Interface;
using RP.Tcp.Communication.Generic.ServerClient;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
//using static System.Runtime.InteropServices.JavaScript.JSType;

#if NET6_0
    using Int128 = System.Int64;
#endif

namespace Tcp.Communication.ByteArray.ServerClient;

public class ByteArray : ICommunicationSerialiable
{
    public byte[] Bytes;
    public int ActualSize;
    
    public ByteArray()
    {
        Bytes = new byte[0];
        ActualSize = 0;
    }

    public ByteArray(int maxSize)
    {
        Bytes = new byte[maxSize];
    }

    public void Deserialize(BinaryReader br)
    {
        ActualSize = br.ReadInt32();
        br.Read(Bytes, 0, ActualSize);
    }

    public void Serialize(BinaryWriter bw)
    {
        bw.Write(ActualSize);
        bw.Write(Bytes, 0, ActualSize);
    }
}

public class ByteArrayMessageCreator : IMessageCreator<ByteArray>
{
    public ByteArray byteArray;

    public ByteArrayMessageCreator(int maxSize)
    {
        byteArray = new ByteArray(maxSize);
    }

    public Func<ByteArray> GetMessage { get { return () => byteArray; } }
}


public class TcpByteArrayServer : IServerCommunication
{
    private ByteArray byteArray;
    private Server<ByteArray> server;
    private uint messagesSent = 0;

    public event Action<Int128> OnNewClient;
    public event Action<Int128, byte[], long> OnNewClientMessage;

    private bool addMissingMessagesSomeTimes;
    private int messagesCounter;
    private int messagesCountToMiss;

    public TcpByteArrayServer(int port, int keepAliveTimeout, int bufferSize, int maxClients)
    {
        byteArray = new ByteArray(bufferSize);

        var byteArrayMessageCreator = new ByteArrayMessageCreator(bufferSize);

        server = new Server<ByteArray>(port.ToString(), 0, 0, bufferSize, bufferSize, byteArrayMessageCreator);

        server.OnNewClient += Server_OnNewClient;
        server.PacketReceived += Server_PacketReceived;
    }

    public void SetMissingMessagesToClient(bool addMissingMessagesSomeTimes, int messagesCountToMiss)
    {
        this.addMissingMessagesSomeTimes = addMissingMessagesSomeTimes;
        this.messagesCountToMiss = messagesCountToMiss;
    }

    private void Server_PacketReceived(int clientId, ByteArray byteArray)
    {
        OnNewClientMessage?.Invoke(clientId, byteArray.Bytes, byteArray.ActualSize);
    }

    private void Server_OnNewClient(int clientId)
    {
        OnNewClient?.Invoke(clientId);
    }

    public void Init(long maxMessageSize)
    {
        server.BeginAcceptClients();
    }

    public void SendDataToClient(Int128 clientId, byte[] data, long count)
    {
        if (addMissingMessagesSomeTimes)
        {
            messagesCounter++;

            if (messagesCounter % messagesCountToMiss == 0)
            {
                //logger?.Info("ServerCommunication: Force Missing Message");
                return;
            }
        }

        byteArray.ActualSize = (int)count;
        byteArray.Bytes = data;

        server.GetClientById((int)clientId).SendData(byteArray);

        Interlocked.Increment(ref messagesSent);
    }

    public List<Int128> GetClients()
    {
        return server.GetClients().Keys.Select(x => (Int128)x).ToList();
    }
    
    public void Dispose()
    {
        server.Dispose();
    }

    public uint GetTotalSentMessages()
    {
        return messagesSent;
    }
}

public class TcpByteArrayClient : IClientCommunication
{
    private ByteArray byteArray;

    private Client<ByteArray> client;

    private uint receivedMessageCounter = 0;

    private uint messageIdGenerator = 0;

    public event Action<byte[], long> NewMessageArrived;

    public TcpByteArrayClient(string serverAddress, int serverPort, int bufferSize)
    {
        byteArray = new ByteArray(bufferSize);

        var byteArrayMessageCreator = new ByteArrayMessageCreator(bufferSize);
        client = new TcpClient<ByteArray>(
            serverAddress,
            serverPort.ToString(),
            0, 0, bufferSize, bufferSize, byteArrayMessageCreator);

        client.PacketReceived += Client_PacketReceived;
    }

    private void Client_PacketReceived(ByteArray byteArray)
    {
        NewMessageArrived.Invoke(byteArray.Bytes, byteArray.ActualSize);
        Interlocked.Increment(ref receivedMessageCounter);
    }

    public void Init(long maxMessageSize)
    {
        client.InvokeStartReceiveData();
    }

    public void ConnectToServer()
    {
        client.TryConnect();
        client.KeepConnectionAlive();
    }
    
    public void SendDataToServer(byte[] data, long count)
    {
        byteArray.Bytes = data;
        byteArray.ActualSize = (int)count;
        
        client.SendData(byteArray);

        Interlocked.Increment(ref messageIdGenerator);
    }

    public uint GetTotalReceivedMessages()
    {
        return receivedMessageCounter;
    }
}
