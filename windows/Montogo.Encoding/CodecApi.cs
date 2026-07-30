using System.Runtime.InteropServices;

namespace Montogo.Encoding;

// Manual interop: CsWin32 0.3.298's metadata does not include ICodecAPI or the
// CODECAPI_* property GUIDs.  Only SetValue is used; the earlier vtable slots
// must still be declared in order.  Unused VARIANT* parameters are IntPtr to
// avoid pulling in VARIANT marshaling where it is not needed.
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, IntPtr valueMin, IntPtr valueMax, IntPtr steppingDelta);
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint valuesCount);
    [PreserveSig] int GetDefaultValue(in Guid api, IntPtr value);
    [PreserveSig] int GetValue(in Guid api, IntPtr value);
    [PreserveSig] int SetValue(in Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

internal static class CodecApiGuids
{
    /// <summary>CODECAPI_AVLowLatencyMode — VT_BOOL.</summary>
    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>CODECAPI_AVEncCommonQualityVsSpeed — VT_UI4, 0 = fastest … 100 = best quality.</summary>
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    // GUIDs taken verbatim from the Windows SDK codecapi.h (STATIC_CODECAPI_*).
    /// <summary>CODECAPI_AVEncCommonRateControlMode — VT_UI4; 0=CBR, 1=PeakVBR, 2=UnconstrainedVBR, 3=Quality.</summary>
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    /// <summary>CODECAPI_AVEncCommonMeanBitRate — VT_UI4, target bits/sec.</summary>
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    /// <summary>CODECAPI_AVEncCommonMaxBitRate — VT_UI4, peak bits/sec.</summary>
    public static readonly Guid AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");

    /// <summary>CODECAPI_AVEncCommonBufferSize — VT_UI4, VBV/HRD buffer in bits. Small = tight per-frame cap.</summary>
    public static readonly Guid AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame — VT_UI4, set to 1 to force the next frame to be a keyframe.</summary>
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
}
