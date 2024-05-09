using log4net;
using Microsoft.AspNetCore.SignalR;
using System.Net;

using static dvr_api.Globals;

[assembly: log4net.Config.XmlConfigurator(ConfigFile = "log4net.config", Watch = true)]
namespace dvr_api
{
    public class DVR_API
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(DVR_API)); // logger!

        ObjectManager clientManager;
        Dictionary<string, APIRequest> activeRequests;
        HeapObjectPool<APIRequest> apiReqPool;

        AsyncSocketServer<MDVR> gpssvr;
        AsyncSocketServer<APIClient> apisvr;

        Cam_Server camsvr;

        public DVR_API()
        {
            apiReqPool = new HeapObjectPool<APIRequest>(MAX_CONCURRENT_APIREQS);
            activeRequests = new Dictionary<string, APIRequest>();
        }

        public void Init(IHubContext<DevicesHub>? deviceHubContext)
        {
            clientManager = new ObjectManager(deviceHubContext);

            gpssvr = new GPS_Server(
                MAX_CONCURRENT_CONNECTIONS,
                SOCKET_IO_BUFSIZE,
                new IPEndPoint(new IPAddress(GPS_SVR_IP), GPS_SVR_PORT),
                clientManager,
                apiReqPool,
                activeRequests
            );

            apisvr = new API_Server(
                MAX_CONCURRENT_CONNECTIONS,
                SOCKET_IO_BUFSIZE,
                new IPEndPoint(new IPAddress(API_SVR_IP), API_SVR_PORT),
                clientManager,
                apiReqPool
            );

            camsvr = new Cam_Server(
                new IPEndPoint(new IPAddress(CAM_SVR_IP), CAM_SVR_PORT),
                activeRequests
            );
        }

        public void Run()
        {
            gpssvr.Init();
            apisvr.Init();

            _ = Task.Run(() => gpssvr.Start());
            _ = Task.Run(() => apisvr.Start());
        }

        public List<string> GetAllConnectedDevices() => clientManager.GetAllConnectedDevices();
    }
}
