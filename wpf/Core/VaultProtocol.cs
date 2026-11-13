namespace UntamedManager.Core;

// Compatibility literals are data contracts, not product branding. Keep these
// byte-identical. Origin/licensing history is recorded in docs/internal/.
internal static class VaultProtocol
{
    public const string ClusterId="\u004BovaCluster";
    public const string ArchiveCoordinationPrefix="SHARED/.legacy-store-v1/";
    public const string HistoricalManagerPrefix="Legacy/";
    public const string HistoricalLocalPrefix="Ryzen-Legacy/";
    public const string ProtectedNameToken="\u004Bova";
}
