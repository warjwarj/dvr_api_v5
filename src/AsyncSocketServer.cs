using log4net;
using System.Net;
using System.Net.Sockets;

using static dvr_api.Globals;

namespace dvr_api
{
    /// <summary>
    /// Async socket server. pass in the derivation of client that this server will accept.
    /// </summary>
    public abstract class AsyncSocketServer<ClientType>
        where ClientType : Client, new()
    {
        // pub
        // what port are we listening on
        public IPEndPoint endpoint { get; protected set; }

        // prot
        protected ObjectManager _clientManager;                                                                 // client manager
        protected int _totalBytesRead;                                                                          // counter of the total # bytes received by the server
        protected int _maxConcurrentConnections;                                                                // the maximum number of connections the server is designed to handle simultaneously
        protected static readonly ILog _logger = LogManager.GetLogger(typeof(AsyncSocketServer<ClientType>));   // logger!
        protected int _socketBufferSize;                                                                          // buffer size to use for each socket
        
        // priv
        private SAEABufferManager _socketArgsBufferManager;                                                     // represents a large reusable set of buffers. This is used for SocketAsyncEventArgs buffers
        private Socket listenSocket;                                                                            // the socket used to listen for incoming connection requests
        private HeapObjectPool<DataHolder> _opDataPool;                                                         // pool of reusable Client objects for sharing context between socket operations.
        private HeapObjectPool<SocketAsyncEventArgs> _socketAeaPool;                                            // pool of reusable SocketAsyncEventArgs objects for write, read and accept socket operations
        private int _numConnectedSockets;                                                                       // the number of clients connected to the server
        private Semaphore _maxConcurrentClients;                                                                // waithandle. client disconnects, semapohore -= 1 and an accept task completes

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="numConnections">the maximum number of connections the server is designed to handle simultaneously</param>
        /// <param name="receiveBufferSize">buffer size to use for each socket I/O operation</param>
        /// <param name="endpoint">IP + port of the server.</param>
        /// <param name="manager">All connected clients, across the server.</param>
        public AsyncSocketServer(int numConnections, int IOBuffersize, IPEndPoint endpoint, ObjectManager manager)
        {
            this._totalBytesRead = 0;
            this._numConnectedSockets = 0;
            this._maxConcurrentConnections = numConnections;
            this._socketBufferSize = IOBuffersize;
            this._socketArgsBufferManager = new SAEABufferManager(IOBuffersize * numConnections, IOBuffersize);
            // allocate objects in advance.
            this._socketAeaPool = new HeapObjectPool<SocketAsyncEventArgs>(numConnections);
            this._opDataPool = new HeapObjectPool<DataHolder>(numConnections);
            this._maxConcurrentClients = new Semaphore(numConnections, numConnections);
            this.endpoint = endpoint;
            this._clientManager = manager;
        }

        /// <summary>
        /// Initializes the server by preallocating reusable buffers + objects
        /// </summary>
        public virtual void Init() // innit bruv
        {
            // Allocates one large byte buffer which all I/O operations use a piece of. This gaurds
            // against memory fragmentation
            _socketArgsBufferManager.InitBuffer();

            // preallocate pool of SocketAsyncEventArgs + Client objects
            SocketAsyncEventArgs readWriteEventArg;
            DataHolder opData;
            ClientType client;

            for (int i = 0; i < _maxConcurrentConnections; i++)
            {
                readWriteEventArg = new SocketAsyncEventArgs();
                opData = new DataHolder();
                client = new ClientType();

                readWriteEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                opData.client = client;

                // assign a byte buffer from the buffer pool to the SocketAsyncEventArg object
                _socketArgsBufferManager.AllocateBuffer(readWriteEventArg);

                // add SocketAsyncEventArg to the pool
                _socketAeaPool.Push(readWriteEventArg);
                _opDataPool.Push(opData);
            }
            _logger.Info($"{this.GetType().Name} instance initialised. Max connections: {_maxConcurrentConnections}, IO operation buffer size: {_socketBufferSize} bytes, ");
        }

        /// <summary>
        /// prep server socket and start listening for connections.
        /// </summary>
        public void Start()
        {
            listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(endpoint);
            listenSocket.Listen(SOCKET_CONNECTION_BACKLOG);

            // post accepts on the listening socket
            SocketAsyncEventArgs acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(Accept_Completed);
            StartAccept(acceptEventArg);

            _logger.Info($"{this.GetType().Name} instance started, listening on {endpoint}");
        }

        // override these methods to customise the socket server.
        protected virtual void ProcessReceive(SocketAsyncEventArgs receiveArgs)
        {
            if (receiveArgs.SocketError == SocketError.Success && receiveArgs.BytesTransferred != 0)
            {
                DataHolder? dh = (DataHolder)receiveArgs.UserToken;
                if (dh != null && dh.client.socket != null)
                {
                    if (dh.client.id == null)
                    {
                        setupLogic(receiveArgs);
                    }
                    // if data is queued on the socket then the read operation will complete synchronusly
                    bool completedSynchronusly = dh.client.socket.ReceiveAsync(receiveArgs);
                    if (!completedSynchronusly)
                    {
                        ProcessReceive(receiveArgs);
                    }
                }
                else
                {
                    _logger.Warn("Closing socket in ProcessReceive because a value was null");
                    CloseClientSocket(receiveArgs);
                }
            }
            else
            {
                _logger.Warn($"Closing socket in ProcessReceive, Error: {receiveArgs.SocketError}, BytesTransferred: {receiveArgs.BytesTransferred}");
                CloseClientSocket(receiveArgs);
            }
        }

        protected virtual void ProcessSend(SocketAsyncEventArgs eArgs)
        {
            if (eArgs.SocketError == SocketError.Success && eArgs.BytesTransferred != 0)
            {
                // done echoing data back to the client
                var dh = (DataHolder)eArgs.UserToken;
                if (dh != null && dh.client.socket != null)
                {
                    // read the next block of data send from the client
                    bool completedSynchronusly = dh.client.socket.ReceiveAsync(eArgs);
                    if (!completedSynchronusly)
                    {
                        ProcessReceive(eArgs);
                    }
                }
            }
            else
            {
                CloseClientSocket(eArgs);
            }
        }

        /// <summary>
        /// This method is the callback method associated with Socket.AcceptAsync
        /// operations and is invoked when an accept operation is complete.
        /// processes the accepted socket and starts listening for the next connection
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void Accept_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
            StartAccept(e);
        }

