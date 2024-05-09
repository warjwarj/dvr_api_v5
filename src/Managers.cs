using System.Buffers;
using System.Configuration;
using System.Net.Sockets;
using System.Collections.Concurrent;
using log4net;
using Microsoft.AspNetCore.SignalR;

namespace dvr_api
{
    /// <summary>
    /// control dvr clients
    /// </summary>
    public sealed class ObjectManager
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ObjectManager)); // logger

        private Dictionary<string, DataHolder> holders;
        private object holdersLock = new object();
        private List<string> connectedDevicesLists;
        public IHubContext<DevicesHub>? deviceHubContext;

        public ObjectManager(IHubContext<DevicesHub> deviceHubContext)
        {
            holders = new Dictionary<string, DataHolder>();
            connectedDevicesLists = new List<string>();
            this.deviceHubContext = deviceHubContext;
        }

        public bool Add(DataHolder dh) => AddLogic(dh);
        public bool Remove(DataHolder dh) => RemoveLogic(dh);
        public DataHolder GetRef(string index) => holders?[index];
        public bool Contains(string index) => index == null ? false : holders.ContainsKey(index);

        private bool AddLogic(DataHolder dh)
        {
            try
            {
                lock(holdersLock)
                {
                    if (Contains(dh.client.id))
                    {
                        Remove(dh);
                    }
                    bool addedSuccessfully = holders.TryAdd(dh.client.id, dh);
                    if (dh.client.clientType == ClientType.MDVR)
                    {
                        connectedDevicesLists.Add(dh.client.id);
                        deviceHubContext.Clients.All.SendAsync("UpdateDeviceConnection", dh.client.id);
                    }
                    return addedSuccessfully;
                }
            } catch (Exception ex) { _logger.Error(ex); return false;  }
        }

        private bool RemoveLogic(DataHolder dh)
        {
            try 
            {
                lock(holdersLock)
                {
                    bool removedSuccessfully = holders.Remove(dh.client.id);
                    if (dh.client.clientType == ClientType.MDVR)
                    {
                        connectedDevicesLists.Remove(dh.client.id);
                        deviceHubContext.Clients.All.SendAsync("UpdateDeviceDisconnection", dh.client.id);
                    }
                    return removedSuccessfully;
                }
            } catch (Exception ex) { _logger.Error(ex); return false;  }
        }

        public List<string> GetAllConnectedDevices()
        {
            return connectedDevicesLists;
        }
    }
    
    public sealed class SAEABufferManager
    {
        int _numBytes;                 // the total number of bytes controlled by the buffer pool
        byte[] _buffer;                // the underlying byte array maintained by the Buffer Manager
        Stack<int> _freeIndexPool;     // index's that we can access induvidual buffers from
        int _currentIndex;
        int _bufferSize;

        public SAEABufferManager(int totalBytes, int bufferSize)
        {
            _numBytes = totalBytes;
            _currentIndex = 0;
            _bufferSize = bufferSize;
            _freeIndexPool = new Stack<int>();
        }

        // Allocates buffer space used by the buffer pool
        public void InitBuffer()
        {
            // create one big large buffer and divide that
            // out to each SocketAsyncEventArg object
            _buffer = new byte[_numBytes];
        }

        // Assigns a buffer from the buffer pool to the
        // specified SocketAsyncEventArgs object
        //
        // <returns>true if the buffer was successfully set, else false</returns>
        public bool AllocateBuffer(SocketAsyncEventArgs args)
        {
            if (_freeIndexPool.Count > 0)
            {
                args.SetBuffer(_buffer, _freeIndexPool.Pop(), _bufferSize);
            }
            else
            {
                if (_numBytes - _bufferSize < _currentIndex)
                {
                    return false;
                }
                args.SetBuffer(_buffer, _currentIndex, _bufferSize);
                _currentIndex += _bufferSize;
            }
            return true;
        }
        // Removes the buffer from a SocketAsyncEventArg object.
        // This frees the buffer back to the buffer pool
        public void FreeBuffer(SocketAsyncEventArgs args)
        {
            _freeIndexPool.Push(args.Offset);
            args.SetBuffer(null, 0, 0);
        }
    }

    public sealed class HeapObjectPool<T>
    {
        private Stack<T> pool;
        private object poolLock = new object();

        public int Count => pool.Count;

        public HeapObjectPool(int capacity)
        {
            pool = new Stack<T>(capacity);
        }

        public T Pop()
        {
            lock(poolLock)
            {
                return pool.Pop();
            }
        }

        public void Push(T item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("Items added to a SocketAsyncEventArgsPool cannot be null");
            }
            lock (poolLock)
            {
                pool.Push(item);
            }
        }
    }
}
