using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace JadeAudioControl.Hid;

/// <summary>A safe handle for the HID file handle.</summary>
public sealed class SafeFileHandleWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFileHandleWrapper() : base(true) { }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>One HID interface as reported by the system.</summary>
public sealed class HidDeviceInfo
{
    public HidDeviceInfo(string path, ushort vendorId, ushort productId, string product,
                         string manufacturer, ushort usagePage, ushort usage,
                         int inputReportLength, int outputReportLength)
    {
        Path = path;
        VendorId = vendorId;
        ProductId = productId;
        Product = product;
        Manufacturer = manufacturer;
        UsagePage = usagePage;
        Usage = usage;
        InputReportLength = inputReportLength;
        OutputReportLength = outputReportLength;
    }

    public string Path { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string Product { get; }
    public string Manufacturer { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int InputReportLength { get; }
    public int OutputReportLength { get; }
}

/// <summary>
/// An opened HID device. Reads are overlapped so they can be cancelled, which
/// is what lets a request time out without wedging the worker.
/// </summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandleWrapper _handle;
    private readonly FileStream _stream;

    public HidDeviceInfo Info { get; }
    public int InputReportLength => Info.InputReportLength;
    public int OutputReportLength => Info.OutputReportLength;

    private HidDevice(SafeFileHandleWrapper handle, HidDeviceInfo info)
    {
        _handle = handle;
        Info = info;
        _stream = new FileStream(
            new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false),
            FileAccess.ReadWrite,
            bufferSize: Math.Max(info.InputReportLength, 1),
            isAsync: true);
    }

    public static HidDevice Open(HidDeviceInfo info)
    {
        var handle = NativeMethods.CreateFile(
            info.Path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Could not open {info.Product}. Is another app using it?");
        NativeMethods.HidD_SetNumInputBuffers(handle, 64);
        return new HidDevice(handle, info);
    }

    /// <summary>Every HID interface matching <paramref name="vendorId"/>.</summary>
    public static IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var found = new List<HidDeviceInfo>();
        NativeMethods.HidD_GetHidGuid(out var hidGuid);
        var set = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return found;

        try
        {
            var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0; ; index++)
            {
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    break;

                var path = GetDevicePath(set, ref interfaceData);
                if (path is null)
                    continue;

                var info = Describe(path);
                if (info is not null && info.VendorId == vendorId)
                    found.Add(info);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static string? GetDevicePath(IntPtr set, ref NativeMethods.SP_DEVICE_INTERFACE_DATA data)
    {
        int required = 0;
        NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required <= 0)
            return null;

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 4 bytes on x64 (padding), 6 on x86.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    set, ref data, buffer, required, ref required, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidDeviceInfo? Describe(string path)
    {
        using var handle = NativeMethods.CreateFile(
            path, 0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var attributes = new NativeMethods.HIDD_ATTRIBUTES
        {
            Size = Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>()
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes))
            return null;

        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
            return null;

        NativeMethods.HIDP_CAPS caps;
        try
        {
            if (NativeMethods.HidP_GetCaps(preparsed, out caps) != unchecked((int)0x00110000)) // HIDP_STATUS_SUCCESS
                return null;
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsed);
        }

        return new HidDeviceInfo(
            path,
            attributes.VendorID,
            attributes.ProductID,
            ReadString(handle, NativeMethods.HidD_GetProductString),
            ReadString(handle, NativeMethods.HidD_GetManufacturerString),
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength);
    }

    private delegate bool StringGetter(SafeFileHandleWrapper device, byte[] buffer, int length);

    private static string ReadString(SafeFileHandleWrapper handle, StringGetter getter)
    {
        var buffer = new byte[512];
        return getter(handle, buffer, buffer.Length)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : string.Empty;
    }

    /// <summary>Write one output report. The buffer must be the full report length.</summary>
    public async Task WriteAsync(byte[] report, CancellationToken token = default)
    {
        // No FlushAsync here: on .NET Framework it calls FlushFileBuffers, which
        // a HID device handle rejects with ERROR_INVALID_FUNCTION. The write
        // itself already reaches the device.
        await _stream.WriteAsync(report, 0, report.Length, token).ConfigureAwait(false);
    }

    /// <summary>Read one input report, or null if the wait expired.</summary>
    public async Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken token = default)
    {
        var buffer = new byte[InputReportLength];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            int read = await _stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
            if (read <= 0)
                return null;
            if (read == buffer.Length)
                return buffer;
            var trimmed = new byte[read];
            Array.Copy(buffer, trimmed, read);
            return trimmed;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}
