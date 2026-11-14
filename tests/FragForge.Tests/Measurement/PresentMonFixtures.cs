namespace FragForge.Tests.Measurement;

/// <summary>
/// Fixtures de capturas REALES de PresentMon 2.6.0 en la PC objetivo, copiadas verbatim.
///
/// IMPORTANTE sobre lo que estas capturas NO dicen:
/// <list type="bullet">
/// <item>La captura con las dos swapchains NO es un baseline válido: mezcla dos flujos de presentación
/// distintos y sus FPS no representan nada. Se usa solo para probar el parser y la elección de swapchain.</item>
/// <item>No se afirma que el juego corra a 60 FPS ni que la pantalla esté a 60 Hz.</item>
/// <item>La relación entre la swapchain <c>0x167400D5A60</c> (DXGI, ~104 ms, sin display) y la
/// <c>0x0</c> (Other, ~16.5 ms, con display) NO está determinada por estos datos.</item>
/// </list>
/// </summary>
public static class PresentMonFixtures
{
    /// <summary>Encabezado real de PresentMon 2.6.0 con flags por defecto, en su orden exacto (28 columnas).</summary>
    public const string Header260 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode," +
        "TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency," +
        "MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait," +
        "MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    // --- Swapchain 0x167400D5A60: DXGI, PresentMode=Other, ~104 ms, sin display, GPU en 0 ---

    public const string DxgiRow1 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,283.2085,NA,115.63470000000001,NA,1.62490000000000,0.00000000000000,NA,169.4300,115.4034,113.7785,1.6249,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    public const string DxgiRow2 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,400.9229,NA,117.71440000000001,NA,1.87450000000000,0.00000000000000,NA,284.8334,117.9640,116.0895,1.8745,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    public const string DxgiRow3 =
        "cs2.exe,9556,0x167400D5A60,DXGI,0,0,0,Other,520.6847,NA,119.76180000000001,NA,1.66020000000000,0.00000000000000,NA,402.7974,119.5475,117.8873,1.6602,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA";

    // --- Swapchain 0x0: PresentRuntime=Other, PresentMode="Hardware: Legacy Flip", ~16.5 ms, con display y GPU ---

    public const string ZeroRow1 =
        "cs2.exe,9556,0x0,Other,0,0,1,Hardware: Legacy Flip,7449.5256,NA,16.34370000000000,16.98530000000000,0.00000000000000,7.52770000000000,16.8431,7433.1819,16.3437,16.3437,0.0000,0.9580,22.9134,22.3928,0.5206,-0.4163,7433.1819,NA,NA,NA";

    public const string ZeroRow2 =
        "cs2.exe,9556,0x0,Other,0,0,1,Hardware: Legacy Flip,7466.4427,NA,16.91710000000000,16.70330000000000,0.00000000000000,1.28640000000000,16.6293,7449.5256,16.9171,16.9171,0.0000,8.0360,10.1675,9.5371,0.6304,-0.3596,7449.5256,NA,NA,NA";

    public const string ZeroRow3 =
        "cs2.exe,9556,0x0,Other,0,0,1,Hardware: Legacy Flip,7483.1724,NA,16.72970000000000,16.47130000000000,0.00000000000000,1.27330000000000,16.3709,7466.4427,16.7297,16.7297,0.0000,1.2864,16.7166,16.0705,0.6461,0.4458,7466.4427,NA,NA,NA";

    /// <summary>Solo la swapchain DXGI (~104 ms).</summary>
    public static string OnlyDxgi => Header260 + "\n" + DxgiRow1 + "\n" + DxgiRow2 + "\n" + DxgiRow3 + "\n";

    /// <summary>Solo la swapchain 0x0 (~16.5 ms).</summary>
    public static string OnlyZero => Header260 + "\n" + ZeroRow1 + "\n" + ZeroRow2 + "\n" + ZeroRow3 + "\n";

    /// <summary>
    /// La captura real completa: las DOS swapchains del MISMO ProcessID intercaladas, como llegaron.
    /// NO es un baseline válido.
    /// </summary>
    public static string TwoSwapChains => Header260 + "\n" + ZeroRow1 + "\n" + DxgiRow1 + "\n" + ZeroRow2 + "\n" + DxgiRow2 + "\n" + ZeroRow3 + "\n" + DxgiRow3 + "\n";

    /// <summary>Con CRLF, para probar ambos finales de línea.</summary>
    public static string TwoSwapChainsCrlf => TwoSwapChains.Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>Encabezado de <c>--v1_metrics</c> (observado sobre dwm.exe). Frame time no estándar.</summary>
    public const string V1MetricsHeader =
        "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI," +
        "msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange," +
        "msFlipDelay,msUntilRenderStart,msGPUActive,msSinceInput";

    /// <summary>Encabezado de <c>--v2_metrics</c> (observado sobre dwm.exe). Frame time no estándar.</summary>
    public const string V2MetricsHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode," +
        "CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime," +
        "AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency";
}
