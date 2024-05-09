using Microsoft.AspNetCore.SignalR;

namespace dvr_api
{
    public class DevicesHub : Hub
    {
        private DVR_API dvr_api;
        public DevicesHub(DVR_API dvr_api)
        {
            this.dvr_api = dvr_api;
        }

        // Fires when a browser client connects
        public override async Task OnConnectedAsync()
        {
            // send the client that connected a list of currently connected devices
            await Clients.Caller.SendAsync(
                "UpdateDeviceList",
                dvr_api.GetAllConnectedDevices()
            );
        }
    }
}
