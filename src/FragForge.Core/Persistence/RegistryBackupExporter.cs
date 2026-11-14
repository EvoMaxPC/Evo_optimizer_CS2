using System.Globalization;
using System.Text;
using FragForge.Core.Models;

namespace FragForge.Core.Persistence;

public static class RegistryBackupExporter
{
    public static async Task WriteAsync(string path, IEnumerable<RegistryActionState> states, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n; FragForge scoped value backup. Absent values are represented by value deletion, never whole-key deletion.\r\n");
        foreach (var state in states)
        foreach (var value in state.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append('[').Append(RegistryPath.ToFullRoot(value.Key)).Append("]\r\n");
            var name = value.Name.Length == 0 ? "@" : "\"" + Escape(value.Name) + "\"";
            if (!value.ValueExisted) { builder.Append(name).Append("=-\r\n\r\n"); continue; }
            builder.Append(name).Append('=');
            switch (value.ValueKind)
            {
                case "String": builder.Append('"').Append(Escape(value.StringData ?? "")).Append('"'); break;
                case "ExpandString": builder.Append("hex(2):").Append(BytesHex(Encoding.Unicode.GetBytes((value.StringData ?? "") + "\0"))); break;
                case "DWord": builder.Append("dword:").Append(((uint)(value.IntegerData ?? 0)).ToString("x8", CultureInfo.InvariantCulture)); break;
                case "QWord": builder.Append("hex(b):").Append(BytesHex(BitConverter.GetBytes(unchecked((ulong)(value.IntegerData ?? 0))))); break;
                case "Binary": builder.Append("hex:").Append(BytesHex(Convert.FromBase64String(value.BinaryBase64 ?? ""))); break;
                case "MultiString":
                    var text = string.Join("\0", value.MultiStringData ?? []) + "\0\0";
                    builder.Append("hex(7):").Append(BytesHex(Encoding.Unicode.GetBytes(text))); break;
                default: throw new InvalidDataException($"Unsupported registry kind '{value.ValueKind}'.");
            }
            builder.Append("\r\n\r\n");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.Unicode, cancellationToken);
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string BytesHex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
