using HuntReplay.Core.Updates;

namespace HuntReplay.Core.Support;

public enum SupportKind { Link, Address }

/// <summary>One voluntary way to support the project: a web page to open or a wallet address to copy.</summary>
public sealed record SupportChannel(string Name, string Value, SupportKind Kind)
{
    public bool IsLink => Kind == SupportKind.Link;
}

/// <summary>
/// The support options shown under the sidebar menu. Empty values and non-https links are never shown.
/// </summary>
public static class SupportChannels
{
    private static readonly SupportChannel[] All =
    [
        new("GitHub  ★", "https://github.com/" + AppUpdate.Repository, SupportKind.Link),
        new("BTC", "bc1qw832etct0lnqt44kypfafcaja2ct2y87fesmfd", SupportKind.Address),
        new("USDT (TRC20)", "TFjByTWMDwAzK5YjQtZnQGudf9opdTPJvb", SupportKind.Address),
        new("GRAM (TON)", "UQAeC2GV1UvNG1hujzujOTVuyaO5MMMl-BaUMTLM8Zz5lrxF", SupportKind.Address),
        new("Solana (SOL)", "8JGp9eLaCN5HfCP1JDcjCi4wywJD2nSzCECT5UUSyMsD", SupportKind.Address),
    ];

    public static IReadOnlyList<SupportChannel> Available { get; } = Filter(All);

    public static IReadOnlyList<SupportChannel> Filter(IEnumerable<SupportChannel> channels) =>
        channels.Where(c => c.Kind == SupportKind.Link ? IsSafeLink(c.Value) : !string.IsNullOrWhiteSpace(c.Value)).ToArray();

    public static bool IsSafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
