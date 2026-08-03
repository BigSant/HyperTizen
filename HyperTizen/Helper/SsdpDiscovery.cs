using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HyperTizen.Helper
{
    public static class SsdpDiscovery
    {
        public static (string ip, int port) GetHyperIpAndPort()
        {
            string searchTarget = "urn:hyperhdr.eu:device:basic:1";
            string ssdpRequest =
$@"M-SEARCH * HTTP/1.1
HOST: 239.255.255.250:1900
MAN: ""ssdp:discover""
MX: 2
ST: {searchTarget}

";

            try
            {
                using (UdpClient udpClient = new UdpClient())
                {
                    udpClient.Client.ReceiveTimeout = 5000;

                    IPEndPoint multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    byte[] requestBytes = Encoding.UTF8.GetBytes(ssdpRequest.Replace("\n", "\r\n"));

                    udpClient.Send(requestBytes, requestBytes.Length, multicastEndpoint);

                    DateTime start = DateTime.Now;
                    TimeSpan timeout = TimeSpan.FromSeconds(5);

                    while (DateTime.Now - start < timeout)
                    {
                        if (udpClient.Available > 0)
                        {
                            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                            byte[] responseBytes = udpClient.Receive(ref remoteEP);
                            string response = Encoding.UTF8.GetString(responseBytes);

                            if (response.ToLower().Contains(searchTarget.ToLower()))
                            {
                                Match locationMatch = Regex.Match(response, @"LOCATION:\s*(http://[^\s]+)", RegexOptions.IgnoreCase);
                                Match portMatch = Regex.Match(response, @"HYPERHDR-FBS-PORT:\s*(\d+)", RegexOptions.IgnoreCase);

                                if (locationMatch.Success && portMatch.Success)
                                {
                                    Uri locationUri = new Uri(locationMatch.Groups[1].Value);
                                    string ip = locationUri.Host;
                                    int port = int.Parse(portMatch.Groups[1].Value);
                                    return (ip, port);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, "SsdpDiscovery.GetHyperIpAndPort() Exception: " + ex.Message);
            }

            return (null, 0);
        }
    }
}