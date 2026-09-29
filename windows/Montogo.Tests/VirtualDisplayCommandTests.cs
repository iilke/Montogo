using Montogo.Driver;

namespace Montogo.Tests;

// Locks the exact JSON wire format the elevated helper sends to the virtual-display-rs
// driver's named pipe. The driver (v0.3.1) parses these with serde_json into DriverCommand,
// so the property names and structure must match the Rust structs exactly:
//   Monitor { id: u32, modes: Vec<MonitorMode> }
//   MonitorMode { width: u32, height: u32, refresh_rate: u32 }
//   enum DriverCommand { Add(Vec<Monitor>), Remove(Vec<u32>), RemoveAll }  (externally tagged)
public class VirtualDisplayCommandTests
{
    [Fact]
    public void BuildAddCommand_MatchesDriverWireFormat()
    {
        var json = VirtualDisplayProtocol.BuildAddCommand(1920, 1080, 60);
        Assert.Equal(
            "{\"Add\":[{\"id\":0,\"modes\":[{\"width\":1920,\"height\":1080,\"refresh_rate\":60}]}]}",
            json);
    }

    [Fact]
    public void RemoveAllCommand_IsBareJsonString()
    {
        // serde serializes the unit variant RemoveAll as a quoted JSON string, not an object.
        Assert.Equal("\"RemoveAll\"", VirtualDisplayProtocol.RemoveAllCommand);
    }
}
