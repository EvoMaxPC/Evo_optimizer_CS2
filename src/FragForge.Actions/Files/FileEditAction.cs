using System.Text;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Files;

public sealed class FileEditAction(IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly FileEditParameters _parameters = ActionParameters.Parse<FileEditParameters>(specification);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public string Kind => "FileEdit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        policy.EnsureFileAllowed(ResolvePath(context));
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        policy.EnsureFileAllowed(path);
        if (!File.Exists(path))
        {
            var missing = new List<string>();
            var directory = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) { missing.Add(directory); directory = Path.GetDirectoryName(directory); }
            return new FileActionState { Path = path, Existed = false, MissingDirectories = missing };
        }
        var info = new FileInfo(path);
        if (info.Length > _parameters.MaxBytes) throw new FragForgeException("file_too_large", $"{path} supera el límite de {_parameters.MaxBytes} bytes.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new FileActionState { Path = path, Existed = true, ContentBase64 = Convert.ToBase64String(bytes), LastWriteTimeUtc = info.LastWriteTimeUtc };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        var original = state.ContentBase64 is null ? null : Convert.FromBase64String(state.ContentBase64);
        if (!BytesEqual(current, original)) throw new VerificationFailedException("El archivo cambió desde la captura; no se sobrescribe.");
        var transformed = Transform(original ?? []);
        await AtomicFile.WriteAllBytesAsync(state.Path, transformed, cancellationToken);
        context.Report("apply", 1, $"{state.Path}: { _parameters.Edits.Count } ediciones verificables.");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        var expected = Transform(state.ContentBase64 is null ? [] : Convert.FromBase64String(state.ContentBase64));
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        return BytesEqual(current, expected) ? VerificationResult.Match("Contenido UTF-8 verificado byte a byte.") : VerificationResult.Missing("El archivo no coincide con la transformación esperada.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        if (state.Existed)
        {
            await AtomicFile.WriteAllBytesAsync(state.Path, Convert.FromBase64String(state.ContentBase64 ?? ""), cancellationToken);
            if (state.LastWriteTimeUtc is not null) File.SetLastWriteTimeUtc(state.Path, state.LastWriteTimeUtc.Value.UtcDateTime);
        }
        else
        {
            if (File.Exists(state.Path)) File.Delete(state.Path);
            foreach (var directory in state.MissingDirectories) if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Archivo restaurado byte a byte.") : VerificationResult.Missing("El archivo no volvió al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "(archivo nuevo)";
        return $"{path}: {_parameters.Edits.Count} sustituciones regex con recuento esperado; UTF-8/BOM preservados; copia completa reversible. Estado actual: {current[..Math.Min(current.Length, 160)]}";
    }
    private byte[] Transform(byte[] original)
    {
        var hasBom = original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = StrictUtf8.GetString(hasBom ? original[3..] : original);
        foreach (var edit in _parameters.Edits)
        {
            var regex = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var count = regex.Matches(text).Count;
            if (count != edit.ExpectedMatches) throw new VerificationFailedException($"'{edit.Pattern}' encontró {count} coincidencias; esperaba {edit.ExpectedMatches}.");
            text = regex.Replace(text, edit.Replacement);
        }
        var bytes = StrictUtf8.GetBytes(text);
        return hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }
    private string ResolvePath(ActionExecutionContext context) => Path.GetFullPath(ActionParameters.ResolveToken(_parameters.Path, context));
    private void Validate()
    {
        if (_parameters.MaxBytes is <= 0 or > 1048576) throw new CatalogValidationException("FileEdit MaxBytes must be 1..1048576.", [_parameters.MaxBytes.ToString()]);
        if (_parameters.Edits.Count == 0) throw new CatalogValidationException("FileEdit requires edits.", ["edits"]);
        foreach (var edit in _parameters.Edits) { _ = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); if (edit.ExpectedMatches < 0) throw new CatalogValidationException("ExpectedMatches cannot be negative.", [edit.Pattern]); }
    }
    private static bool BytesEqual(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
internal sealed record FileEditParameters { public required string Path { get; init; } public int MaxBytes { get; init; } = 1048576; public IReadOnlyList<FileTextEdit> Edits { get; init; } = []; }
internal sealed record FileTextEdit { public required string Pattern { get; init; } public string Replacement { get; init; } = ""; public int ExpectedMatches { get; init; } = 1; }
