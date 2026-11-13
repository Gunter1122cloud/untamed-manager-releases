using System;

namespace UntamedManager.Core;

/// <summary>
/// Runtime-only, byte-preserving compatibility with existing two-host Store-v1
/// installations. Names are encoded in source to avoid retired branding in
/// the product repository, but their on-disk values must NEVER be renamed.
/// This class does not create or mutate the original profile/cluster.
/// </summary>
public static class OriginalStoreRuntimeIdentity
{
    public const string ProfileDirectoryName = "\u004Bova";
    public const string ClusterId = "\u004BovaCluster";
    public const string CoordinationDirectoryName = ".\u006Bova-store-v1";

    public static bool IsOriginalClusterId(string? id) =>
        string.Equals(id, ClusterId, StringComparison.OrdinalIgnoreCase);
}
