using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BetterWebLauncher
{
    public sealed class PppStatus
    {
        public bool Connected;
        public string Ip = "-";
        public long BytesSent;
        public long BytesReceived;
    }

    /// <summary>基于网络接口状态判断 PPPoE 连接是否在线，并读取 IP / 流量计数。</summary>
    public static class NetworkMonitor
    {
        public static PppStatus GetStatus(string connectionName)
        {
            var result = new PppStatus();

            NetworkInterface? byName = null;
            NetworkInterface? anyPpp = null;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    bool isPpp = ni.NetworkInterfaceType == NetworkInterfaceType.Ppp;
                    bool nameMatch = !string.IsNullOrEmpty(connectionName) &&
                        (string.Equals(ni.Name, connectionName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(ni.Description, connectionName, StringComparison.OrdinalIgnoreCase));

                    if (isPpp && nameMatch) { byName = ni; break; }
                    if (isPpp && anyPpp == null) anyPpp = ni;
                    if (nameMatch && byName == null) byName = ni;
                }

                var ni2 = byName ?? anyPpp;
                if (ni2 == null) return result;

                result.Connected = true;

                try
                {
                    foreach (var ua in ni2.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.Ip = ua.Address.ToString();
                            break;
                        }
                    }
                }
                catch { }

                try
                {
                    var stats = ni2.GetIPv4Statistics();
                    result.BytesSent = stats.BytesSent;
                    result.BytesReceived = stats.BytesReceived;
                }
                catch { }
            }
            catch { }

            return result;
        }
    }
}