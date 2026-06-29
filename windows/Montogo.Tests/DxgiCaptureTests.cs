using System.Runtime.Versioning;
using Montogo.Capture;

namespace Montogo.Tests;

[SupportedOSPlatform("windows8.0")]
public class DxgiCaptureTests
{
    [Fact]
    public void EnumerateDisplays_ReturnsAtLeastOneDisplay()
    {
        var displays = DxgiCapture.EnumerateDisplays();
        Assert.NotEmpty(displays);
    }

    [Fact]
    public void EnumerateDisplays_FirstDisplay_HasPositiveDimensions()
    {
        var first = DxgiCapture.EnumerateDisplays()[0];
        Assert.True(first.Width > 0);
        Assert.True(first.Height > 0);
    }

    [Fact]
    public void EnumerateDisplays_FirstDisplay_HasNonEmptyDeviceName()
    {
        var first = DxgiCapture.EnumerateDisplays()[0];
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceName));
    }

    [Fact]
    public void EnumerateDisplays_IndexesAreSequential()
    {
        var displays = DxgiCapture.EnumerateDisplays();
        for (int i = 0; i < displays.Count; i++)
            Assert.Equal(i, displays[i].Index);
    }
}
