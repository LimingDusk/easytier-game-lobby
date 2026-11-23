using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameLobby;

internal static class HostNetwork
{
    private static IEnumerable<UnicastIPAddressInformation> ActiveAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

    public static IPAddress Choose() =>
        Choose(ActiveAddresses().Select(a => (a.Address, a.PrefixLength)).ToArray());

    internal static IPAddress Choose((IPAddress Address, int Prefix)[] existing)
    {
        // Prefer EasyTier's usual DHCP subnet. Use another private /24 if a local link overlaps.
        for (int i = 0; i < 65536 + 4096; i++)
        {
            int subnet = (i + 0x7e7e) & 0xffff;
            byte[] bytes = i < 65536 ? [(byte)10, (byte)(subnet >> 8), (byte)subnet, (byte)1]
                : [(byte)172, (byte)(16 + ((i >> 8) & 15)), (byte)i, (byte)1];
            uint candidate = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            bool conflict = existing.Any(link =>
            {
                int prefix = Math.Min(24, link.Prefix);
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                uint address = BinaryPrimitives.ReadUInt32BigEndian(link.Address.GetAddressBytes());
                return (address & mask) == (candidate & mask);
            });
            if (!conflict) return new IPAddress(bytes);
        }
        throw new InvalidOperationException("本机已有网络覆盖了候选房间网段。请检查其他 VPN 或虚拟网卡。");
    }

    public static bool IsAssigned(IPAddress address) => ActiveAddresses().Any(a => a.Address.Equals(address));
}
