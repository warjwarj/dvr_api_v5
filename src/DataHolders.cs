using System.Net.Sockets;

using static dvr_api.Utils;

namespace dvr_api
{
    /// <summary>
    /// just a holder that we can put in the SocketAsyncEventArgs object
    /// </summary>
    /// <remarks>Wrapper around a tcp client, and possibly an APIrequest object</remarks>
    public class DataHolder
    {
        public Client? client;
        public Queue<APIRequest> APIReqQueue;

        public DataHolder() 
        { 
            this.APIReqQueue = new Queue<APIRequest>();
        }
    }

    /// <summary>
    /// Use this class to hold data about a server task - essentially an API request
    /// </summary>
    public class APIRequest
    {
        public Socket? requester;
        public Socket? requestee;
        public string? request;
        public string[] request_stringarr;

        public APIRequest() { }

        public DateTime? EndTimeVideoPacket
        {
            get
            {
                return formatIntoDateTime(request_stringarr[4]).AddSeconds(Int32.Parse(request_stringarr[^1]));
            }
        }

        public void Init(Socket requester, Socket requestee, string request)
        {
            this.requester = requester;
            this.requestee = requestee;
            this.request = request;
            request_stringarr = request.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
