using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace EveUpdateHelper;

internal sealed record CdnPrefix(string Cidr, string Region);
internal sealed record CdnAddress(string IP, string Prefix, string Region);
internal sealed record CdnCatalog(List<CdnPrefix> Prefixes, string Date, string Source);

internal static class CloudFrontCatalog
{
    const string Official = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    internal static CdnCatalog Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var prefixes = new List<CdnPrefix>();
        foreach (var item in doc.RootElement.GetProperty("prefixes").EnumerateArray())
        {
            if (item.GetProperty("service").GetString() != "CLOUDFRONT") continue;
            var prefix = new CdnPrefix(item.GetProperty("ip_prefix").GetString()!, item.GetProperty("region").GetString() ?? "GLOBAL");
            Validate(prefix.Cidr); prefixes.Add(prefix);
        }
        if (prefixes.Count is < 1 or > 4096) throw new IOException("CloudFront 网段清单为空或异常。");
        return new CdnCatalog(prefixes.Distinct().ToList(), doc.RootElement.GetProperty("createDate").GetString() ?? "未知日期", source);
    }
    internal static CdnCatalog Embedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("cloudfront-prefixes.json")))!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "随包官方快照");
    }
    internal static async Task<CdnCatalog> Load(Action<string> log, CancellationToken ct, Func<HttpClient>? clientFactory = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = clientFactory?.Invoke() ?? NetworkScanner.Client();
            using var response = await client.GetAsync(Official, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16384]; int n;
            while ((n = await body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + n > 4 * 1024 * 1024) throw new IOException("AWS 网段清单过大。");
                output.Write(buffer, 0, n);
            }
            return Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()), "AWS 官方实时清单");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log("AWS 在线网段暂不可用：" + NetworkScanner.ErrorText(ex) + "；使用随包官方快照继续。"); return Embedded(); }
    }
    static (uint Network, int Bits) Validate(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !NetworkScanner.PublicIPv4(parts[0]) || !int.TryParse(parts[1], out var bits) || bits is < 8 or > 32)
            throw new IOException("无效的公网 IPv4 网段。");
        uint address = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(parts[0]).GetAddressBytes());
        uint mask = bits == 32 ? uint.MaxValue : uint.MaxValue << (32 - bits);
        if ((address & mask) != address) throw new IOException("IPv4 网段边界异常。");
        return (address, bits);
    }
    internal static List<CdnAddress> Sample(IEnumerable<CdnPrefix> prefixes, Random? random = null)
    {
        random ??= Random.Shared;
        var blocks = new Dictionary<(uint, int), CdnPrefix>();
        foreach (var prefix in prefixes)
        {
            var (network, bits) = Validate(prefix.Cidr);
            int sampleBits = Math.Max(24, bits);
            ulong end = (ulong)network + (1UL << (32 - bits));
            ulong step = 1UL << (32 - sampleBits);
            for (ulong current = network; current < end; current += step)
            {
                var key = ((uint)current, sampleBits);
                if (!blocks.TryGetValue(key, out var existing) || existing.Region == "GLOBAL") blocks[key] = prefix;
                if (blocks.Count > 32768) throw new IOException("CloudFront 抽样范围异常，拒绝过大清单。");
            }
        }
        var result = new List<CdnAddress>();
        foreach (var (block, prefix) in blocks)
        {
            uint count = 1U << (32 - block.Item2);
            uint offset = count > 2 ? (uint)random.Next(1, (int)count - 1) : (uint)random.Next((int)count);
            var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, block.Item1 + offset);
            string ip = new IPAddress(bytes).ToString();
            if (NetworkScanner.PublicIPv4(ip)) result.Add(new CdnAddress(ip, prefix.Cidr, prefix.Region));
        }
        // Interleave the entire address space rather than starting with one contiguous region.
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(result));
        return result.DistinctBy(n => n.IP).ToList();
    }
}
