using Tizen.Applications;
using Tizen.Applications.Notifications;
using Tizen.System;
using HyperTizen.WebSocket;
using System.Threading.Tasks;

namespace HyperTizen
{
    class App : ServiceApplication
    {
        public static HyperionClient client;
        protected override void OnCreate()
        {
            base.OnCreate();
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "false");
            Task.Run(() => WebSocketServer.StartServerAsync());
            Display.StateChanged += Display_StateChanged;
            client = new HyperionClient();
        }

        private void Display_StateChanged(object sender, DisplayStateChangedEventArgs e)
        {
            if (e.State == DisplayState.Off)
            {
                Task.Run(() => client.Stop());
            } else if (e.State == DisplayState.Normal)
            {
                Configuration.Enabled = bool.Parse(Preference.Get<string>("enabled"));
                Task.Run(() => client.Start());
            }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
        }

        protected override void OnDeviceOrientationChanged(DeviceOrientationEventArgs e)
        {
            base.OnDeviceOrientationChanged(e);
        }

        protected override void OnLocaleChanged(LocaleChangedEventArgs e)
        {
            base.OnLocaleChanged(e);
        }

        protected override void OnLowBattery(LowBatteryEventArgs e)
        {
            base.OnLowBattery(e);
        }

        protected override void OnLowMemory(LowMemoryEventArgs e)
        {
            base.OnLowMemory(e);
        }

        protected override void OnRegionFormatChanged(RegionFormatChangedEventArgs e)
        {
            base.OnRegionFormatChanged(e);
        }

        protected override void OnTerminate()
        {
            base.OnTerminate();
        }

        static void Main(string[] args)
        {
            App app = new App();
            app.Run(args);
        }

        public static class Configuration
        {
            public static string RPCServer = Preference.Contains("rpcServer") ? Preference.Get<string>("rpcServer") : null;
            public static bool Enabled = bool.Parse(Preference.Get<string>("enabled"));
        }
    }
}
