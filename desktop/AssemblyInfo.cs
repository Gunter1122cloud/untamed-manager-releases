using System.Runtime.CompilerServices;

// Only the isolated Windows test executable may exercise native internal
// Source RCON protocol framing directly. No new public production API.
[assembly: InternalsVisibleTo("DesktopBridgeTests")]
