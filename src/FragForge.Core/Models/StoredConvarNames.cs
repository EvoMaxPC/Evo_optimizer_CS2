namespace FragForge.Core.Models;

public static class StoredConvarNames
{
    public static IReadOnlyList<string> Ordered { get; } = Array.AsReadOnly(new[]
    {
        "fps_max", "fps_max_ui", "cl_showfps", "m_rawinput", "m_customaccel", "m_mouse_accel1", "m_mouse_accel2",
        "sensitivity", "zoom_sensitivity_ratio", "cl_interp", "cl_interp_ratio", "cl_updaterate", "cl_cmdrate", "rate",
        "engine_low_latency_sleep_after_client_tick", "mat_vsync", "r_fullscreen_gamma", "cl_autohelp", "viewmodel_fov"
    });
}
