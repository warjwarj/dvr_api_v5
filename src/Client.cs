using log4net;
using System.Net.Sockets;
using System.Text;
using System.Runtime;

using static dvr_api.Utils;

public enum ClientType
{
    MDVR,
    APIClientRawTCP,
    APIClientWebsocket
}

namespace dvr_api
{
    public abstract class Client
    {
        protected static readonly ILog _logger = LogManager.GetLogger(typeof(Client)); // logger!
        public abstract ClientType clientType { get; set; }
        public bool setupComplete = false;

        public string? id { get; set; } = null;
        public Socket? socket { get; set; } = null;

        public Client(){}
        
        public abstract bool SetId(byte[] data);
        public abstract void ResetFields();
    }

    public class MDVR : Client
    {
        public override ClientType clientType { get; set; } = ClientType.MDVR;
        public MDVR() : base() { }

        public override bool SetId(byte[] data)
        {
            if (data != null)
            {
                // we might have multiple messages
                string[] messages = Encoding.ASCII.GetString(data).Split('\r', StringSplitOptions.RemoveEmptyEntries);
                if (messages.Length != 0)
                {
                    // set id if not already set
                    if (id == null)
                    {
                        id = getMDVRIdFromMessage(messages[0]);
                    }
                }
                else
                {
                    _logger.Debug("Couldn't parse data from MDVR. Check that it is delimited correctly");
                }
            }
            return id != null;
        }
        public void SetId(string id)
        {
            this.id = id;
        }
        public override void ResetFields()
        {
            this.id = null;
            this.socket = null;
            this.setupComplete = false;
        }
        
        public int setupParams(SocketAsyncEventArgs eArgs)
        {
            string devId = ((DataHolder)eArgs.UserToken).client.id;
            string paramstring = 
                $"$SRV2!;{devId};{string.Join(".", Globals.CAM_SVR_IP)};{Globals.CAM_SVR_PORT}\r" +
                $"$REP!;{devId}10;10;10;1200;3600\r" +
                $"$CFG!;{devId};noRptAck=1\r";
            return Encoding.ASCII.GetBytes(
                paramstring,
                0,
                paramstring.Length,
                eArgs.Buffer,
                eArgs.Offset
                );
        }
    }

    public class APIClient : Client
    {
        public override ClientType clientType { get; set; } = ClientType.APIClientWebsocket;
        public bool wsHandshakeComplete = false;
        public APIClient() : base() 
        { 
            id = Guid.NewGuid().ToString();
        }

        public override bool SetId(byte[] data)
        {
            return true;
        }

        public override void ResetFields()
        {
            this.setupComplete = true;
            this.wsHandshakeComplete = false;
            this.socket = null;
            this.setupComplete = false;
        }
    }

    public static class Protocol
    {

    }
}