using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Campus.Domain;
using Campus.Sync;

namespace Campus.Desktop.Services;

/// <summary>How a bundle gets from one device to the other.</summary>
public enum SyncTransport
{
    /// <summary>A file written to a stick, a folder, or anywhere else the user can reach.</summary>
    Folder = 0,

    /// <summary>A direct socket on the local network. Same bundle, no file left behind.</summary>
    LocalNetwork = 1,
}

/// <summary>
/// Sync, as the desktop uses it.
///
/// Two transports, one format. Over a folder the bundle is a file you can carry; over the local
/// network it is the same bytes on a socket, sent to an address the user typed. There is no
/// discovery, no broadcast and no server: nothing announces this machine to a network, because a
/// workspace that goes to the trouble of encrypting itself should not then advertise its
/// existence to the café Wi-Fi.
/// </summary>
public sealed class SyncService(WorkspaceService workspace)
{
    /// <summary>The port used when the user does not name one. Registered to nothing.</summary>
    public const int DefaultPort = 47_411;

    private readonly WorkspaceService _workspace = workspace;
    private TcpListener? _listener;
    private CancellationTokenSource? _listening;

    public event EventHandler<string>? Progress;

    public bool IsListening => _listener is not null;

    private SyncEngine Engine => new(
        _workspace.Database,
        _workspace.Vault,
        _workspace.DeviceId,
        Environment.MachineName);

    // ------------------------------------------------------------------------- folder

    /// <summary>Writes a bundle for a peer to a file.</summary>
    public async Task<BundleManifest> ExportAsync(
        PairedDevice peer, string pairingCode, string path, CancellationToken ct = default)
    {
        var engine = Engine;
        engine.Progress += (_, message) => Progress?.Invoke(this, message);
        return await engine.WriteForAsync(peer, pairingCode, path, ct);
    }

    /// <summary>Applies a bundle from a file.</summary>
    public async Task<SyncResult?> ImportAsync(
        string path, string pairingCode, CancellationToken ct = default)
    {
        var engine = Engine;
        engine.Progress += (_, message) => Progress?.Invoke(this, message);
        return await engine.ApplyAsync(path, pairingCode, ct);
    }

    public Task<BundleManifest?> InspectAsync(string path, CancellationToken ct = default)
        => SyncBundle.ReadManifestAsync(path, ct);

    // ------------------------------------------------------------------ local network

    /// <summary>
    /// Waits for one peer to connect, sends it a bundle, and stops.
    ///
    /// Deliberately one connection at a time and only while the user is looking at the sync
    /// page. A listener that runs in the background is a service; this is a handshake.
    /// </summary>
    public async Task<BundleManifest?> ServeAsync(
        PairedDevice peer, string pairingCode, int port = DefaultPort, CancellationToken ct = default)
    {
        var bundle = Path.Combine(Path.GetTempPath(), "Campus", $"sync-{CampusId.New().Value}.campussync");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);

