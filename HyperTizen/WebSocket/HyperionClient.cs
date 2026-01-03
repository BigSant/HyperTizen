using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Threading;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        public HyperionClient()
        {
            if (!Preference.Contains("rpcServer")) return;
            client = new WebSocketClient(Preference.Get<string>("rpcServer"));
            Task.Run(() => client.ConnectAsync());
            Task.Run(() => Start());
        }

        public void UpdateURI(string uri)
        {
            if (client?.client != null && client.client.State == WebSocketState.Open)
            {
                client.client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }

            client = new WebSocketClient(uri);
            Task.Run(() => client.ConnectAsync());
        }

        public async Task Start(bool shouldStart = false)
        {
            if (client != null && Capturer.GetCondition())
            {
                while (App.Configuration.Enabled || shouldStart)
                {
                    Color[] colors = Capturer.GetColors();
                    string image = Capturer.ToImage(colors);

                    if (client?.client?.State == WebSocketState.Open)
                    {
                        ImageCommand imgCmd = new ImageCommand(image);
                        string message = JsonConvert.SerializeObject(imgCmd);
                        var buffer = Encoding.UTF8.GetBytes(message);
                        await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    if (App.Configuration.Enabled && shouldStart) shouldStart = false;
                    else if (!App.Configuration.Enabled && shouldStart) App.Configuration.Enabled = true;
                }
            }
        }

        public async Task Stop()
        {
            if (App.Configuration.Enabled) App.Configuration.Enabled = false;
            Color[] colors = Capturer.GetColors();
            string image = Capturer.ToImage(colors);

            if (client?.client?.State == WebSocketState.Open)
            {
                ImageCommand imgCmd = new ImageCommand(image);
                string message = JsonConvert.SerializeObject(imgCmd);
                var buffer = Encoding.UTF8.GetBytes(message);
                await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}