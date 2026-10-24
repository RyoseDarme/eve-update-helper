using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EveUpdateHelper;

internal static class DnsWire
{
    internal static byte[] Query(string domain, ushort id)
    {
        if (!NetworkScanner.Domains.Contains(domain)) throw new ArgumentException("Unexpected DNS name.");
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(Encoding.ASCII.GetBytes(label)); }
        bytes.AddRange([0, 0, 1, 0, 1]);
        return bytes.ToArray();
    }
    internal static string[] Parse(byte[] data, ushort id)
    {
        int U16(int offset) => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)) : throw new IOException("DNS packet truncated.");
        if (data.Length < 12 || U16(0) != id || (data[2] & 0x80) == 0 || (data[2] & 0x02) != 0 || (data[3] & 15) != 0 || U16(4) != 1)
            throw new IOException("DNS response invalid or truncated.");
        int pos = 12;
        void SkipName()
        {
            for (int count = 0; count < 128; count++)
            {
                if (pos >= data.Length) throw new IOException("DNS name truncated.");
                int size = data[pos++];
                if (size == 0) return;
                if ((size & 192) == 192) { if (pos >= data.Length) throw new IOException("DNS pointer truncated."); pos++; return; }
                if (size > 63 || pos + size > data.Length) throw new IOException("DNS label invalid.");
                pos += size;
            }
            throw new IOException("DNS name too long.");
        }
        SkipName();
        if (U16(pos) != 1 || U16(pos + 2) != 1) throw new IOException("DNS question mismatch.");
        pos += 4;
        var addresses = new List<string>();
        int answers = U16(6);
        for (int i = 0; i < answers; i++)
        {
            SkipName();
            int type = U16(pos), cls = U16(pos + 2), size = U16(pos + 8);
            pos += 10;
            if (pos + size > data.Length) throw new IOException("DNS record truncated.");
            if (type == 1 && cls == 1 && size == 4)
            {
                string address = new IPAddress(data.AsSpan(pos, 4)).ToString();
                if (NetworkScanner.PublicIPv4(address)) addresses.Add(address);
            }
            pos += size;
        }
        return addresses.Distinct().ToArray();
    }
    internal static async Task<string[]> Resolve(string domain, string server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(server, 53);
        ushort id = (ushort)Random.Shared.Next(65536);
        await udp.SendAsync(Query(domain, id), timeout.Token);
        var response = await udp.ReceiveAsync(timeout.Token);
        return Parse(response.Buffer, id);
    }
}
