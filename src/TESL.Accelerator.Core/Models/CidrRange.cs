using System;
using System.Net;

namespace TESL.Accelerator.Core.Models
{
    public class CidrRange
    {
        public string CidrString { get; }
        public uint Network { get; }
        public uint Mask { get; }

        public CidrRange(string cidr)
        {
            CidrString = cidr.Trim();
            var parts = CidrString.Split('/');
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid CIDR format: {cidr}");
            }

            var ip = IPAddress.Parse(parts[0]);
            int prefix = int.Parse(parts[1]);
            byte[] bytes = ip.GetAddressBytes();
            Network = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | (uint)bytes[3];
            Mask = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix);
            Network &= Mask;
        }

        public bool Contains(uint ip) => (ip & Mask) == Network;

        public bool Contains(IPAddress ipAddress)
        {
            byte[] bytes = ipAddress.GetAddressBytes();
            if (bytes.Length != 4) return false;
            uint ipUint = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | (uint)bytes[3];
            return Contains(ipUint);
        }

        public override string ToString() => CidrString;
    }
}
