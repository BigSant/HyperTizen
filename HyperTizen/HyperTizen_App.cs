using System;
using Tizen.Applications;
using Tizen.Applications.Notifications;
using System.Threading.Tasks;

namespace HyperTizen
{
    class App : ServiceApplication
    {
        public static HyperionClient client;
        protected override void OnCreate()
        {
            base.OnCreate();

            // STEP 1: Load preferences FIRST (before any testing)
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "false");

            // Older builds auto-enabled capture after selecting any fallback.
            // Reset that persisted value once when upgrading to the safe-start
            // build. The service remains available for WebSocket control and
            // source-side adapters without touching restricted capture APIs.
            if (!Preference.Contains("safeStartupV1"))
            {
                Preference.Set("enabled", "false");
                Preference.Set("safeStartupV1", "true");
            }

            // CRITICAL: Force diagnostic mode based on build constant
            // This OVERRIDES any saved preference to ensure build const is respected
            // Set Globals.DIAGNOSTIC_MODE_ENABLED = true in code to enable diagnostic mode
            Preference.Set("diagnosticMode", Globals.DIAGNOSTIC_MODE_ENABLED ? "true" : "false");

            // STEP 2: Initialize Globals with preferences
            Globals.Instance.LoadPreferencesEarly();

            // STEP 3: Start WebSocket servers
            Helper.Log.StartWebSocketServer(45678);

            // Start WebSocket control server on port 45677 for UI control
            Helper.Log.Write(Helper.eLogType.Info, "Launching control WebSocket server task...");
            Task.Run(async () =>
            {
                try
                {
                    await WebSocket.WebSocketServer.StartServerAsync();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"Control WebSocket server task crashed: {ex.Message}");

                    // Show TV notification so user can see the error even without logs
                    try
                    {
                        Notification crashNotif = new Notification
                        {
                            Title = "WebSocket Critical Error",
                            Content = $"Task crashed: {ex.Message}",
                            Count = 1
                        };
                        NotificationManager.Post(crashNotif);
                    }
                    catch { /* Ignore notification errors */ }
                }
            });

            // Continue startup immediately. Firmware/native capture diagnostics
            // are explicit research operations and must never block OnCreate().
            client = new HyperionClient();

            // Show service started notification (always shown)
            Notification startNotif = new Notification
            {
                Title = "HyperTizen Service",
                Content = "Service started",
                Count = 1
            };
            NotificationManager.Post(startNotif);
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
            // Show service stopped notification (always shown)
            Notification stopNotif = new Notification
            {
                Title = "HyperTizen Service",
                Content = "Service stopped",
                Count = 1
            };
            NotificationManager.Post(stopNotif);

            // Stop WebSocket server
            Helper.Log.StopWebSocketServer();
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
