using System.Net;

namespace CabinetNC.Cloud.Api.Http;

/// <summary>
/// Networks whose <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> the API believes. Anything else keeps the
/// socket address, so a client cannot spoof its way out of a rate-limit bucket. Empty = no proxy in front.
/// </summary>
public static class TrustedProxies
{
    public const string Variable = "CABINETNC_TRUSTED_PROXY_CIDRS";

    public static IReadOnlyList<IPNetwork> FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(Variable));

    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var networks = new List<IPNetwork>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(raw, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(raw, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32));
            }
            else
            {
                throw new InvalidOperationException($"{Variable} contains an entry that is not an IP address or CIDR.");
            }
        }
        return networks;
    }
}
