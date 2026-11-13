namespace UntamedManager.Core;

// Compatibility literals are data contracts, not product branding. Keep these
// byte-identical. Origin/licensing history is recorded in docs/internal/.
internal static class VaultProtocol
{
    public const string ClusterId="KovaCluster";
    public const string ArchiveCoordinationPrefix="SHARED/.kova-store-v1/";
    public const string HistoricalManagerPrefix="Kova/";
    public const string HistoricalLocalPrefix="Ryzen-Kova/";
    public const string ProtectedNameToken="Kova";
}
