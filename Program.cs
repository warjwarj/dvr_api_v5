using dvr_api;
using Microsoft.AspNetCore.SignalR;

using static dvr_api.Globals;

public class Program
{
    public static void Main(string[] args)
    {
        try
        {
            var dvr_api = new DVR_API();
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddSignalR();
            builder.Services.AddCors();

            // Singleton == it won't be created again for each request
            builder.Services.AddSingleton<DevicesHub>(provider => new DevicesHub(dvr_api));

            var app = builder.Build();


            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
                app.UseCors(x => x
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .SetIsOriginAllowed(origin => true)// allow any origin
                    .AllowCredentials()); // allow credentials
            }

            app.UseStaticFiles();
            app.UseRouting();
            app.MapFallbackToFile("index.html");
            app.MapHub<DevicesHub>("/devicesHub");

            // so we can send messages from within the dvr_api --> ClientManager
            IHubContext<DevicesHub>? deviceHubContext = (IHubContext<DevicesHub>)app.Services.GetService(typeof(IHubContext<DevicesHub>));

            dvr_api.Init(deviceHubContext);
            dvr_api.Run();
            //app.Run(SIGNALR_ENDPOINT);
            app.Run();
        }
        catch (Exception e)
        {
            Console.WriteLine("Error in main: " + e);
        }
    }
}