using System.Net;
using System.Net.Sockets;
using Campus.Domain;
using Campus.Storage;

namespace Campus.Sync;

/// <summary>What arrived from the phone.</summary>
/// <summary>
/// The phone connected and was turned away, with the reason it was told.
///
/// Thrown rather than returned as an empty result, because "the phone had nothing waiting" and
/// "that phone is not paired with this workspace" are opposite situations and the caller was
/// reporting both as the first one.
/// </summary>
public sealed class PhoneSyncRefusedException(string reason) : Exception(reason);

public sealed record PhoneSyncResult(
    string DeviceName,
    int Accepted,
    int Rejected,
    int Attachments);

/// <summary>
/// Takes what Campus Pocket caught.
///
/// It listens for exactly one phone, for as long as somebody is waiting, and then stops. There is
/// no background listener and no discovery: the phone is told where to connect by the person
/// holding both devices, and a device that never scanned the pairing code cannot get past the
/// greeting.
/// </summary>
public sealed class PhoneReceiver(
    CampusDatabase database,
    Campus.Vault.CampusVault vault,
    string deviceId,
    string workspaceName)
{
    private readonly DeviceRepository _devices = new(database);
    private readonly ObjectRepository _objects = new(database, deviceId);
    private readonly Campus.Vault.CampusVault _vault = vault;

    public event EventHandler<string>? Progress;

    /// <summary>
    /// Whether an unpaired phone asking for a secret should be given one.
    ///
    /// Off unless the person at this machine has just pressed the button for it, and only ever
    /// set for a conversation over the cable. A phone on the network asking to be paired is a
    /// phone on the network claiming to be yours.
    /// </summary>
    public bool OfferPairing { get; set; }

    /// <summary>Raised when a phone has just been paired, so the caller can say whose it is.</summary>
    public event EventHandler<string>? Paired;

    /// <summary>
    /// Waits for one phone to connect and takes what it has. Returns null if nothing connected
    /// before the wait was cancelled.
    /// </summary>
    public async Task<PhoneSyncResult?> ReceiveAsync(
        int port = PhoneSync.Port, CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        try
        {
            Progress?.Invoke(this, $"Waiting on port {port}");

            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            // A phone that connects and then says nothing must not hold the listener for ever.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));

            return await ConverseAsync(stream, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Takes what a phone has, over a connection that already exists.
    ///
    /// Used for the cable, where the tunnel Windows opens through Apple's device service is a
    /// stream rather than something accepted from a listener. The conversation over it is
    /// identical — the phone still greets first and still has to prove it holds the pairing
    /// secret, because which end dialled says nothing about who is on it.
    /// </summary>
    public async Task<PhoneSyncResult?> ReceiveOverAsync(Stream stream, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Much shorter than the wait for a phone to appear on Wi-Fi. The connection already
        // exists here: if the app on the other end is not going to greet us, it is not going to,
        // and ten minutes of a disabled button is indistinguishable from the feature not working.
        deadline.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            return await ConverseAsync(stream, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The phone did not answer. Open Campus Pocket and leave it on screen.");
        }
    }

    private async Task<PhoneSyncResult?> ConverseAsync(Stream stream, CancellationToken ct)
    {
        Progress?.Invoke(this, "Connected. Waiting for the phone to say hello…");

        var hello = await PhoneSync.ReadJsonAsync<PhoneSync.Hello>(stream, ct).ConfigureAwait(false);

        if (hello is null || hello.Version != PhoneSync.Version)
        {
            throw await Refuse(stream, "This version of Campus does not understand that phone.", ct)
                .ConfigureAwait(false);
        }

        var device = await _devices.GetAsync(hello.DeviceId, ct).ConfigureAwait(false);

        // Pairing by QR mints the secret before the phone has said who it is, so the record it
        // leaves behind is keyed by an id invented here. The phone then greets with its own, and
        // this is where the two are reconciled — by the signature, which only the phone holding
        // that secret can produce. Without this the first connection after a QR pairing is
        // refused as "not paired", for ever, which is exactly what it did.
        device ??= await AdoptAsync(hello, ct).ConfigureAwait(false);

        var secret = device?.SharedKey is { Length: > 0 } stored ? stored : null;

        // A phone with no secret that is asking for one. Four things have to be true before it
        // gets one, and three of them are outside this program: it is on a cable, iOS let us
        // reach it because it has been told to trust this machine, the person here pressed the
        // button, and the phone says it has nothing already.
        var pairing = secret is null && hello.WantsPairing && OfferPairing;

        if (pairing)
        {
            secret = await PairOverCableAsync(hello, ct).ConfigureAwait(false);
        }
        else if (secret is null)
        {
            throw await Refuse(stream, hello.WantsPairing
                ? "This phone is asking to be paired. Press \u201CPair over the cable\u201D on "
                  + "this page to allow it."
                : "That phone is not paired with this workspace. Plug it in and press "
                  + "\u201CPair over the cable\u201D on this page.", ct).ConfigureAwait(false);
        }
        else if (device is { Trusted: false })
        {
            throw await Refuse(stream, "That phone is no longer trusted by this workspace.", ct)
                .ConfigureAwait(false);
        }

        // Skipped for a phone being paired right now: it could not have signed anything, because
        // not holding the secret is the whole of what it came to fix.
        if (!pairing && !PhoneSync.Verify(hello, secret))
        {
            // Being on the same network is not proof of anything, and this is where that is
            // enforced rather than assumed.
            throw await Refuse(stream,
                "That phone could not prove it holds the pairing secret. Pair it again.", ct)
                .ConfigureAwait(false);
        }

        Progress?.Invoke(this, pairing
            ? $"Paired with {hello.DeviceName}. Reading what it has…"
            : $"{hello.DeviceName} is paired. Reading what it has…");

        await PhoneSync.WriteJsonAsync(stream, new PhoneSync.HelloAck
        {
            Accepted = true,
            WorkspaceName = workspaceName,
            PairingCode = pairing
                ? PhoneSync.BuildPairingCode(hello.DeviceId, workspaceName,
                    Convert.FromBase64String(secret!))
                : null,
        }, ct).ConfigureAwait(false);

        if (pairing) Paired?.Invoke(this, hello.DeviceName);

        using var key = PhoneSync.TransferKey(secret);

        var push = await PhoneSync
            .ReadSealedAsync<PhoneSync.Push>(stream, key, hello.DeviceId, ct)
            .ConfigureAwait(false);

        if (push is null) return null;

        Progress?.Invoke(this, $"{push.Items.Count} from {hello.DeviceName}");

        var accepted = new List<string>();
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        var attachments = 0;

        var subjects = await LoadSubjectsAsync(ct).ConfigureAwait(false);

        foreach (var capture in push.Items)
        {
            ct.ThrowIfCancellationRequested();

            // Attachments arrive in the order their captures were listed, so the stream has to be
            // read for every capture that claims one — even one that is then rejected, or the
            // next capture would read someone else's bytes.
            byte[]? bytes = null;
            if (capture.Attachment is { Length: > 0 })
            {
                try
                {
                    bytes = await PhoneSync
                        .ReadSealedBytesAsync(stream, key, hello.DeviceId, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException
                                              or System.Security.Cryptography.CryptographicException)
                {
                    rejected[capture.Id] = "The attachment could not be read.";
                    break;
                }
            }

            try
            {
                await StoreAsync(capture, bytes, subjects, hello.DeviceId, ct).ConfigureAwait(false);

                accepted.Add(capture.Id);
                if (bytes is not null) attachments++;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                rejected[capture.Id] = ex.Message;
            }
        }

        await PhoneSync.WriteSealedAsync(stream, new PhoneSync.PushAck
        {
            AcceptedIds = accepted,
            Rejected = rejected,
        }, key, hello.DeviceId, ct).ConfigureAwait(false);

        device.LastSeenAt = DateTimeOffset.UtcNow;
        await _devices.SaveAsync(device, ct).ConfigureAwait(false);

        return new PhoneSyncResult(hello.DeviceName, accepted.Count, rejected.Count, attachments);
    }

    /// <summary>
    /// Finds the record a QR pairing left behind and re-keys it to the id the phone announced.
    ///
    /// The signature is the proof and the only one available: a device that can sign this nonce
    /// with that secret is the device the secret was minted for. Every paired phone is tried,
    /// which is a handful of HMACs — and a phone that matches none of them is simply not paired.
    /// </summary>
    private async Task<PairedDevice?> AdoptAsync(PhoneSync.Hello hello, CancellationToken ct)
    {
        if (hello.Signature is not { Length: > 0 }) return null;

        var known = await _devices.AllAsync(ct).ConfigureAwait(false);

        foreach (var candidate in known)
        {
            if (candidate.SharedKey is not { Length: > 0 } secret) continue;
            if (!PhoneSync.Verify(hello, secret)) continue;

            await _devices.ForgetAsync(candidate.DeviceId, ct).ConfigureAwait(false);

            var adopted = new PairedDevice
            {
                DeviceId = hello.DeviceId,
                DisplayName = hello.DeviceName is { Length: > 0 } name ? name : candidate.DisplayName,
                Platform = candidate.Platform,
                PairedAt = candidate.PairedAt,
                SharedKey = secret,
                Trusted = candidate.Trusted,
            };

            await _devices.SaveAsync(adopted, ct).ConfigureAwait(false);

            Progress?.Invoke(this, $"Recognised {adopted.DisplayName}.");
            return adopted;
        }

        return null;
    }

    /// <summary>
    /// Mints a secret for a phone on the cable and records the pairing.
    ///
    /// The record is keyed by the id the phone announced, rather than by one invented here, which
    /// is the difference between this and pairing by QR: over the cable the phone speaks first,
    /// so its identity is known before the secret exists rather than after.
    /// </summary>
    private async Task<string> PairOverCableAsync(PhoneSync.Hello hello, CancellationToken ct)
    {
        var key = PhoneSync.NewSharedKey();
        var secret = Convert.ToBase64String(key);

        await _devices.SaveAsync(new PairedDevice
        {
            DeviceId = hello.DeviceId,
            DisplayName = hello.DeviceName is { Length: > 0 } name ? name : "iPhone",
            Platform = DevicePlatform.IOS,
            PairedAt = DateTimeOffset.UtcNow,
            SharedKey = secret,
        }, ct).ConfigureAwait(false);

        return secret;
    }

    /// <summary>
    /// Tells the phone why it is being turned away, then tells this side too.
    ///
    /// The phone is told first: it is waiting on the acknowledgement and would otherwise sit
    /// there until its own timeout rather than showing the reason to the person holding it.
    /// </summary>
    private static async Task<PhoneSyncRefusedException> Refuse(
        Stream stream, string reason, CancellationToken ct)
    {
        await PhoneSync.WriteJsonAsync(stream, new PhoneSync.HelloAck
        {
            Accepted = false,
            Reason = reason,
        }, ct).ConfigureAwait(false);

        // Handed back to be thrown by the caller rather than thrown here, so the compiler can see
        // that the path ends — an awaited call that always throws looks like one that returns.
        return new PhoneSyncRefusedException(reason);
    }

    /// <summary>
    /// Turns one capture into an object in the workspace.
    ///
    /// Everything from the phone lands as what it says it is, filed under the subject it named if
    /// that subject exists here. It is never guessed at: a capture whose subject does not exist
    /// arrives unfiled, which is visible and fixable, rather than filed somewhere plausible.
    /// </summary>
    private async Task StoreAsync(
        PhoneSync.Capture capture,
        byte[]? attachment,
        IReadOnlyDictionary<string, CampusId> subjects,
        string fromDeviceId,
        CancellationToken ct)
    {
        var kind = (ObjectKind)capture.Kind;

        var entity = new CampusObject
        {
            Id = CampusId.TryParse(capture.Id, out var id) ? id : CampusId.New(),
            Kind = kind == ObjectKind.Unknown ? ObjectKind.InboxItem : kind,
            Title = capture.Title.Trim() is { Length: > 0 } title ? title : "Captured on the phone",
            DueAt = capture.DueAt,
            CreatedAt = capture.CapturedAt,
            Source = CaptureSource.Phone,
            SourceDeviceId = fromDeviceId,
            Status = ObjectStatus.NotStarted,
        };

        if (capture.SubjectName is { Length: > 0 } subject
            && subjects.TryGetValue(subject, out var subjectId))
        {
            entity.SubjectId = subjectId;
        }

        if (attachment is not null)
        {
            var stored = await _vault.Objects.PutBytesAsync(attachment, ct).ConfigureAwait(false);
            var name = capture.Attachment ?? "attachment";

            entity.Kind = ObjectKind.File;
            entity.Payload = new FilePayload
            {
                ContentHash = stored.ContentHash,
                OriginalFileName = name,
                Extension = Path.GetExtension(name),
                MimeType = MimeFor(Path.GetExtension(name)),
                Media = MediaFor(Path.GetExtension(name)),
                SizeBytes = stored.SizeBytes,
                ImportedAt = DateTimeOffset.UtcNow,
            };
        }
        else
        {
            entity.Payload = PayloadFor(entity.Kind, capture);
        }

        await _objects.SaveAsync(entity, ct).ConfigureAwait(false);
    }

    private static IObjectPayload? PayloadFor(ObjectKind kind, PhoneSync.Capture capture)
        => kind switch
        {
            ObjectKind.Note => new NotePayload { Body = capture.Body ?? "" },
            ObjectKind.Task => new TaskPayload { Notes = capture.Body },
            ObjectKind.Assignment => new AssignmentPayload { Instructions = capture.Body },
            ObjectKind.Requirement => new RequirementPayload { Action = capture.Body },
            ObjectKind.Link => new LinkPayload { Url = capture.Body ?? "" },
            _ => new InboxPayload { RawText = capture.Body },
        };

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".heic" => "image/heic",
        ".m4a" => "audio/mp4",
        ".mp4" or ".mov" => "video/mp4",
        _ => "application/octet-stream",
    };

    private static MediaKind MediaFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => MediaKind.Pdf,
        ".png" or ".jpg" or ".jpeg" or ".heic" => MediaKind.Image,
        ".m4a" => MediaKind.Audio,
        ".mp4" or ".mov" => MediaKind.Video,
        _ => MediaKind.Unknown,
    };

    private async Task<Dictionary<string, CampusId>> LoadSubjectsAsync(CancellationToken ct)
    {
        var subjects = await _objects
            .QueryAsync(new CampusQuery { Kinds = { ObjectKind.Subject } }, ct)
            .ConfigureAwait(false);

        return subjects.ToDictionary(s => s.Title, s => s.Id, StringComparer.OrdinalIgnoreCase);
    }
}