        /// <summary>
        /// called whenever a socket operation completes
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the completed receive operation</param>
        protected virtual void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.BytesTransferred != 0 && e.SocketError == SocketError.Success)
            {
                //increment the count of the total bytes receive by the server
                Interlocked.Add(ref _totalBytesRead, e.BytesTransferred);
                _logger.Debug($"{this.GetType().Name} throughput, bytes: {_totalBytesRead}");

                // determine which type of operation just completed and call the associated handler
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        ProcessReceive(e);
                        break;
                    case SocketAsyncOperation.Send:
                        ProcessSend(e);
                        break;
                    default:
                        throw new ArgumentException("The last operation completed on the socket was not a receive or send");
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        /// <summary>
        /// Begins an operation to accept a connection request from the client
        /// </summary>
        /// <param name="acceptEventArgs">Context obj</param>
        protected virtual void StartAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            // loop while the method completes synchronously
            bool completedSynchronusly = false;
            while (!completedSynchronusly)
            {
                _maxConcurrentClients.WaitOne();

                // socket must be cleared since the context object is being reused
                // at this point we will have referenced the socket elsewhere so free to lose this reference.
                acceptEventArgs.AcceptSocket = null;
                completedSynchronusly = listenSocket.AcceptAsync(acceptEventArgs);
                if (!completedSynchronusly)
                {
                    ProcessAccept(acceptEventArgs);
                }
            }
        }

        /// <summary>
        /// Process the connection. Client has already 'connected' at this point.
        /// </summary>
        /// <param name="acceptArgs"></param>
        protected virtual void ProcessAccept(SocketAsyncEventArgs acceptArgs)
        {
            Interlocked.Increment(ref _numConnectedSockets);
            _logger.Info($"{this.GetType().Name}: Client connected. Total connections: {_numConnectedSockets}");

            // Get the socket for the accepted client connection and put it into the
            // ReadEventArg object user token
            SocketAsyncEventArgs readArgs = _socketAeaPool.Pop();
            DataHolder dataholder = _opDataPool.Pop();

            dataholder.client.socket = acceptArgs.AcceptSocket;
            readArgs.UserToken = dataholder;

            bool completedSynchronusly = dataholder.client.socket.ReceiveAsync(readArgs);
            if (!completedSynchronusly)
            {
                ProcessReceive(readArgs);
            }
        }

        /// <summary>
        /// Do stuff that needs to be done on the first connection of the device like set ID and add to device manager
        /// </summary>
        /// <param name="eArgs"></param>
        protected virtual void setupLogic(SocketAsyncEventArgs eArgs)
        {
            DataHolder? dh = (DataHolder)eArgs.UserToken;
            Client? client = dh.client;

            if (dh != null && client != null && eArgs.Buffer != null)
            {
                byte[] bytes = eArgs.Buffer[eArgs.Offset..(eArgs.Offset + eArgs.BytesTransferred)];
                if (client.id == null)
                {
                    client.SetId(bytes);
                }
                if (client.id != null)
                {
                    bool addedSuccessfully = _clientManager.Add(dh);
                    _logger.Debug($"{this.GetType().Name}: Registered client connection in controller: {addedSuccessfully}");
                }
            }
            else
            {
                _logger.Warn("Data needed to do firstReceivedLogic was null, closing socket.");
                CloseClientSocket(eArgs);
            }
        }

        /// <summary>
        /// Called when the client sends 0 bytes.
        /// </summary>
        /// <param name="closeArgs"></param>
        protected virtual void CloseClientSocket(SocketAsyncEventArgs closeArgs)
        {
            DataHolder dh = (DataHolder)closeArgs.UserToken;
            if (dh == null) { return; }

            // close the socket associated with the client
            try
            {
                dh.client.socket.Shutdown(SocketShutdown.Both);
            }
            // throws if client socket has already closed
            catch { }
            
            if (_clientManager.Contains(dh.client.id))
            {
                bool foundAndRemoved = _clientManager.Remove(dh);
                _logger.Debug($"{this.GetType().Name}: Registered client disconnection in controller: {foundAndRemoved}");
            }
            else
            {
                _logger.Warn($"Trying to close a socket that we never registered the connection of. ID: {dh.client.id}");
            }
            
            // decrement the counter keeping track of the total number of clients connected to the server
            Interlocked.Decrement(ref _numConnectedSockets);

            // reset the fields of the object we're reusing
            dh.client.ResetFields();

            // Free the SocketAsyncEventArg + client so they can be reused by another client
            _socketAeaPool.Push(closeArgs);
            _opDataPool.Push(dh);

            // relase a count of the semaphore
            _maxConcurrentClients.Release();
        }
    }
}