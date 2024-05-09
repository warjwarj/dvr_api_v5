    using log4net;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;

    using static dvr_api.Utils;
    using static dvr_api.Globals;

    namespace dvr_api
    {

    /*
    *  
    *  Read files and upload as a blob
    *  Presigned URL: https://docs.aws.amazon.com/AmazonS3/latest/userguide/ShareObjectPreSignedURL.html
    * 
    */

    public class Cam_Server
    {
        protected static readonly ILog _logger = LogManager.GetLogger(typeof(Cam_Server));  // logger!
        private Socket CamServerSocket;
        private IPEndPoint endpoint;
        private Dictionary<string, APIRequest> activeRequests;

        public Cam_Server(IPEndPoint endpoint, Dictionary<string, APIRequest> activeRequests)
        {
            CamServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.endpoint = endpoint;
            this.activeRequests = activeRequests;
        }

        public async void Init()
        {
            try
            {
                // start main API server, bind to address
                CamServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                CamServerSocket.Bind(new IPEndPoint(new IPAddress(CAM_SVR_IP), CAM_SVR_PORT));
                CamServerSocket.Listen(SOCKET_CONNECTION_BACKLOG);

                _logger.Info($"Cam server listening on port {CAM_SVR_PORT}...");

                while (true)
                {
                    try
                    {
                        // get socket we're reading from, only need it temporarily.
                        using Socket clientSocket = await CamServerSocket.AcceptAsync();

                        // stream video from the DVR to the API caller
                        // save the video and upload it to the S3 bucket
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error in cam server: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Fatal error in Cam server loop, attempting to restart...Message: {ex}");
            }
            finally
            {
                CamServerSocket.Close();
                Thread.Sleep(1000);
            }
        }

        /*
        * The below is a c struct that defines the packet headers for all the packets sent to the cam server.
        * Fixed at 32 bytes, with each section always takign up the same position of bytes.
        * 
        * Why they allocated only 5 bytes for the device id I don't know.
        * 
        *  typedef struct PacketHeader
        *  {
        *      char magic[7];             // $PACKET
        *      char semicolon1;           // ; 
        *      char devID[5];             // 85000
        *      char semicolon2;           // ;
        *      char metadataInfoLen[6];   // 0xffff
        *      char semicolon3;           // ;
        *      char packetBinLen[10];     // 0xffffffff
        *      char cr;                   // <CR, \r >
        *  }
        * 
        */

        public async Task streamVideoDeviceToClient(Socket readingFrom)
        {
            /*
            * 
            * This function is written to just accept $FILE prefixed packets.
            * TODO: dont bother with networkstream just use socket
            * 
            */

            // header of the packet we're reading
            byte[] raw_header = new byte[32];

            // buffers
            byte[] payload_header_len_buffer = new byte[6];
            byte[] payload_body_len_buffer = new byte[10];

            // flag for first packet received 
            bool firstpacket = true;

            // use this string to match the video being streamed from the device to the video request task
            string req_match_string = null;

            // the request we can match to
            APIRequest vidreq = null;

            try
            {
                while (true)
                {
                    lock (readingFrom)
                    {
                        // get outer packet header bytes
                        readingFrom.Receive(raw_header);

                        // return if no more recordings available in timframe specified - see README - 'Handling gaps in recording'
                        if (section(Encoding.ASCII.GetString(raw_header), 0) == "$FILEEND")
                        {
                            break;
                        }

                        // get lengths of inner packet and body
                        Array.Copy(raw_header, 14, payload_header_len_buffer, 0, 6);
                        Array.Copy(raw_header, 21, payload_body_len_buffer, 0, 10);
                        int payload_header_len = Convert.ToInt32(Encoding.ASCII.GetString(payload_header_len_buffer), 16);
                        int payload_body_len = Convert.ToInt32(Encoding.ASCII.GetString(payload_body_len_buffer), 16);

                        // read the exact amount of bytes into each variable, get header as string, and get string for indexing vid req
                        byte[] payload_header = new byte[payload_body_len];
                        readingFrom.Receive(payload_header);
                        string payload_header_string = Encoding.ASCII.GetString(payload_header);

                        // first iteration, before we go any further perfom checks
                        if (firstpacket)
                        {
                            // Match the video request
                            req_match_string = getReqMatchStringFromFilePacketHeader(payload_header_string);

                            // check that we acn find who requested the video
                            if (activeRequests.ContainsKey(req_match_string))
                            {
                                try
                                {
                                    vidreq = activeRequests[req_match_string];
                                    //streamingTo = vidreq.Requester.getNetworkStream;
                                }
                                catch (Exception e)
                                {
                                    _logger.Error($"Matched video to a request but either failed to retreive the video or create a networkstream from the Requester socket. {e}");
                                    break;
                                }
                            }
                            else
                            {
                                _logger.Error($"Couldn't match received video {payload_header_string} to a request using reqmatch {req_match_string}, aborting stream function.");
                                break;
                            }

                            if (section(payload_header_string, 0) != "$FILE")
                            {
                                _logger.Error($"Expected $FILE packets, not {payload_header_string}, aborting stream function.");
                                break;
                            }
                            firstpacket = false;
                        }

                        // read body after prev checks incase we don't need to.
                        // already read the header so can remove the length from the amount of bytes we want.
                        byte[] payload_body = new byte[payload_body_len - payload_header_len];
                        readingFrom.Receive(payload_body);

                        //~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

                        // send the packet to the client.
                        // here is where where we save it.

                        string filePath = "C:/";

                        // Write bytes to the file
                        try
                        {
                            using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
                            {
                                fileStream.Write(payload_body, 0, payload_body.Length);
                            }

                            Console.WriteLine("Bytes have been written to the file successfully.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("An error occurred: " + ex.Message);
                        }

                        //~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

                        // the datetime of the received packet - starttime + seconds
                        DateTime packet_datetime = formatIntoDateTime(section(payload_header_string, 7)).AddSeconds(int.Parse(section(payload_header_string, 8)));

                        // if the video packet is dated to be equal or sooner than expected, return the List we have been aggregating.
                        if (packet_datetime >= vidreq.EndTimeVideoPacket)
                        {
                            activeRequests.Remove(req_match_string);
                            break;
                        }
                    }
                    readingFrom.Dispose();
                    return;
                }
            }
            catch (NullReferenceException nullrefex)
            {
                _logger.Error($"Null reference exception, a variable that was critical to the function was not set to a meaningful value {nullrefex}");
            }
            catch (SocketException sockex)
            {
                _logger.Error($"Could not stream video from device to client due to a socket error. {sockex}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Generic error in streamVideoDeviceToClient: {ex}");
            }
            return;
        }
    }

    // public class Cam_Server
    // {
    //     protected static readonly ILog _logger = LogManager.GetLogger(typeof(Cam_Server));  // logger!

    //     private Socket CamServerSocket;

    //     public Cam_Server(IPEndPoint endpoint)
    //     {
    //         CamServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    //     }

    //     public async void Init(object quit)
    //     {
    //         try
    //         {
    //             // cast because threading delegate param is obj
    //             CancellationToken server_quit_token = (CancellationToken)quit;

    //             // start main API server, bind to address
    //             CamServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    //             CamServerSocket.Bind(new IPEndPoint(new IPAddress(CAM_SVR_IP), CAM_SVR_PORT));
    //             CamServerSocket.Listen(SOCKET_CONNECTION_BACKLOG);

    //             _logger.Info($"Cam server listening on port {CAM_SVR_PORT}...");

    //             while (!server_quit_token.IsCancellationRequested)
    //             {
    //                 try
    //                 {
    //                     // get socket we're reading from, only need it temporarily.
    //                     Socket clientSocket = await CamServerSocket.AcceptAsync();

    //                     // stream video from the DVR to the API caller
    //                     _ = streamVideoDeviceToClient(clientSocket);
    //                 }
    //                 catch (Exception ex)
    //                 {
    //                     _logger.Error($"Error in cam server: {ex}");
    //                 }
    //             }
    //         }
    //         catch (Exception ex)
    //         {
    //             _logger.Error($"Fatal error in Cam server loop, attempting to restart...Message: {ex}");
    //         }
    //         finally
    //         {
    //             CamServerSocket.Close();
    //             Thread.Sleep(1000);
    //         }
    //     }

    //     /*
    //     * The below is a c struct that defines the packet headers for all the packets sent to the cam server.
    //     * Fixed at 32 bytes, with each section always takign up the same position of bytes.
    //     * 
    //     * Why they allocated only 5 bytes for the device id I don't know.
    //     * 
    //     *  typedef struct PacketHeader
    //     *  {
    //     *      char magic[7];             // $PACKET
    //     *      char semicolon1;           // ; 
    //     *      char devID[5];             // 85000
    //     *      char semicolon2;           // ;
    //     *      char metadataInfoLen[6];   // 0xffff
    //     *      char semicolon3;           // ;
    //     *      char packetBinLen[10];     // 0xffffffff
    //     *      char cr;                   // <CR, \r >
    //     *  }
    //     * 
    //     */

    //     public static async Task streamVideoDeviceToClient(Socket readingFrom)
    //     {
    //         /*
    //         * 
    //         * This function is written to just accept $FILE prefixed packets.
    //         * TODO: dont bother with networkstream just use socket
    //         * 
    //         */

    //         // header of the packet we're reading
    //         byte[] raw_header = new byte[32];

    //         // buffers
    //         byte[] payload_header_len_buffer = new byte[6];
    //         byte[] payload_body_len_buffer = new byte[10];

    //         // flag for first packet received 
    //         bool firstpacket = true;

    //         // use this string to match the video being streamed from the device to the video request task
    //         string req_match_string = null;

    //         // the request we can match to
    //         HistoricVideoRequest vidreq = null;

    //         try
    //         {
    //             while (true)
    //             {
    //                 lock (readingFrom)
    //                 {
    //                     // get outer packet header bytes
    //                     readingFrom.Receive(raw_header);

    //                     // return if no more recordings available in timframe specified - see README - 'Handling gaps in recording'
    //                     if (section(Encoding.ASCII.GetString(raw_header), 0) == "$FILEEND")
    //                     {
    //                         break;
    //                     }

    //                     // get lengths of inner packet and body
    //                     Array.Copy(raw_header, 14, payload_header_len_buffer, 0, 6);
    //                     Array.Copy(raw_header, 21, payload_body_len_buffer, 0, 10);
    //                     int payload_header_len = Convert.ToInt32(Encoding.ASCII.GetString(payload_header_len_buffer), 16);
    //                     int payload_body_len = Convert.ToInt32(Encoding.ASCII.GetString(payload_body_len_buffer), 16);

    //                     // read the exact amount of bytes into each variable, get header as string, and get string for indexing vid req
    //                     byte[] payload_header = new byte[payload_body_len];
    //                     readingFrom.Receive(payload_header);
    //                     string payload_header_string = Encoding.ASCII.GetString(payload_header);

    //                     // first iteration, before we go any further perfom checks
    //                     if (firstpacket)
    //                     {
    //                         // Match the video request
    //                         req_match_string = getReqMatchStringFromFilePacketHeader(payload_header_string);

    //                         // check that we acn find who requested the video
    //                         if (serverJobController.CheckForObject(req_match_string))
    //                         {
    //                             try
    //                             {
    //                                 vidreq = (HistoricVideoRequest)serverJobController.GetObject(req_match_string);
    //                                 //streamingTo = vidreq.Requester.getNetworkStream;
    //                             }
    //                             catch (Exception e)
    //                             {
    //                                 _logger.Error($"Matched video to a request but either failed to retreive the video or create a networkstream from the Requester socket. {e}");
    //                                 break;
    //                             }
    //                         }
    //                         else
    //                         {
    //                             _logger.Error($"Couldn't match received video {payload_header_string} to a request using reqmatch {req_match_string}, aborting stream function.");
    //                             break;
    //                         }

    //                         if (section(payload_header_string, 0) != "$FILE")
    //                         {
    //                             _logger.Error($"Expected $FILE packets, not {payload_header_string}, aborting stream function.");
    //                             break;
    //                         }
    //                         firstpacket = false;
    //                     }

    //                     // read body after prev checks incase we don't need to.
    //                     // already read the header so can remove the length from the amount of bytes we want.
    //                     byte[] payload_body = new byte[payload_body_len - payload_header_len];
    //                     readingFrom.Receive(payload_body);

    //                     _logger.Error($"Streamed {Encoding.ASCII.GetString(payload_header)} {Encoding.ASCII.GetString(payload_body)} to API client");


    //                     // send the packet to the client. 
    //                     vidreq.Requester.SendBytes([.. payload_header, .. payload_body]);

    //                     // the datetime of the received packet - starttime + seconds
    //                     DateTime packet_datetime = formatIntoDateTime(section(payload_header_string, 7)).AddSeconds(int.Parse(section(payload_header_string, 8)));

    //                     // if the video packet is dated to be equal or sooner than expected, return the List we have been aggregating.
    //                     if (packet_datetime >= vidreq.EndTime)
    //                     {
    //                         serverJobController.FinaliseAndRemoveObject(req_match_string);
    //                         break;
    //                     }
    //                 }
    //                 readingFrom.Dispose();
    //                 return;
    //             }
    //         }
    //         catch (NullReferenceException nullrefex)
    //         {
    //             Log($"Null reference exception, a variable that was critical to the function was not set to a meaningful value {nullrefex}", LogType.Error);
    //         }
    //         catch (SocketException sockex)
    //         {
    //             Log($"Could not stream video from device to client due to a socket error. {sockex}", LogType.Error);
    //         }
    //         catch (Exception ex)
    //         {
    //             Log($"Generic error in streamVideoDeviceToClient: {ex}", LogType.Error);
    //         }
    //         return;
    //     }
    // }
}
