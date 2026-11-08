using System;
using System.IO;

string root = Path.Combine(Path.GetTempPath(), "Untamed-MultiHost-Backup-Test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    MultiHostClusterBackupArchiveTests.Run(root);
    Console.WriteLine("PASS all isolated multi-host backup tests");
}
finally
{
    // Test files remain under one uniquely named TEMP directory for forensic
    // inspection. The test never cleans up a caller-provided or production path.
}
