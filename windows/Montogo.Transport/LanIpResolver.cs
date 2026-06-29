using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Montogo.Transport;

/// <summary>
/// Picks the most appropriate local IPv4 address for the LAN interface where
/// the Mac will connect.  Used to bind the UDP listener to a specific interface
/// instead of 0.0.0.0, reducing the attack surface.
/// </summary>
public static class LanIpResolver
{
    /// <summary>
    /// Returns the best LAN address and a nullable warning string.
    /// Warning is null when exactly one clean candidate was found.
    /// Warning is non-null for fallback (no candidates) or ambiguity (multiple).
    /// </summary>
    public static (IPAddress Address, string? Warning) GetLanAddress()
    {
        var candidates = new List<(string NicName, IPAddress Address)>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (IsVirtualAdapter(nic)) continue;

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IsPrivateRange(unicast.Address))
                    candidates.Add((nic.Name, unicast.Address));
            }
        }

        return candidates.Count switch
        {
            0 => (IPAddress.Any,
                  "No private LAN address found — falling back to 0.0.0.0 (all interfaces)."),

            1 => (candidates[0].Address, null),

            _ => (candidates[0].Address,
                  $"Multiple LAN interfaces found; binding to {candidates[0].Address} " +
                  $"({candidates[0].NicName}). Others: " +
                  string.Join(", ", candidates.Skip(1).Select(c => $"{c.Address} ({c.NicName})")))
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        // Check both Description and Name: Hyper-V adapters surface the keyword in
        // the description ("Hyper-V Virtual Ethernet Adapter") while the interface
        // name is "vEthernet (Default Switch)" or "vEthernet (WSL)".
        return ContainsAny(nic.Description, "Virtual", "Hyper-V", "WSL", "VirtualBox")
            || ContainsAny(nic.Name,        "vEthernet", "WSL");

        static bool ContainsAny(string source, params string[] keywords) =>
            keywords.Any(k => source.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrivateRange(IPAddress addr)
    {
        byte[] b = addr.GetAddressBytes();
        return b[0] == 10                                  // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168);              // 192.168.0.0/16
    }
}
