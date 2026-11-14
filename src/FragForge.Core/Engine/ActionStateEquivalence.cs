using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Core.Engine;

public static class ActionStateEquivalence
{
    public static bool Equivalent(ActionState expected, ActionState actual) => (expected, actual) switch
    {
        (RegistryActionState a, RegistryActionState b) => a.Values.Count == b.Values.Count && a.Values.Zip(b.Values).All(p => RegistryEqual(p.First, p.Second)),
        (ServiceActionState a, ServiceActionState b) => a.Service == b.Service,
        (ScheduledTaskActionState a, ScheduledTaskActionState b) => a.Path == b.Path && a.Exists == b.Exists && a.Enabled == b.Enabled,
        (PowerPlanActionState a, PowerPlanActionState b) => a.PlanName == b.PlanName && a.PreviousActiveGuid == b.PreviousActiveGuid && a.PlanExisted == b.PlanExisted && a.OwnedPlanGuid == b.OwnedPlanGuid && a.Settings.SequenceEqual(b.Settings),
        (BcdActionState a, BcdActionState b) => a.Element == b.Element && a.Existed == b.Existed && a.PreviousValue == b.PreviousValue,
        (NetworkPropertyActionState a, NetworkPropertyActionState b) => a.Properties.SequenceEqual(b.Properties),
        (FileActionState a, FileActionState b) => a.Path == b.Path && a.Existed == b.Existed && a.ContentBase64 == b.ContentBase64 && a.LastWriteTimeUtc == b.LastWriteTimeUtc,
        (ScriptActionState a, ScriptActionState b) => JsonSerializer.Serialize(a.CapturedJson, JsonDefaults.Create(false)) == JsonSerializer.Serialize(b.CapturedJson, JsonDefaults.Create(false)),
        (InspectionActionState, InspectionActionState) => true,
        _ => false
    };
    private static bool RegistryEqual(RegistryValueCapture a, RegistryValueCapture b) => a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Name.Equals(b.Name, StringComparison.Ordinal) && a.KeyExisted == b.KeyExisted && a.ValueExisted == b.ValueExisted && a.ValueKind == b.ValueKind && a.StringData == b.StringData && a.IntegerData == b.IntegerData && (a.MultiStringData ?? []).SequenceEqual(b.MultiStringData ?? []) && a.BinaryBase64 == b.BinaryBase64 && a.MissingAncestors.SequenceEqual(b.MissingAncestors, StringComparer.OrdinalIgnoreCase);
}
