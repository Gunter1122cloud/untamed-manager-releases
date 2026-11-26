// Adapted from DiFuks/ark-manager, commit 63ab649f12efdebd036a8474d8aa1cb676fc5aae,
// src/ArkManager.Core/Services/Rcon/RconErrors.cs (DescribeSocketError).
// MIT License
// Copyright (c) 2026 Dmitry Fuks
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Net.Sockets;

namespace UntamedManager.Core;

internal static class RconTransportDiagnostics
{
    // Copied from upstream; the exception's localized Message is never exposed.
    public static string DescribeSocketError(SocketError code) => code switch
    {
        SocketError.ConnectionRefused =>
            "Connection refused — the server is not listening on the RCON port. " +
            "Most often this means ServerAdminPassword is empty (ASA refuses to open the RCON port " +
            "without one), or the server is still loading.",
        SocketError.TimedOut =>
            "Connection timed out — the server may still be loading, or a firewall is blocking the port.",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
            "RCON host is unreachable.",
        SocketError.HostNotFound =>
            "RCON host not found.",
        SocketError.AccessDenied =>
            "Access denied opening the RCON connection (firewall or security software).",
        SocketError.ConnectionReset =>
            "Connection reset by the server (it closed the socket — likely still loading).",
        _ => $"RCON connection failed (socket error: {code}).",
    };

    // Obsidian adapter: stable prefix; only for read-only request failures.
    internal static string ReadOnlyFailure(SocketException error) =>
        "UM-RCON-TRANSPORT: " + DescribeSocketError(error.SocketErrorCode);
}
