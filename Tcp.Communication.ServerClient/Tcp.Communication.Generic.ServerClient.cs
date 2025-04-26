using RP.Communication.Message.Interface;
using RP.Infra;
using RP.Infra.Logger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RP.Tcp.Communication.Generic.ServerClient
{
    public class Server<T> : IDisposable where T : ICommunicationSerialiable, new()
    {
        #region Events Fields
        public event Action<T> PacketIsAboutToBeSend;
        public event Action<T> PacketSent;
        public event Action<int, T> PacketReceived;
        public event Action<int> OnNewClient;
        public event Action<int> OnRemoveClient;
        #endregion Events Fields

        #region Clients
        private Dictionary<int, Client<T>> clients = new Dictionary<int, Client<T>>();
        private IMessageCreator<T> incomningMessageCreator;
        #endregion Clients

        #region Communication Mediate Fields
        private String port;

        private int writeTimeoutInterval_mSec;
        private int readTimeoutInterval_mSec;

        private int maxOutgoingMessageSizeInBytes;
        private int maxIncomingMessageSizeInBytes;

        private TcpListener tcpServer;
        private NamedPipeServerStream pipeServer;
        #endregion Communication Mediate Fields

        #region Logger Fields
        private ILogger logger;
        #endregion Logger Fields

        public Server(
            String port,
            int writeTimeoutInterval_mSec,
            int readTimeoutInterval_mSec,
            int maxOutgoingMessageSizeInBytes,
            int maxIncomingMessageSizeInBytes,
            IMessageCreator<T> incomningMessageCreator,
            ILogger logger = null)
        {
            this.port = port;
            this.logger = logger;
            this.writeTimeoutInterval_mSec = writeTimeoutInterval_mSec;
            this.readTimeoutInterval_mSec = readTimeoutInterval_mSec;
            this.maxOutgoingMessageSizeInBytes = maxOutgoingMessageSizeInBytes;
            this.maxIncomingMessageSizeInBytes = maxIncomingMessageSizeInBytes;
            this.incomningMessageCreator = incomningMessageCreator;

            tcpServer = new TcpListener(new IPEndPoint(IPAddress.Any, int.Parse(port)));
            tcpServer.Start();
            
            GenerateNewPipeServerStream();
        }

        private void GenerateNewPipeServerStream()
        {
            pipeServer = new NamedPipeServerStream(string.Format("PipesOfPiece_{0}", port), PipeDirection.InOut, 20, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1000, 1000);
        }

        public Dictionary<int, Client<T>> GetClients()
        {
            return new Dictionary<int, Client<T>>(clients);
        }

        public Client<T> GetClientById(int id)
        {
            return clients[id];
        }

        public void BeginAcceptClients()
        {
            BeginAcceptTcpClients();
            BeginAcceptPipeClients();
        }

        private void BeginAcceptTcpClients()
        {
            try
            {
                tcpServer.BeginAcceptTcpClient(DoAcceptTcpClientCallback, tcpServer);
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Error(string.Format("BeginAcceptClients failed. Exception: {0}", ex.ToString()));
            }
        }

        private void DoAcceptTcpClientCallback(IAsyncResult ar)
        {
            try
            { 
                var listener = (TcpListener)ar.AsyncState;
                var tcpClient = listener.EndAcceptTcpClient(ar);
                var stream = tcpClient.GetStream();

                AddClient(
                    new TcpClient<T>(
                        tcpClient,
                        stream,
                        writeTimeoutInterval_mSec,
                        readTimeoutInterval_mSec,
                        maxOutgoingMessageSizeInBytes,
                        maxIncomingMessageSizeInBytes,
                        incomningMessageCreator,
                        logger));

                BeginAcceptTcpClients();
            }
            catch (Exception ex)
            {
                logger?.Error(ex);
            }
        }

        private void BeginAcceptPipeClients()
        {
            try
            {
                pipeServer.BeginWaitForConnection(DoAcceptPipeClientCallback, pipeServer);
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Error(string.Format("BeginAcceptClients failed. Exception: {0}", ex.ToString()));
            }
        }

        private void DoAcceptPipeClientCallback(IAsyncResult ar)
        {
            try
            {
                var listener = (NamedPipeServerStream)ar.AsyncState;
                listener.EndWaitForConnection(ar);
                Stream stream = listener;

                AddClient(
                    new NamedPipeClient<T>(
                        listener,
                        stream,
                        writeTimeoutInterval_mSec,
                        readTimeoutInterval_mSec,
                        maxOutgoingMessageSizeInBytes,
                        maxIncomingMessageSizeInBytes,
                        incomningMessageCreator,
                        logger));

                GenerateNewPipeServerStream();
                BeginAcceptPipeClients();
            }
            catch (Exception ex)
            {
                logger?.Error(ex);
            }
        }

        private void AddClient(Client<T> client)
        {
            RefreshClientList();

            client.PacketIsAboutToBeSend += Client_PacketIsAboutToBeSend;
            client.PacketReceived += (t) => { Client_PacketReceived(client.Id, t); };
            client.PacketSent += Client_PacketSent;

            lock (clients)
            {
                clients.Add(client.Id, client);
            }

            try
            {
                var evnt = OnNewClient;
                if (evnt != null)
                    evnt(client.Id);
            }
            catch (Exception ex) { if (logger != null) logger.Error(ex); }
        }

        private void RemoveClient(Client<T> client)
        {
            try
            {
                var evnt = OnRemoveClient;
                if (evnt != null)
                    evnt(client.Id);
            }
            catch (Exception ex) { if (logger != null) logger.Error(ex); }

            client.PacketIsAboutToBeSend -= Client_PacketIsAboutToBeSend;
            client.PacketReceived -= (t) => { Client_PacketReceived(client.Id, t); };
            client.PacketSent -= Client_PacketSent;

            client.Dispose();

            lock (clients)
            {
                clients.Remove(client.Id);
            }
        }

        private void RefreshClientList()
        {
            var clientsCopy = clients.ToList();

            foreach (var client in clientsCopy)
                if (!client.Value.IsConnected)
                    clients.Remove(client.Key);
        }

        private void Close()
        {
            if (clients != null)
            {
                var clientsCopy = clients.ToList();
                foreach (var client in clientsCopy)
                    try { RemoveClient(client.Value); } catch { }

                clients = null;
            }

            try
            {
                if (pipeServer != null)
                {
                    if (pipeServer.IsConnected)
                        pipeServer.Disconnect();
                    pipeServer.Close();
                    pipeServer.Dispose();
                }
            }
            catch { }
            finally { pipeServer = null; }

            try
            {
                if (tcpServer != null)
                    tcpServer.Stop();
            }
            catch { }
            finally { tcpServer = null; }
        }

        public void Dispose()
        {
            Close();
        }

        #region Events Methods

        private void Client_PacketSent(T t)
        {
            var evnt = PacketSent;
            if (evnt != null)
                evnt(t);
        }

        private void Client_PacketReceived(int clientId, T t)
        {
            var evnt = PacketReceived;
            if (evnt != null)
                evnt(clientId, t);
        }

        private void Client_PacketIsAboutToBeSend(T t)
        {
            var evnt = PacketIsAboutToBeSend;
            if (evnt != null)
                evnt(t);
        }

        #endregion Events Methods
    }

    public interface IClient<T> : IDisposable
    {
        int Id { get; }

        event Action<T> PacketIsAboutToBeSend;
        event Action<T> PacketSent;
        event Action<T> PacketReceived;

        int GetMaxIncomingMessageSizeInBytes();
        int GetMaxOutcomingMessageSizeInBytes();

        bool IsConnected { get; }
        void SendData(T t, bool keepAlive = false);
        void InvokeStartReceiveData();
        void KeepConnectionAlive();
        void TryConnect();
    }

    public abstract class Client<T> : IClient<T> where T : ICommunicationSerialiable, new() //IDisposable where T : ICommunicationSerialiable, new()
    {
        #region Id Fields
        private static int idGenerator = 0;
        public int Id { get; private set; }
        #endregion Id Fields

        #region Events Fields
        public event Action<T> PacketIsAboutToBeSend;
        public event Action<T> PacketSent;
        public event Action<T> PacketReceived;
        #endregion Events Fields

        #region Communication Mediate Fields
        protected Stream stream;
        protected int writeTimeoutInterval_mSec;
        protected int readTimeoutInterval_mSec;
        protected System.Threading.Tasks.Task receiveDataTask;
        #endregion Communication Mediate Fields

        #region Send/Receive Data Buffers Fields
        internal Buffer OutgoData;
        internal Buffer IncomeData;
        #endregion Send/Receive Data Buffers Fields

        #region T Creator
        private IMessageCreator<T> messageCreator;
        #endregion T Creator

        #region Keep Connection Alive Fields
        protected Timer keepAliveTimer;
        protected DateTime LastReceiveOrSentMessage;
        protected object keepAliveLocker = new object();
        #endregion Keep Connection Alive Fields

        #region Logger Fields
        protected ILogger logger;
        #endregion Logger Fields    

        #region Dispose Fields
        protected bool isDisposed;
        #endregion Dispose Fields

        public Client(
            int writeTimeoutInterval_mSec,
            int readTimeoutInterval_mSec,
            int maxOutgoingMessageSizeInBytes,
            int maxIncomingMessageSizeInBytes,
            IMessageCreator<T> messageCreator,
            ILogger logger = null)
        {
            Id = Interlocked.Increment(ref idGenerator);

            this.writeTimeoutInterval_mSec = writeTimeoutInterval_mSec;
            this.readTimeoutInterval_mSec = readTimeoutInterval_mSec;

            OutgoData = new Buffer(maxOutgoingMessageSizeInBytes);
            IncomeData = new Buffer(maxIncomingMessageSizeInBytes);

            this.messageCreator = messageCreator;

            this.logger = logger;
        }

        public int GetMaxIncomingMessageSizeInBytes()
        {
            return IncomeData.bytesArray.Length;
        }
        public int GetMaxOutcomingMessageSizeInBytes()
        {
            return IncomeData.bytesArray.Length;
        }

        public abstract bool IsConnected { get; }

        public void SendData(T t, bool keepAlive = false)
        {
            lock (keepAliveLocker)
            {
                try
                {
                    var memStream = OutgoData.memStream;
                    var bytesArray = OutgoData.bytesArray;

                    if (keepAlive)
                    {
                        memStream.Position = 0;
                        var bw = new BinaryWriter(memStream);

                        bw.Write(Validation.Start, 0, Validation.Start.Length);
                        bw.Write(Validation.StartKeepAliveMessage);
                        var bytesToSend = (int)bw.BaseStream.Position;

                        stream.Write(bytesArray, 0, bytesToSend);
                    }
                    else
                    {
                        var packetIsAboutToBeSend = PacketIsAboutToBeSend;
                        if (packetIsAboutToBeSend != null)
                            packetIsAboutToBeSend(t);

                        memStream.Position = 0;
                        var bw = new BinaryWriter(memStream);

                        bw.Write(Validation.Start, 0, Validation.Start.Length);
                        bw.Write(Validation.StartRegularMessage);

                        var comMessageStartPos = bw.BaseStream.Position;
                        bw.BaseStream.Position += sizeof(Int32);
                        bw.Write(Validation.Count, 0, Validation.Count.Length);
                        var dataMessageStartPos = bw.BaseStream.Position;
                        t.Serialize(bw);
                        var dataMessageEndPos = bw.BaseStream.Position;
                        var serializeSize = dataMessageEndPos - dataMessageStartPos;
                        Array.Copy(BitConverter.GetBytes((int)serializeSize), 0, bytesArray, comMessageStartPos, sizeof(Int32));
                        bw.Write(Validation.End, 0, Validation.End.Length);
                        var bytesToSend = (int)bw.BaseStream.Position;

                        stream.Write(bytesArray, 0, bytesToSend);

                        var packetSent = PacketSent;
                        if (packetSent != null)
                        {
                            packetSent(t);
                            LastReceiveOrSentMessage = DateTime.Now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (logger != null)
                        logger.Error(string.Format("Failed to send message: {0}", ex.ToString()));
                }
            }
        }

        protected void StartReceiveData()
        {
            try
            {
                var memStream = IncomeData.memStream;
                var bytesArray = IncomeData.bytesArray;

                bool isSync = false;
                while (true)
                {
                    try
                    {
                        if (isSync)
                        {
                            var v = messageCreator.GetMessage();

                            int readSize = 0;
                            while (readSize < sizeof(int))
                                readSize += stream.Read(bytesArray, readSize, sizeof(int) - readSize);
                            Validation.Validate(stream, Validation.Count);

                            readSize = 0;
                            var messageSize = BitConverter.ToInt32(bytesArray, 0);
                            while (readSize < messageSize)
                                readSize += stream.Read(bytesArray, readSize, messageSize - readSize);

                            memStream.Position = 0;
                            var br = new BinaryReader(memStream);

                            v.Deserialize(br);

                            Validation.Validate(stream, Validation.End);
                            isSync = false;

                            var packetReceived = PacketReceived;
                            if (packetReceived != null)
                                packetReceived(v);
                        }
                        else
                        {
                            isSync = Validation.Sync(stream, Validation.Start);
                            LastReceiveOrSentMessage = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message == Validation.ValidationFailedExceptionMessage)
                        {
                            isSync = false;
                            if (logger != null)
                                logger.Error("Message out of sync!");
                        }
                        else
                        {
                            if (isDisposed)
                                throw;
                            else
                                Thread.Sleep(10);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Error(string.Format("Connection failed, {0}", ex.ToString()));
                if (!isDisposed)
                    Close();
            }
        }

        public void InvokeStartReceiveData()
        {
            receiveDataTask = new System.Threading.Tasks.Task(() => this.StartReceiveData(), System.Threading.Tasks.TaskCreationOptions.LongRunning);
            receiveDataTask.Start();
        }

        public void KeepConnectionAlive()
        {
            keepAliveTimer = new Timer(KeepAlive, null, 0, 5000);
        }

        protected void KeepAlive(Object o)
        {
            try
            {
                keepAliveTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                if (isDisposed)
                {
                    keepAliveTimer = null;
                    return;
                }

                if (DateTime.Now.Subtract(LastReceiveOrSentMessage).TotalSeconds > 5)
                    SendData(default, true);

                if (!IsConnected)
                    TryConnect();
            }
            catch (Exception ex)
            {
                if (logger != null)
                    logger.Error(ex.ToString());
            }
            finally
            {
                if (keepAliveTimer != null)
                    keepAliveTimer.Change(5000, 5000);
            }
        }

        public virtual void TryConnect()
        {
            if (writeTimeoutInterval_mSec > 0)
                stream.WriteTimeout = writeTimeoutInterval_mSec;

            if (readTimeoutInterval_mSec > 0)
                stream.ReadTimeout = readTimeoutInterval_mSec;
        }

        protected virtual void Close()
        {
            try
            {
                if (stream != null)
                    stream.Close();
            }
            catch { }
            finally { stream = null; }

            try
            {
                if (receiveDataTask != null)
                    receiveDataTask.Dispose();
            }
            catch { }
            finally { receiveDataTask = null; }
        }

        public void Dispose()
        {
            isDisposed = true;
            Close();
        }
    }

    public class TcpClient<T> : Client<T> where T : ICommunicationSerialiable, new()
    {
        #region Communication Mediate Fields        
        protected String Ip;
        protected String Port;
        protected TcpClient client;
        #endregion Communication Mediate Fields

        public TcpClient(
            TcpClient client,
            Stream stream,
            int writeTimeoutInterval_mSec,
            int readTimeoutInterval_mSec,
            int maxOutgoingMessageSizeInBytes,
            int maxIncomingMessageSizeInBytes,
            IMessageCreator<T> messageCreator,
            ILogger logger = null) : base(writeTimeoutInterval_mSec, readTimeoutInterval_mSec, maxOutgoingMessageSizeInBytes, maxIncomingMessageSizeInBytes, messageCreator, logger)
        {
            this.client = client;
            this.stream = stream;

            if (maxIncomingMessageSizeInBytes > 0)
                InvokeStartReceiveData();
        }
        public TcpClient(
                String ip,
                String port,
                int writeTimeoutInterval_mSec,
                int readTimeoutInterval_mSec,
                int maxOutgoingMessageSizeInBytes,
                int maxIncomingMessageSizeInBytes,
                IMessageCreator<T> messageCreator,
                ILogger logger = null) : base(writeTimeoutInterval_mSec, readTimeoutInterval_mSec, maxOutgoingMessageSizeInBytes, maxIncomingMessageSizeInBytes, messageCreator, logger)
        {
            OutgoData = new Buffer(maxOutgoingMessageSizeInBytes);
            IncomeData = new Buffer(maxIncomingMessageSizeInBytes);
            this.logger = logger;
            this.writeTimeoutInterval_mSec = writeTimeoutInterval_mSec;
            this.readTimeoutInterval_mSec = readTimeoutInterval_mSec;

            Ip = ip;
            Port = port;
        }

        public override void TryConnect()
        {
            try
            {
                try { if (client != null) client.Close(); }
                catch (Exception ex) { if (logger != null) logger.Error(string.Format("Close Connection: {0}", ex.ToString())); }

                client = new TcpClient();

                client.Connect(new IPEndPoint(IPAddress.Parse(Ip), int.Parse(Port)));

                // TODO: Check it
                //client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                stream = client.GetStream();

                base.TryConnect();
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Error(string.Format("Connection failed, {0}", ex.ToString()));
                Close();
            }
        }

        public override bool IsConnected
        {
            get
            {
                // TODO: Check it
                //if (client != null)
                //    client.Client.IOControl(IOControlCode.KeepAliveValues, new byte[1] { 0 }, null);

                return client != null && client.Connected;
            }
        }

        protected override void Close()
        {
            base.Close();     

            try
            {
                if (client != null)
                    client.Close();
            }
            catch { }
            finally { client = null; }
        }
    }

    public class NamedPipeClient<T> : Client<T> where T : ICommunicationSerialiable, new()
    {
        #region Communication Mediate Fields        
        protected String port;
        protected PipeStream pipeClient;
        #endregion Communication Mediate Fields

        public NamedPipeClient(
            PipeStream client,
            Stream stream,
            int writeTimeoutInterval_mSec,
            int readTimeoutInterval_mSec,
            int maxOutgoingMessageSizeInBytes,
            int maxIncomingMessageSizeInBytes,
            IMessageCreator<T> messageCreator,
            ILogger logger = null) : base(writeTimeoutInterval_mSec, readTimeoutInterval_mSec, maxOutgoingMessageSizeInBytes, maxIncomingMessageSizeInBytes, messageCreator, logger)
        {
            this.pipeClient = client;
            this.stream = stream;

            if (maxIncomingMessageSizeInBytes > 0)
                InvokeStartReceiveData();
        }

        public NamedPipeClient(
            String port,
            int writeTimeoutInterval_mSec,
            int readTimeoutInterval_mSec,
            int maxOutgoingMessageSizeInBytes,
            int maxIncomingMessageSizeInBytes,
            IMessageCreator<T> messageCreator,
            ILogger logger = null) : base(writeTimeoutInterval_mSec, readTimeoutInterval_mSec, maxOutgoingMessageSizeInBytes, maxIncomingMessageSizeInBytes, messageCreator, logger)
        {
            OutgoData = new Buffer(maxOutgoingMessageSizeInBytes);
            IncomeData = new Buffer(maxIncomingMessageSizeInBytes);
            this.logger = logger;
            this.writeTimeoutInterval_mSec = writeTimeoutInterval_mSec;
            this.readTimeoutInterval_mSec = readTimeoutInterval_mSec;

            this.port = port;
        }

        public override void TryConnect()
        {
            try
            {
                try { if (pipeClient != null) pipeClient.Close(); }
                catch (Exception ex) { if (logger != null) logger.Error(string.Format("Close Connection: {0}", ex.ToString())); }
                var namedPipeClient = new NamedPipeClientStream(".", string.Format("PipesOfPiece_{0}", port), PipeDirection.InOut);
                pipeClient = namedPipeClient;
                namedPipeClient.Connect();
                stream = pipeClient;
            }
            catch (Exception ex)
            {
                if (logger != null) logger.Error(string.Format("Connection failed, {0}", ex.ToString()));
                Close();
            }
        }

        public override bool IsConnected
        {
            get
            {
                return pipeClient != null && pipeClient.IsConnected;
            }
        }

        protected override void Close()
        {
            base.Close();

            try
            {
                if (pipeClient != null)
                {
                    pipeClient.Close();
                    pipeClient.Dispose();
                }
            }
            catch { }
            finally { pipeClient = null; }
        }
    }

    public static class Validation
    {
        public static byte StartKeepAliveMessage = 70;
        public static byte StartRegularMessage = 60;
        public static byte[] Start = new byte[] { 10, 20, 30, 40, 50 };
        public static byte[] End = new byte[] { 80, 90, 100 };
        public static byte[] Count = new byte[] { 15, 25, 35, 45, 55 };

        public static string ValidationFailedExceptionMessage = "Failed reading data by validation test!";

        public static void Validate(BinaryReader br, byte[] arr)
        {
            var count = arr.Length;
            for (int i = 0; i < count; i++)
                if (arr[i] != br.ReadByte())
                    throw new Exception(ValidationFailedExceptionMessage);
        }

        public static void Validate(Stream br, byte[] arr)
        {
            var count = arr.Length;
            for (int i = 0; i < count; i++)
            {
                int readByte;
                do
                {
                    readByte = br.ReadByte();
                }
                while (readByte < 0);

                if (arr[i] != readByte)
                    throw new Exception(ValidationFailedExceptionMessage);
            }
        }

        public static bool Sync(Stream br, byte[] arr)
        {
            int i = 0;

            while (true)
            {
                int readByte;
                do
                {
                    readByte = br.ReadByte();
                }
                while (readByte < 0);

                if (readByte == arr[i])
                {
                    i++;
                }
                else
                {
                    if (readByte == arr[0])
                        i = 1;
                    else
                        i = 0;
                }

                if (i == arr.Length)
                {
                    readByte = br.ReadByte();
                    if (readByte == Validation.StartRegularMessage)
                    {
                        return true;
                    }
                    else
                    {
                        i = 0;

                        if (readByte == Validation.StartKeepAliveMessage)
                            continue;
                        else
                            continue;
                    }
                }
                
            }
        }

        public static void MaxCount(int count, int maxCount)
        {
            if (count > maxCount)
                throw new Exception(ValidationFailedExceptionMessage);
        }
    }

    internal class Buffer
    {
        public byte[] bytesArray;
        public MemoryStream memStream;

        internal Buffer(int maxMessageSizeInBytes)
        {
            bytesArray = new byte[maxMessageSizeInBytes];
            memStream = new MemoryStream(bytesArray);
        }
    }

    public interface IMessageCreator<T>
    {
        Func<T> GetMessage { get; }
    }

    public class MessageCreator<T> : IMessageCreator<T> where T : new()
    {
        public Func<T> GetMessage { get { return () => new T(); } }
    }

    public class SingeltonMessageCreator<T> : IMessageCreator<T> where T : new()
    {
        T t = new T();

        public Func<T> GetMessage { get { return () => t; } }
    }

    public class UnitTestClient<T> where T : ICommunicationSerialiable, new()
    {
        private Client<T> client;

        private Object locker = new Object();

        public UnitTestClient(Client<T> client)
        {
            this.client = client;
        }

        public void InvokeSendData(T t)
        {
            lock (locker)
            {
                client.SendData(t);
            }
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}