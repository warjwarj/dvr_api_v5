using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace dvr_api
{
    public class API_Server : AsyncSocketServer<APIClient>
    {
        // priv
        private HeapObjectPool<APIRequest> _apiReqPool;

        public API_Server(int numConnections, int IOBuffersize, IPEndPoint endpoint, ObjectManager controller, HeapObjectPool<APIRequest> apiReqPool)
            : base(numConnections, IOBuffersize, endpoint, controller)
        {
            // make API req objects and init their bufffer manager
            this._apiReqPool = apiReqPool;
        }

        public override void Init()
        {
            base.Init();
            APIRequest apiReq;
            for (int i = 0; i < this._maxConcurrentConnections; i++)
            {
                apiReq = new APIRequest();
                _apiReqPool.Push(apiReq);
            }
        }

        protected override void setupLogic(SocketAsyncEventArgs eArgs)
        {
            DataHolder? dh = (DataHolder)eArgs.UserToken;
            if (dh != null && dh.client != null && eArgs.Buffer != null)
            {
                bool addedSuccessfully = _clientManager.Add(dh);
                _logger.Debug($"{this.GetType().Name}: Registered client connection in controller: {addedSuccessfully}");
                dh.client.setupComplete = true;
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
                    DataHolder dh = (DataHolder)eArgs.UserToken;
                    APIClient client = (APIClient)dh.client;
                    
                    // send at the end of the function.
                    string response = null;

                    if (dh != null && client != null && eArgs.Buffer != null)
                    {
                        // we don't need any data to do this, so just do it before anything else.
                        if (!client.setupComplete)
                        {
                            setupLogic(eArgs); // <-- this will flip the setup complete variable according to internal logic
                        }
                        // check if data is a handshake
                        if (!Regex.IsMatch(Encoding.ASCII.GetString(eArgs.Buffer[eArgs.Offset..(eArgs.Offset + 3)]), "^GET"))
                        {
                            int decodedLength = eArgs.BytesTransferred;
                            TcpUtils.Opcode opcode = TcpUtils.Opcode.text;
                            if (client.clientType == ClientType.APIClientWebsocket)
                            {
                                (decodedLength, opcode) = TcpUtils.DecodeMessage(ref eArgs);
                            }
                            switch (opcode)
                            {
                                case TcpUtils.Opcode.text:
                                byte[] bytes = eArgs.Buffer[eArgs.Offset..(eArgs.Offset + decodedLength)];
                                int messageStart = 0;
                                for (int i = 0; i < bytes.Length; i++)
                                {
                                    // carrige return
                                    if (bytes[i] == 0b00001101 || bytes[i] == 0b00001010)
                                    {
                                        string message = Encoding.ASCII.GetString(bytes[messageStart..i]);
                                        string dev_id = Utils.getMDVRIdFromMessage(message);

                                        _logger.Debug($"{this.GetType().Name}: Received from {client.id}: {message}");
                                        if (dev_id != null && _clientManager.Contains(dev_id))
                                        {
                                            DataHolder? device_dh = _clientManager.GetRef(dev_id);
                                            if (device_dh != null)
                                            {
                                                APIRequest apiReq = _apiReqPool.Pop();
                                                apiReq.Init(
                                                    dh.client.socket,
                                                    device_dh.client.socket,
                                                    message
                                                    );
                                                device_dh.APIReqQueue.Enqueue(apiReq);
                                                // this is a bit risky because we are assuming that the bytes sent can be read as ASCII.
                                                //...as the DVR protocol is ASCII based there should never really be a case where the client
                                                //...is sending non-ASCII readable data.
                                                device_dh.client.socket.Send(bytes[messageStart..(i+1)]);
                                            }
                                            else
                                            {
                                                throw new NullReferenceException("Device DH retreived from client manager was null.");
                                            }
                                        }
                                        else
                                        {
                                            
                                        }
                                        messageStart = i;
                                    }
                                }
                                    break;
                                case TcpUtils.Opcode.close:
                                    eArgs.SocketError = SocketError.ConnectionRefused;
                                    break;
                                case TcpUtils.Opcode.ping:
                                    _logger.Info("Didn't expect a ping frame.");
                                    break;
                                case TcpUtils.Opcode.pong:
                                    _logger.Info("Didn't expect a pong frame.");
                                    break;
                                case TcpUtils.Opcode.bin:
                                    _logger.Info("Didn't expect a bin frame.");
                                    break;
                                case TcpUtils.Opcode.unrecognised:
                                    _logger.Info("Frame opcode unrecognised.");
                                    break;
                                default:
                                    _logger.Error("WS message wasn't sent with a recognised opcode.");
                                    break;
                            }

                            // if needed send a message to the client 
                            if (response != null)
                            {
                                // send response to client.
                                // TODO ENDOCDE MESSAGE
                                // int responseLen = Encoding.ASCII.GetBytes(
                                //     response,
                                //     0,
                                //     response.Length,
                                //     eArgs.Buffer,
                                //     eArgs.Offset
                                //     );
                                // eArgs.SetBuffer(eArgs.Offset, responseLen);
                                // bool completedSynchronusly = client.socket.SendAsync(eArgs);
                                // eArgs.SetBuffer(eArgs.Offset, _socketBufferSize);
                                // client.setupComplete = true;
                                // if (!completedSynchronusly)
                                // {
                                //     ProcessSend(eArgs);
                                // }
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
                            int handshakeLength = TcpUtils.PrepWSHandshake(ref eArgs);
                            if (handshakeLength != int.MaxValue)
                            {
                                // REMEMBER TO RESET THE BUFFER SIZE DAMMIT
                                eArgs.SetBuffer(eArgs.Offset, handshakeLength);
                                bool completedSynchronusly = client.socket.SendAsync(eArgs);
                                eArgs.SetBuffer(eArgs.Offset, _socketBufferSize);
                                if (!completedSynchronusly)
                                {
                                    ProcessSend(eArgs);
                                }
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
                    //_logger.Warn($"Closing socket in ProcessReceive, Error: {receiveArgs.SocketError}, BytesTransferred: {receiveArgs.BytesTransferred}");
                    CloseClientSocket(eArgs);
                }
            }
            catch(Exception ex)
            {
                _logger.Error($"{this.GetType().Name}: Error in ProcessReceive: {ex}");
                CloseClientSocket(eArgs);
            }
        }

        // /// <summary>
        // /// Lets assume for now that the client is going to use websocket protocol
        // /// </summary>
        // /// <param name="eArgs"></param>
        // protected override void ProcessReceive(SocketAsyncEventArgs eArgs)
        // {
        //     try
        //     {
        //         var dh = (DataHolder)eArgs.UserToken;
        //         var client = (APIClient)dh.client;
        //         if (dh != null && client != null && eArgs.Buffer != null)
        //         {
        //             if (!_clientManager.Contains(dh.client.id))
        //             {
        //                 setupLogic(eArgs);
        //             }
        //             if (!Regex.IsMatch(Encoding.ASCII.GetString(eArgs.Buffer[eArgs.Offset..(eArgs.Offset + 3)]), "^GET"))
        //             {
        //                 int decodedLength = eArgs.BytesTransferred;
        //                 TcpUtils.Opcode opcode = TcpUtils.Opcode.text;
        //                 if (client.clientType == ClientType.APIClientWebsocket)
        //                 {
        //                     (decodedLength, opcode) = TcpUtils.DecodeMessage(ref eArgs);
        //                 }
        //                 switch (opcode)
        //                 {
        //                     case TcpUtils.Opcode.text:
        //                         byte[] bytes = eArgs.Buffer[eArgs.Offset..(eArgs.Offset + decodedLength)];
        //                         int messageStart = 0;
        //                         for (int i = 0; i < bytes.Length; i++)
        //                         {
        //                             // carrige return
        //                             if (bytes[i] == 0b00001101 || bytes[i] == 0b00001010)
        //                             {
        //                                 string message = Encoding.UTF8.GetString(bytes[messageStart..i]);
        //                                 string dev_id = Utils.getMDVRIdFromMessage(message);

        //                                 _logger.Info($"{this.GetType().Name}: Received from {client.id}: {message}");
        //                                 if (dev_id != null && _clientManager.Contains(dev_id))
        //                                 {
        //                                     DataHolder? device_dh = _clientManager.GetRef(dev_id);
        //                                     if (device_dh != null)
        //                                     {
        //                                         APIRequest apiReq = _apiReqPool.Pop();
        //                                         apiReq.Init(
        //                                             dh.client.socket,
        //                                             device_dh.client.socket,
        //                                             message
        //                                             );
        //                                         device_dh.APIReqQueue.Enqueue(apiReq);
        //                                         device_dh.client.socket.Send(bytes[messageStart..i]);
        //                                         Console.WriteLine(Encoding.ASCII.GetString(bytes[messageStart..i]));
        //                                     }
        //                                     else
        //                                     {
        //                                         _logger.Debug($"Request made for device {dev_id} that isn't connected");
        //                                     }
        //                                 }
        //                                 messageStart = i;
        //                             }
        //                         }
        //                         break;
        //                     case TcpUtils.Opcode.close:
        //                         eArgs.SocketError = SocketError.ConnectionRefused;
        //                         break;
        //                     case TcpUtils.Opcode.ping:
        //                         _logger.Info("Didn't expect a ping frame.");
        //                         break;
        //                     case TcpUtils.Opcode.pong:
        //                         _logger.Info("Didn't expect a pong frame.");
        //                         break;
        //                     case TcpUtils.Opcode.bin:
        //                         _logger.Info("Didn't expect a bin frame.");
        //                         break;
        //                     case TcpUtils.Opcode.unrecognised:
        //                         _logger.Info("Frame opcode unrecognised.");
        //                         break;
        //                     default:
        //                         _logger.Error("WS message wasn't sent with a recognised opcode.");
        //                         break;
        //                 }
        //                 base.ProcessReceive(eArgs);
        //             }
        //             else
        //             {
        //                 int handshakeLength = TcpUtils.PrepWSHandshake(ref eArgs);
        //                 if (handshakeLength != int.MaxValue)
        //                 {
        //                     // REMEMBER TO RESET THE BUFFER SIZE DAMMIT
        //                     eArgs.SetBuffer(eArgs.Offset, handshakeLength);
        //                     bool completedSynchronusly = client.socket.SendAsync(eArgs);
        //                     eArgs.SetBuffer(eArgs.Offset, _socketBufferSize);
        //                     if (!completedSynchronusly)
        //                     {
        //                         base.ProcessSend(eArgs);
        //                     }
        //                 }
        //             }
        //         }
        //     }
        //     catch (Exception e)
        //     {
        //         _logger.Error($"API_Server.ProcessReceive: {e}");
        //     }
        // }
    }
}
