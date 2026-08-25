using System.Buffers.Binary;
using System.Net.Sockets;

namespace Campus.Sync;

/// <summary>An iPhone or iPad currently plugged into this machine.</summary>
/// <param name="DeviceId">usbmux's own handle for it, valid until it is unplugged.</param>
/// <param name="SerialNumber">The device's UDID, stable for ever.</param>
/// <param name="OverCable">False for a device usbmux can see over Wi-Fi rather than the wire.</param>
public sealed record UsbDevice(int DeviceId, string SerialNumber, bool OverCable)
{
    /// <summary>A short form of the UDID, for showing next to a device name.</summary>
    public string ShortSerial =>
        SerialNumber.Length <= 8 ? SerialNumber : SerialNumber[^8..];
}

/// <summary>
/// A tunnel to a TCP port on an iPhone, over the cable.
///
/// Windows already runs the machinery for this: plugging an iPhone in starts Apple Mobile Device
/// Service, which is a build of usbmuxd, and it listens on 127.0.0.1:27015. Its whole purpose is
/// to let a program on this machine open a TCP connection to a port on the device as though the
/// two were on a network together. iTunes syncs through it and Xcode debugs through it.
///
/// Campus uses it for exactly one thing: getting a <see cref="Stream"/> to Campus Pocket. What
/// happens over that stream is the same conversation the two have over Wi-Fi, encrypted with the
/// same pairing secret — the cable is a wire, not a permission, and being plugged in proves
/// nothing that being on the same network did not already fail to prove.
///
/// The protocol is a sixteen-byte header and an XML property list. It is not documented by Apple
/// and is not going to be, but it has been stable since 2009 and every tool that talks to an iOS
/// device over USB speaks it.
/// </summary>
public static class UsbMux
{
    /// <summary>Where Apple Mobile Device Service listens on Windows.</summary>
    public const int ServicePort = 27_015;

    private const int PlistVersion = 1;
    private const int PlistMessage = 8;
    private const int MaxReplyBytes = 4 * 1024 * 1024;

    private static int _tag;

    /// <summary>
    /// Whether the service is running at all.
    ///
    /// Its absence is the single most likely reason this feature does nothing on a given machine,
    /// and it has a specific fix — install Apple Devices, or iTunes — so it is worth being able
    /// to say that rather than reporting a failure to connect.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", ServicePort, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Every device attached right now. Empty when none is, which is not an error.</summary>
    public static async Task<IReadOnlyList<UsbDevice>> ListAsync(CancellationToken ct = default)
    {
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync("127.0.0.1", ServicePort, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }

        await using var stream = client.GetStream();

        var reply = await ExchangeAsync(stream, new Dictionary<string, object>
        {
            ["MessageType"] = "ListDevices",
            ["ClientVersionString"] = "Campus",
            ["ProgName"] = "Campus",
            ["kLibUSBMuxVersion"] = 3,
        }, ct).ConfigureAwait(false);

        var devices = new List<UsbDevice>();

        foreach (var entry in PropertyList.Array(reply, "DeviceList") ?? [])
        {
            var id = PropertyList.Integer(entry, "DeviceID");
            if (id is null) continue;

            var properties = PropertyList.AsDictionary(entry)?.GetValueOrDefault("Properties");
            var serial = PropertyList.String(properties, "SerialNumber") ?? string.Empty;

            // usbmux also reports devices it can see over Wi-Fi, which are not what "plug the
            // cable in" means and are slower and less reliable to reach.
            var connection = PropertyList.String(properties, "ConnectionType") ?? "USB";

            devices.Add(new UsbDevice((int)id.Value, serial,
                connection.Equals("USB", StringComparison.OrdinalIgnoreCase)));
        }

        return devices;
    }

    /// <summary>
    /// Opens a tunnel to a TCP port on the device. The returned stream is that port.
    /// </summary>
    /// <remarks>
    /// The caller owns the stream and closing it closes the tunnel. Nothing on the device is
    /// woken by this: something has to already be listening on that port, which for Campus means
    /// Campus Pocket is open.
    /// </remarks>
    public static async Task<Stream> ConnectAsync(
        int deviceId, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();

        try
        {
            await client.ConnectAsync("127.0.0.1", ServicePort, ct).ConfigureAwait(false);

            var stream = client.GetStream();

            // The port travels in network byte order inside an integer, which is a quirk of the
            // protocol rather than of the plist: usbmux passes it straight to connect().
            var swapped = BinaryPrimitives.ReverseEndianness((ushort)port);

            var reply = await ExchangeAsync(stream, new Dictionary<string, object>
            {
                ["MessageType"] = "Connect",
                ["DeviceID"] = deviceId,
                ["PortNumber"] = (int)swapped,
                ["ClientVersionString"] = "Campus",
                ["ProgName"] = "Campus",
                ["kLibUSBMuxVersion"] = 3,
            }, ct).ConfigureAwait(false);

            var result = PropertyList.Integer(reply, "Number") ?? -1;

            if (result != 0)
            {
                client.Dispose();
                throw new IOException(Explain(result, port));
            }

            // From here the socket is the device's port. usbmux says nothing else on it.
            return new UsbTunnel(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>usbmux's result codes, in words that say what to do about them.</summary>
    private static string Explain(long result, int port) => result switch
    {
        2 => "The phone is no longer plugged in.",
        3 => $"Nothing on the phone is listening on port {port}. Open Campus Pocket and try again.",
        5 => "Windows has not been trusted by this phone yet. Unlock it and tap Trust.",
        _ => $"The phone refused the connection (code {result}).",
    };

    // ------------------------------------------------------------------ framing

    private static async Task<object?> ExchangeAsync(
        NetworkStream stream, Dictionary<string, object> request, CancellationToken ct)
    {
        var body = PropertyList.Write(request);
        var tag = Interlocked.Increment(ref _tag);

        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0), body.Length + 16);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), PlistVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), tag);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var replyHeader = new byte[16];
        await stream.ReadExactlyAsync(replyHeader, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(replyHeader) - 16;

        if (length < 0 || length > MaxReplyBytes)
            throw new InvalidDataException($"usbmux replied with {length} bytes.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        return PropertyList.Read(payload);
    }
}

/// <summary>
/// The tunnel, as a stream that owns the socket underneath it.
///
/// A thin wrapper rather than handing back the NetworkStream directly, because disposing the
/// stream has to dispose the TcpClient too — otherwise the connection to usbmux stays open and
/// the device's port stays busy after the sync has finished.
/// </summary>
internal sealed class UsbTunnel(TcpClient client, NetworkStream stream) : Stream
{
    public override bool CanRead => stream.CanRead;
    public override bool CanWrite => stream.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => stream.Flush();
    public override Task FlushAsync(CancellationToken ct) => stream.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count)
        => stream.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => stream.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
        => stream.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => stream.WriteAsync(buffer, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream.Dispose();
            client.Dispose();
        }

        base.Dispose(disposing);
    }
}
