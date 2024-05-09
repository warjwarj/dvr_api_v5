using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using static dvr_api.Utils;

namespace dvr_api
{
    public class GPS_Server : AsyncSocketServer<MDVR>
    {
        private HeapObjectPool<APIRequest> apiReqPool;
        private Dictionary<string, APIRequest> activeRequests;

        public GPS_Server(int numConnections, int IOBuffersize, IPEndPoint endpoint, ObjectManager controller, HeapObjectPool<APIRequest> apiReqPool, Dictionary<string, APIRequest> activeRequests)
            : base(numConnections, IOBuffersize, endpoint, controller)
        {
            this.apiReqPool = apiReqPool;
            this.activeRequests = activeRequests;
        }

        protected override void setupLogic(SocketAsyncEventArgs eArgs)
        {
            DataHolder? dh = (DataHolder)eArgs.UserToken;
            if (dh != null && dh.client != null && eArgs.Buffer != null)
            {
                bool setIdSuccessfully = dh.client.SetId(eArgs.Buffer[eArgs.Offset..(eArgs.Offset + eArgs.BytesTransferred)]);
                if (setIdSuccessfully)
                {
                    bool addedSuccessfully = _clientManager.Add(dh);
                    _logger.Debug($"{this.GetType().Name}: Registered client connection in controller: {addedSuccessfully}");
                    dh.client.setupComplete = true;
                }
            }
            else
            {
                throw new NullReferenceException("Object deserialised from a SAEA object was null");
            }
        }

        protected override void ProcessReceive(SocketAsyncEventArgs eArgs)
        {
            try
            {
                if (eArgs.SocketError == SocketError.Success && eArgs.BytesTransferred != 0)
                {
                    DataHolder? dh = (DataHolder)eArgs.UserToken;
                    MDVR? client = (MDVR)dh.client;
                    if (dh != null && client != null && eArgs.Buffer != null)
                    {
                        // get array of messages. Regex splits on $. Then filter for !empty, and ends with <CR>
                        string whole = Encoding.ASCII.GetString(eArgs.Buffer[eArgs.Offset..(eArgs.Offset + eArgs.BytesTransferred)]);
                        string[] parts = Array.FindAll(Regex.Split(whole, "(?=\\$)"), s => !string.IsNullOrEmpty(s) && s.EndsWith("\r"));

                        // we need this variable to determine whether or not to send setup params at the end of the function
                        bool setUpCompleteBeforeProcessingData = client.setupComplete;

                        // process induvidual messages
                        for (int i=0; i < parts.Length; i++)
                        {
                            if (!client.setupComplete)
                            {
                                setupLogic(eArgs); // <-- this will flip the setup complete variable according to internal logic
                            }
                            _logger.Debug($"{this.GetType().Name}: Received from {client.id}: {parts[i]}");
                        }
                        // this code contained in the if statement is essentially checking if there is an outstanding api request for this device.
                        // it assumes that the data that was just transferred is the response to a command that was just sent.
                        // TODO: could check that the first section of the message matches that of the request, so like $video;0;ok\r == $video;0;123123;123123;12313\r etc.
                        if (dh.APIReqQueue.Count != 0)
                        {
                            APIRequest apiReq = dh.APIReqQueue.Dequeue();

                            // we are sending a message to an API client, browser client, so we need to encode it according to the websocket protocol
                            int encodedLength = TcpUtils.EncodeMessage(ref eArgs, eArgs.BytesTransferred, (byte)TcpUtils.Opcode.text);
                            ReadOnlySpan<byte> data = eArgs.Buffer[eArgs.Offset..(eArgs.Offset + encodedLength)];

                            apiReq.requester.Send(data);
                            apiReqPool.Push(apiReq);
                        }
                        // if client.setupComplete was false before and true after then 
                        // its the device's first communication and we need to send out setup params.
                        if (!setUpCompleteBeforeProcessingData && client.setupComplete)
                        {
                            int paramLength = client.setupParams(eArgs);
                            eArgs.SetBuffer(eArgs.Offset, paramLength);
                            bool completedSynchronusl = client.socket.SendAsync(eArgs);
                            eArgs.SetBuffer(eArgs.Offset, _socketBufferSize);
                            client.setupComplete = true;
                            if (!completedSynchronusl)
                            {
                                ProcessSend(eArgs);
                            }
                        }
                        else
                        {
                            // if data is queued on the socket then the read operation will complete synchronusly
                            bool completedSynchronusly = dh.client.socket.ReceiveAsync(eArgs);
                            if (!completedSynchronusly)
                            {
                                ProcessReceive(eArgs);
                            }
                        }
                    }
                    else
                    {
                        throw new NullReferenceException("Object deserialised from a SAEA object was null");
                    }
                }
                else
                {
                    //_logger.Warn($"Closing socket in ProcessReceive, Error: {eArgs.SocketError}, BytesTransferred: {receiveArgs.BytesTransferred}");
                    CloseClientSocket(eArgs);
                }
            }
            catch(Exception ex)
            {
                _logger.Error($"{this.GetType().Name}: Error in ProcessReceive: {ex}");
            }
        }
    }
}
