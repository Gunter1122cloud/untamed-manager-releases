using System.Windows.Media;

namespace UntamedManager;

// This card models local startup arguments, not a CurseForge-installation claim.
public sealed record ConfiguredModCard(
    string Map,string Id,string Mode,int Order,string Detail,Brush Accent);
