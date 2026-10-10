using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace WAR2D.UI
{
    /// <summary>The Play screen's choices: the nickname (remembered between runs) and the join address.</summary>
    public static class MenuModel
    {
        private const string NicknameKey = "nickname", AddressKey = "joinAddress";

        /// <summary>The nickname sent when the local player spawns (sanitised by the server).</summary>
        public static string Nickname
        {
            get => PlayerPrefs.GetString(NicknameKey, "");
            set => PlayerPrefs.SetString(NicknameKey, value ?? "");
        }

        /// <summary>The last address joined.</summary>
        public static string Address
        {
            get => PlayerPrefs.GetString(AddressKey, "");
            set => PlayerPrefs.SetString(AddressKey, value ?? "");
        }

        /// <summary>True for an IPv4/IPv6 address or a DNS host name.</summary>
        public static bool IsAddressValid(string address)
        {
            address = address?.Trim();
            if (string.IsNullOrEmpty(address) || address.Length > 253) return false;
            if (IPAddress.TryParse(address, out IPAddress ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? address.Split('.').Length == 4 : true;
            return Uri.CheckHostName(address) == UriHostNameType.Dns && !address.StartsWith(".") && !address.EndsWith(".");
        }

        /// <summary>This machine's first non-loopback IPv4 address (what others join), or "this machine's address".</summary>
        public static string LocalAddress()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(info.Address))
                            return info.Address.ToString();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not read this machine's address: {e.Message}");
            }
            return "this machine's address";
        }
    }
}
