using UntamedDesktop;
internal sealed class SyntheticRecoveryExitWitness : DesktopWatchdogRecovery.IExitWitness
{
    public bool Exited;
    public int Code;
    public bool HasExited=>Exited;
    public int ExitCode=>Code;
    public void Dispose(){}
}
