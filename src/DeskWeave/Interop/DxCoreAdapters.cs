using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskWeave.Core;

namespace DeskWeave.Interop;

/// <summary>Read-only DXCore discovery. No device creation, elevated access, or driver installation.</summary>
internal static class DxCoreAdapters
{
    internal sealed record Adapter(string Name, string Kind, AdapterKey Key, double? Dedicated, double? Shared);

    internal static IReadOnlyList<Adapter> Discover()
    {
        var result = new Dictionary<AdapterKey, Adapter>();
        try
        {
            var iid = typeof(IFactory).GUID;
            Marshal.ThrowExceptionForHR(DXCoreCreateAdapterFactory(in iid, out var factory));
            try
            {
                // Graphics is first so an ML-capable GPU is still a GPU.
                Add(factory, new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1"), "GPU", result);
                Add(factory, new Guid("d46140c4-add7-451b-9e56-06fe8c3b58ed"), "NPU", result);
                Add(factory, new Guid("b71b0d41-1088-422f-a27c-0250b7d3a988"), "NPU", result);
                Add(factory, new Guid("248e2800-a793-4724-abaa-23a6de1be090"), "Accelerator", result);
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning("Accelerator discovery unavailable: {0}", ex.Message);
        }

        return result.Values.ToArray();
    }

    private static void Add(IFactory factory, Guid attribute, string kind, Dictionary<AdapterKey, Adapter> result)
    {
        var iid = typeof(IAdapterList).GUID;
        var hr = factory.CreateAdapterList(1, in attribute, in iid, out var list);
        if (hr < 0)
        {
            Trace.TraceWarning("DXCore {0} discovery failed: 0x{1:X8}", kind, hr);
            return;
        }

        try
        {
            for (uint i = 0; i < list.GetAdapterCount(); i++)
            {
                var adapterIid = typeof(IAdapter).GUID;
                var adapterHr = list.GetAdapter(i, in adapterIid, out var adapter);
                if (adapterHr < 0)
                {
                    Trace.TraceWarning("DXCore adapter discovery failed: 0x{0:X8}", adapterHr);
                    continue;
                }
                try
                {
                    if (Property<byte>(adapter, 11) != 1)
                        continue;
                    var luid = Property<ulong>(adapter, 0);
                    if (luid is null)
                        continue;
                    var key = new AdapterKey((uint)(luid.Value >> 32), (uint)luid.Value);
                    if (result.ContainsKey(key))
                        continue;
                    // Generic ML is not itself proof of an NPU (it can include compute-only GPUs).
                    var npuAttribute = new Guid("d46140c4-add7-451b-9e56-06fe8c3b58ed");
                    var deviceKind = kind == "GPU" ? kind : adapter.IsAttributeSupported(in npuAttribute) ? "NPU" : "Accelerator";
                    var name = Description(adapter);
                    result.Add(key, new Adapter(name, deviceKind, key,
                        Property<ulong>(adapter, 7), Property<ulong>(adapter, 9)));
                }
                catch (COMException ex)
                {
                    Trace.TraceWarning("DXCore adapter properties unavailable: 0x{0:X8}", ex.HResult);
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    private static unsafe T? Property<T>(IAdapter adapter, uint property) where T : unmanaged
    {
        if (!adapter.IsPropertySupported(property)) return null;
        T value = default;
        var hr = adapter.GetProperty(property, (nuint)sizeof(T), (nint)(&value));
        if (hr >= 0) return value;
        Trace.TraceWarning("DXCore property {0} failed: 0x{1:X8}", property, hr);
        return null;
    }

    private static unsafe string Description(IAdapter adapter)
    {
        Marshal.ThrowExceptionForHR(adapter.GetPropertySize(2, out var size));
        if (size is 0 or > 65536) throw new COMException("Invalid DXCore description size.");
        var bytes = new byte[(int)size];
        fixed (byte* buffer = bytes)
            Marshal.ThrowExceptionForHR(adapter.GetProperty(2, size, (nint)buffer));
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    [DllImport("dxcore.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DXCoreCreateAdapterFactory(in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IFactory factory);

    [ComImport, Guid("78ee5945-c36e-4b13-a669-005dd11c0f06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFactory
    {
        [PreserveSig] int CreateAdapterList(uint count, in Guid attributes, in Guid iid, out IAdapterList list);
        [PreserveSig] int GetAdapterByLuid(in ulong luid, in Guid iid, out IAdapter adapter);
    }

    [ComImport, Guid("526c7776-40e9-459b-b711-f32ad76dfc28"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAdapterList
    {
        [PreserveSig] int GetAdapter(uint index, in Guid iid, out IAdapter adapter);
        [PreserveSig] uint GetAdapterCount();
    }

    [ComImport, Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAdapter
    {
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsValid();
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsAttributeSupported(in Guid attribute);
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsPropertySupported(uint property);
        [PreserveSig] int GetProperty(uint property, nuint size, nint data);
        [PreserveSig] int GetPropertySize(uint property, out nuint size);
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsQueryStateSupported(uint state);
        [PreserveSig] int QueryState(uint state, nuint inputSize, nint input, nuint outputSize, nint output);
    }
}
