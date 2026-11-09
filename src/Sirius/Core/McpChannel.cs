using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace Sirius.Core;

// O canal entre a janela da Sirius e a ponte que o Claude Code abre (Sirius.exe --mcp). Cada janela ouve num
// pipe com o nome da pasta do projeto, e só a mesma pessoa, na mesma sessão do Windows, consegue usar.
public static class McpChannel
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    public static string PipeName(string folder)
    {
        var key = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var user = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : null;
        return $"Sirius-mcp-{user ?? Environment.UserName}-{process.SessionId}-{hash}";
    }

    public static NamedPipeServerStream CreateServer(string folder) =>
        new(PipeName(folder), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    // A Sirius com esta pasta aberta ou, se não houver, com uma das pastas acima dela: o Claude pode ter sido
    // aberto numa subpasta do projeto. Null quando nenhuma responde.
    public static async Task<string?> AskAsync(string folder, string tool, JsonObject? arguments)
    {
        for (var current = Path.GetFullPath(folder); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            using var client = new NamedPipeClientStream(".", PipeName(current), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var connect = new CancellationTokenSource(ConnectTimeout);
                await client.ConnectAsync(connect.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using var answer = new CancellationTokenSource(AnswerTimeout);
            var request = new JsonObject { ["tool"] = tool, ["args"] = arguments?.DeepClone() };
            await WriteLineAsync(client, request.ToJsonString(), answer.Token);
            return await ReadLineAsync(client, answer.Token);
        }
        return null;
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancel)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel);
        await stream.FlushAsync(cancel);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancel);
            if (read == 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (end >= 0)
            {
                bytes.AddRange(buffer.Take(end));
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.AddRange(buffer.Take(read));
            if (bytes.Count > 4 * 1024 * 1024)
                return null;
        }
    }
}
