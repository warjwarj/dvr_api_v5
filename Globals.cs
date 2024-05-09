using System.Net;

namespace dvr_api
{
    public static class Globals
    {
        // test, dev, prod. Decides what mode we run the application in.
        //public static readonly string RUN_MODE = "test";
        public static readonly string RUN_MODE = "dev";
        //public static readonly string RUN_MODE = "prod";

        // ip and port of the cam server
        public static readonly byte[] CAM_SVR_IP = { 0, 0, 0, 0 };
        public static readonly int CAM_SVR_PORT = 9048;

        // ip and port of the gps server
        public static readonly byte[] GPS_SVR_IP = { 0, 0, 0, 0 };
        // public static readonly byte[] GPS_SVR_IP = { 127, 0, 0, 1 };
        public static readonly int GPS_SVR_PORT = 9047;

        // ip and port of the main api server
        public static readonly byte[] API_SVR_IP = { 0, 0, 0, 0 };
        // public static readonly byte[] API_SVR_IP = { 127, 0, 0, 1 };
        public static readonly int API_SVR_PORT = 9046;
        
            // ip and port of the main api server
        public static readonly string SIGNALR_ENDPOINT = "https://192.168.1.127:5234";

        // number of possible queued connections on a socket
        public static readonly int SOCKET_CONNECTION_BACKLOG = 1000;

        /// <summary>
        /// Per server instance.
        /// </summary>
        public static readonly int MAX_CONCURRENT_CONNECTIONS = 10000;

        /// <summary>
        /// How many API requests do you think will be happening at once per server instance?
        /// </summary>
        public static readonly int MAX_CONCURRENT_APIREQS = 1000000;

        /// <summary>
        /// Practically: the size of the SAEA buffer. Used for both send and receive.
        /// </summary>
        public static readonly int SOCKET_IO_BUFSIZE = 1024;

        // This variable is only really used for testing
        public static readonly IPAddress LOOPBACK_ADDR = IPAddress.Parse("127.0.0.1");
    }
}
