namespace UntamedManager.Core;

// Compatibility literals are data contracts, not product branding. Keep these
// byte-identical. Origin/licensing history is recorded in docs/internal/.
internal static class VaultProtocol
{
    public const string ClusterId=OriginalStoreRuntimeIdentity.ClusterId;
    public const string ArchiveCoordinationPrefix="SHARED/"+OriginalStoreRuntimeIdentity.CoordinationDirectoryName+"/";
    public const string HistoricalManagerPrefix=OriginalStoreRuntimeIdentity.ProfileDirectoryName+"/";
    public const string HistoricalLocalPrefix="Ryzen-"+OriginalStoreRuntimeIdentity.ProfileDirectoryName+"/";
    public const string ProtectedNameToken=OriginalStoreRuntimeIdentity.ProfileDirectoryName;
}
