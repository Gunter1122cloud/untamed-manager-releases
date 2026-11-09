using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

// Executable fixture, not a mocked second process: the CI native test EXE
// launches two independent Windows processes that both invoke the real Core
// ReplaceVerifiedSandbox code against ONE new, isolated TEMP target.
// This class is linked into DesktopBridgeTests ONLY, never the Manager host.
internal static class MultiHostTempProcessLeaseTests
{
    internal const string ChildSwitch = "--isolated-temp-restore-lease-child";

    private static void Check(bool pass, string reason)
    {
        if (!pass) throw new InvalidOperationException(
            "MULTI-PROCESS TEMP RESTORE TEST FAILED: " + reason);
        Console.WriteLine("PASS real two-process Windows TEMP restore: " + reason);
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 6 || args[0] != ChildSwitch ||
            args[1] is not ("hold" or "contend")) return 64;
        try
        {
            string mode = args[1], archive = args[2], target = args[3];
            string ownershipFile = args[4], readyFile = args[5];
            var ownership = JsonSerializer.Deserialize<MultiHostBackupOwnershipSnapshot>(
                File.ReadAllText(ownershipFile)) ??
                throw new InvalidDataException("Synthetic ownership was missing");
            MultiHostSandboxRestoreTransaction.ReplaceVerifiedSandbox(
                archive, target, ownership, explicitOperatorApproval: true,
                afterPhaseForTesting: phase =>
                {
                    if (mode != "hold" || phase != "previousMoved") return;
                    File.WriteAllText(readyFile, phase);
                    // The parent intentionally terminates this child while its
                    // OS-exclusive lease is held and the durable journal says
                    // previousMoved. Bounded so a broken parent cannot hang CI.
                    for (int i = 0; i < 1800 && !File.Exists(readyFile + ".release"); i++)
                        Thread.Sleep(50);
                    if (!File.Exists(readyFile + ".release"))
                        throw new TimeoutException("Isolated test release was never observed");
                });
            Console.WriteLine("REPLACEMENT_COMPLETED");
            return mode == "contend" ? 65 : 0;
        }
        catch (IOException ex) when (ex.Message.Contains(
            "UM-TEMP-RESTORE-LEASE-BUSY", StringComparison.Ordinal))
        {
            Console.WriteLine("LEASE_BUSY");
            return args[1] == "contend" ? 0 : 66;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 67;
        }
    }

    private static Process StartChild(string mode, string archive, string target,
        string ownershipFile, string readyFile)
    {
        string host = Environment.ProcessPath ??
            throw new InvalidOperationException("Native test executable path is unavailable");
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // In dotnet-run the current process is dotnet.exe; in an apphost
        // invocation it is DesktopBridgeTests.exe. Both routes launch this
        // exact synthetic native test assembly, not an ASA/Manager process.
        if (Path.GetFileNameWithoutExtension(host)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(ChildSwitch);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add(target);
        info.ArgumentList.Add(ownershipFile);
        info.ArgumentList.Add(readyFile);
        return Process.Start(info) ??
            throw new InvalidOperationException("A second real Windows process was not started");
    }

    private static async Task WaitForExit(Process child, string description)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            throw new TimeoutException("Isolated " + description + " child did not terminate");
        }
    }

    private static async Task WaitForReady(Process holder, string ready)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready) && !holder.HasExited &&
               clock.Elapsed < TimeSpan.FromSeconds(25))
            await Task.Delay(40);
        Check(File.Exists(ready) && !holder.HasExited &&
              File.ReadAllText(ready) == "previousMoved",
            "separate holder Windows process reached a durable previousMoved stage");
    }

    public static async Task Run(string testRoot, string originalArchive,
        string replacementArchive, MultiHostBackupOwnershipSnapshot ownership)
    {
        string name = "Untamed-MultiHost-Restore-" + Guid.NewGuid().ToString("N");
        string target = Path.Combine(Path.GetTempPath(), name);
        string fixture = Path.Combine(testRoot, "two-real-processes-lease");
        Directory.CreateDirectory(fixture);
        string ownershipFile = Path.Combine(fixture, "synthetic-ownership.json");
        string readyFile = Path.Combine(fixture, "holder-ready.txt");
        File.WriteAllText(ownershipFile, JsonSerializer.Serialize(ownership));
        var original = MultiHostClusterBackupArchive.RestoreToIsolatedSandbox(
            originalArchive, target);
        string journalPath = target + ".replace-transaction-v1.json";
        string leasePath = journalPath + ".lease";
        Process? holder = null;
        Process? contender = null;
        try
        {
            holder = StartChild("hold", replacementArchive, target,
                ownershipFile, readyFile);
            await WaitForReady(holder, readyFile);
            Check(File.Exists(leasePath) && !Directory.Exists(target),
                "holder exclusively owns the OS lease while the old TEMP target is safely moved aside");

            contender = StartChild("contend", replacementArchive, target,
                ownershipFile, readyFile);
            await WaitForExit(contender, "competing writer");
            string stdout = await contender.StandardOutput.ReadToEndAsync();
            string stderr = await contender.StandardError.ReadToEndAsync();
            Check(contender.ExitCode == 0 && stdout.Contains("LEASE_BUSY",
                    StringComparison.Ordinal) && string.IsNullOrWhiteSpace(stderr),
                "different Windows process cannot acquire the same TEMP writer lease or publish a second replacement");
            var whileBusy = MultiHostSandboxRestoreTransaction.InspectRecovery(target);
            Check(whileBusy.Phase == "busy" && whileBusy.NeedsOperator &&
                  !whileBusy.CanRollback,
                "journal recovery refuses mutation while a separate process owns the lease");

            holder.Kill(entireProcessTree: true);
            await WaitForExit(holder, "crashed writer");
            var clock = Stopwatch.StartNew();
            while (File.Exists(leasePath) && clock.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(40);
            Check(!File.Exists(leasePath),
                "unexpected OS process termination closes the lease handle without stale writer ownership");

            var recovery = MultiHostSandboxRestoreTransaction.InspectRecovery(target);
            Check(recovery.Phase == "previousMoved" && recovery.NeedsOperator &&
                  recovery.CanRollback && recovery.TransactionId.HasValue,
                "fresh process reopens checksum-verified durable journal and offers explicit rollback");
            var rolledBack = MultiHostSandboxRestoreTransaction.Rollback(
                target, recovery.TransactionId!.Value, explicitOperatorApproval: true);
            var restored = MultiHostClusterBackupArchive.VerifyIsolatedSandbox(target);
            Check(rolledBack.Phase == "rolledBack" &&
                  restored.SourceBatchId == original.SourceBatchId &&
                  !File.Exists(leasePath),
                "after crash, explicit journal rollback restores the original full SHA-256 verified TEMP batch");

            // The interrupted replacement candidate remains quarantined for
            // operator inspection. Remove it only from this synthetic CI lab.
            using var envelope = JsonDocument.Parse(File.ReadAllText(journalPath));
            using var payload = JsonDocument.Parse(
                envelope.RootElement.GetProperty("Payload").GetString()!);
            string candidateName = payload.RootElement.GetProperty("CandidateName").GetString()!;
            string candidate = Path.Combine(Path.GetTempPath(), candidateName);
            Check(Path.GetDirectoryName(candidate) == Path.GetTempPath().TrimEnd(
                    Path.DirectorySeparatorChar) &&
                  candidateName.StartsWith("Untamed-MultiHost-Restore-",
                    StringComparison.Ordinal) &&
                  Guid.TryParseExact(candidateName["Untamed-MultiHost-Restore-".Length..],
                    "N", out _) &&
                  MultiHostClusterBackupArchive.VerifyIsolatedSandbox(candidate)
                      .SourceBatchId != original.SourceBatchId,
                "interrupted replacement candidate is retained as a separate verified TEMP artifact");
            Directory.Delete(candidate, true);
        }
        finally
        {
            if (contender is { HasExited: false })
            {
                contender.Kill(entireProcessTree: true);
                await WaitForExit(contender, "competing writer cleanup");
            }
            if (holder is { HasExited: false })
            {
                holder.Kill(entireProcessTree: true);
                await WaitForExit(holder, "holder cleanup");
            }
            contender?.Dispose();
            holder?.Dispose();
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            if (File.Exists(readyFile)) File.Delete(readyFile);
        }
    }
}
