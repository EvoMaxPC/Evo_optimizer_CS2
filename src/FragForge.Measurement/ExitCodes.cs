namespace FragForge.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Preflight = 3;
    public const int Cancelled = 4;
    public const int RollbackCritical = 5;
    public const int MeasurementInvalid = 6;
}