        try
        {
            var manifest = await ExportAsync(peer, pairingCode, bundle, ct);

            _listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            Progress?.Invoke(this, $"Waiting on port {port}");

            using var client = await _listener.AcceptTcpClientAsync(_listening.Token);
            Progress?.Invoke(this, "Sending");

            await using var network = client.GetStream();
            await using var file = File.OpenRead(bundle);

            // Length first, so the receiver knows when the bundle ends without the socket
            // having to close to signal it.
            var header = BitConverter.GetBytes(file.Length);
            await network.WriteAsync(header, ct);
            await file.CopyToAsync(network, ct);
            await network.FlushAsync(ct);

            Progress?.Invoke(this, "Sent");
            return manifest;
        }
        finally
        {
            StopListening();

            // The bundle is plaintext-adjacent — encrypted, but with a key derived from a short
            // code — so it does not get to sit in the temporary folder afterwards.
            try { if (File.Exists(bundle)) File.Delete(bundle); }
            catch (IOException) { /* Windows will clear it */ }
        }
    }

    public void StopListening()
    {
        _listening?.Cancel();
        _listener?.Stop();
        _listener = null;
        _listening?.Dispose();
        _listening = null;
    }

    /// <summary>Connects to a peer that is serving, and applies what it sends.</summary>
    public async Task<SyncResult?> FetchAsync(
        string host, string pairingCode, int port = DefaultPort, CancellationToken ct = default)
    {
        var bundle = Path.Combine(Path.GetTempPath(), "Campus", $"sync-{CampusId.New().Value}.campussync");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);

        try
        {
            Progress?.Invoke(this, $"Connecting to {host}");

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            await using var network = client.GetStream();

            var header = new byte[sizeof(long)];
            await network.ReadExactlyAsync(header, ct);
            var length = BitConverter.ToInt64(header);

            if (length is <= 0 or > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The other device offered a nonsensical size.");

            Progress?.Invoke(this, "Receiving");

            await using (var file = File.Create(bundle))
            {
                var buffer = new byte[81_920];
                var remaining = length;

                while (remaining > 0)
                {
                    var read = await network.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);

                    if (read == 0) throw new IOException("The connection ended early.");

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }

            Progress?.Invoke(this, "Applying");
            return await ImportAsync(bundle, pairingCode, ct);
        }
        finally
        {
            try { if (File.Exists(bundle)) File.Delete(bundle); }
            catch (IOException) { /* Windows will clear it */ }
        }
    }

    /// <summary>
    /// The addresses a peer on the same network could reach this machine on. Shown so the user
    /// can read one out rather than having to go and find it.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Where(a => !a.StartsWith("169.254", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    // -------------------------------------------------------------------------- phone

    /// <summary>
    /// Starts pairing a phone: makes the secret, records the device, and returns the code to
    /// show. The code carries the secret because it is shown on a screen and read by a camera in
    /// the same room — a stronger channel than anything two devices could negotiate over a
    /// network neither of them trusts.
    /// </summary>
    public async Task<string> BeginPhonePairingAsync(
        string displayName, DevicePlatform platform = DevicePlatform.IOS,
        CancellationToken ct = default)
    {
        var key = PhoneSync.NewSharedKey();

        // The phone announces its own id when it first connects; until then the record is keyed
        // by the secret, which is what the greeting is checked against.
        var id = Domain.CampusId.New().Value;

        await Engine.PairAsync(new PairedDevice
        {
            DeviceId = id,
            DisplayName = displayName,
            Platform = platform,
            PairedAt = DateTimeOffset.UtcNow,
            SharedKey = Convert.ToBase64String(key),
        }, ct);

        return PhoneSync.BuildPairingCode(id, Environment.MachineName, key);
    }

    /// <summary>
    /// Waits for the paired phone to connect and takes what it caught. Stops when the caller
    /// stops waiting; nothing listens in the background.
    /// </summary>
    public async Task<PhoneSyncResult?> ReceiveFromPhoneAsync(CancellationToken ct = default)
    {
        var receiver = new PhoneReceiver(
            _workspace.Database, _workspace.Vault, _workspace.DeviceId, "Campus");

        receiver.Progress += (_, message) => Progress?.Invoke(this, message);

        return await receiver.ReceiveAsync(PhoneSync.Port, ct);
    }

    // ------------------------------------------------------------------------ over the cable

    /// <summary>Whether Windows has Apple's device service, which is what carries the cable.</summary>
    public Task<bool> CableSupportAsync(CancellationToken ct = default)
        => UsbMux.IsAvailableAsync(ct);

    /// <summary>Phones plugged into this machine right now.</summary>
    public async Task<IReadOnlyList<UsbDevice>> AttachedPhonesAsync(CancellationToken ct = default)
    {
        var devices = await UsbMux.ListAsync(ct);
        return devices.Where(d => d.OverCable).ToList();
    }

    /// <summary>
    /// Takes what a plugged-in phone has, without either device needing to be on a network.
    ///
    /// The tunnel is opened from this side because that is the only direction Apple's service
    /// carries; everything after that is the same conversation as over Wi-Fi, encrypted with the
    /// same pairing secret. Campus Pocket has to be open on the phone — iOS gives a foreground
    /// app a socket and takes it away again when the app is put down.
    /// </summary>
    public async Task<PhoneSyncResult?> ReceiveOverCableAsync(
        UsbDevice device, CancellationToken ct = default)
    {
        Progress?.Invoke(this, $"Reaching the phone over the cable…");

        await using var tunnel = await UsbMux.ConnectAsync(device.DeviceId, PhoneSync.Port, ct);

        var receiver = new PhoneReceiver(
            _workspace.Database, _workspace.Vault, _workspace.DeviceId, "Campus");

        receiver.Progress += (_, message) => Progress?.Invoke(this, message);

        return await receiver.ReceiveOverAsync(tunnel, ct);
    }

    // ------------------------------------------------------------------------ pairing

    /// <summary>
    /// Records a peer this device has agreed to sync with. Pairing carries no keys: both sides
    /// derive what they need from the code, and the code is never stored.
    /// </summary>
    public async Task PairAsync(
        string deviceId, string displayName, DevicePlatform platform, CancellationToken ct = default)
    {
        await Engine.PairAsync(new PairedDevice
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            Platform = platform,
            PairedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    public Task ForgetAsync(string deviceId, CancellationToken ct = default)
        => Engine.ForgetAsync(deviceId, ct);

    public Task<IReadOnlyList<PairedDevice>> DevicesAsync(CancellationToken ct = default)
        => Engine.DevicesAsync(ct);

    public Task<long> PositionAsync(CancellationToken ct = default)
        => Engine.PositionAsync(ct);

    public Task<int> PendingForAsync(PairedDevice peer, CancellationToken ct = default)
        => Engine.PendingForAsync(peer, ct);

    public Task<IReadOnlyList<SyncConflict>> ConflictsAsync(CancellationToken ct = default)
        => Engine.ConflictsAsync(ct);

    public Task ResolveAsync(
        SyncConflict conflict, ConflictResolution resolution, CancellationToken ct = default)
        => Engine.ResolveAsync(conflict, resolution, ct);

    /// <summary>
    /// The offer another device scans or types to know who it is pairing with. It carries no
    /// secret — only who is offering and the salt the key will be derived with.
    /// </summary>
    public string BuildOffer(byte[] salt)
        => Pairing.Offer(_workspace.DeviceId, Environment.MachineName, salt);
}
