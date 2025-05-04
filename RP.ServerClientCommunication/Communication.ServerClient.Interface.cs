using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if NET6_0
    using Int128 = System.Int64;
#endif

namespace RP.Communication.ServerClient.Interface
{
    public interface IClientCommunication
    {
        void Init(long maxMessageSize);
        void ConnectToServer();
        event Action<byte[], long> NewMessageArrived;
        void SendDataToServer(byte[] data, long count);
    }

    public interface IServerCommunication : IDisposable
    {
        void Init(long maxMessageSize);
        event Action<Int128> OnNewClient;
        event Action<Int128, byte[], long> OnNewClientMessage;
        List<Int128> GetClients();
        void SendDataToClient(Int128 clientId, byte[] data, long count);
    }
}
