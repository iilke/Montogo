using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Montogo.Driver;

/// <summary>
/// The virtual-display-rs <b>v0.3.1</b> wire protocol and the low-level send to its named
/// pipe <c>\\.\pipe\virtualdisplaydriver</c>. That pipe is admin-only, so only the elevated
/// <c>Montogo.DriverHelper</c> calls <see cref="SendToDriver"/>.
///
/// Commands are one UTF-8 JSON message each; serde uses external tagging:
/// <code>
///   Add:        {"Add":[{"id":0,"modes":[{"width":1920,"height":1080,"refresh_rate":60}]}]}
///   RemoveAll:  "RemoveAll"
/// </code>
/// </summary>
public static class VirtualDisplayProtocol
{
    // \\.\pipe\virtualdisplaydriver — NamedPipeClientStream takes the name without the prefix.
    public const string DriverPipeName   = "virtualdisplaydriver";

    /// <summary>serde serializes the <c>RemoveAll</c> unit variant as a bare JSON string.</summary>
    public const string RemoveAllCommand = "\"RemoveAll\"";

    /// <summary>
    /// Builds the JSON for an <c>Add</c> command with a single monitor (id 0) that has one
    /// mode. Kept separate so the exact wire shape can be unit-tested without a pipe.
    /// </summary>
    public static string BuildAddCommand(int width, int height, int refreshRate)
    {
        // Anonymous object → default System.Text.Json keeps the exact property names, which
        // must match the Rust structs (id, modes, width, height, refresh_rate).
        var command = new
        {
            Add = new[]
            {
                new { id = 0u, modes = new[] { new { width, height, refresh_rate = refreshRate } } }
            }
        };
        return JsonSerializer.Serialize(command);
    }

    /// <summary>
    /// Sends one JSON command to the driver's control pipe. Requires elevation — the pipe's
    /// default security only admits SYSTEM/Administrators.
    /// </summary>
    public static void SendToDriver(string json, int connectTimeoutMs = 3000)
    {
        using var pipe = new NamedPipeClientStream(".", DriverPipeName, PipeDirection.Out, PipeOptions.None);
        pipe.Connect(connectTimeoutMs);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        pipe.Write(bytes, 0, bytes.Length);   // message-typed pipe: one write = one message
        pipe.Flush();
    }
}
