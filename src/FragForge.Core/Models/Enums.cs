namespace FragForge.Core.Models;

public enum EvidenceLevel { High, Medium, Low, Experimental }
public enum RiskLevel { Safe, Moderate, Advanced, Experimental }
public enum TweakState { NotApplied, Applied, PartiallyApplied, NotApplicable, Unknown, AppliedExternally, Inspected }
public enum TweakProfile { Safe, Balanced, Competitive, Advanced }
public enum VerificationOutcome { Matches, DoesNotMatch, Partial, Unknown }
public enum FeatureState { Unknown, Enabled, Disabled, NotPresent }
public enum MemoryChannelState { Unknown, Single, Dual, Other }
public enum GpuVendor { Unknown, Intel, Nvidia, Amd, Microsoft, Other }
public enum DiskKind { Unknown, Hdd, Ssd, Nvme, Scm }
public enum NetworkKind { Unknown, Ethernet, WiFi, Other }
public enum PreflightSeverity { Info, Warning, Blocking }
public enum ExperimentPhase { BeforeApply, AfterApply, AfterRevert }
public enum JournalEventType { OperationStarted, SnapshotCreated, RestorePointCreated, TweakCaptureCompleted, TweakApplyStarted, TweakApplied, TweakSkipped, TweakRollbackStarted, TweakRolledBack, TweakRollbackFailed, OperationCompleted, OperationFailed, RevertStarted, RevertCompleted, RevertFailed, SnapshotReverted, PolicyRejected, HookCompleted }
