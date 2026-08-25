using System.Net;
using System.Net.Sockets;
using Campus.Domain;
using Campus.Storage;

namespace Campus.Sync;

/// <summary>What arrived from the phone.</summary>
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
        deadline.CancelAfter(TimeSpan.FromMinutes(10));

        return await ConverseAsync(stream, deadline.Token).ConfigureAwait(false);
    }

    private async Task<PhoneSyncResult?> ConverseAsync(Stream stream, CancellationToken ct)
    {
        var hello = await PhoneSync.ReadJsonAsync<PhoneSync.Hello>(stream, ct).ConfigureAwait(false);

        if (hello is null || hello.Version != PhoneSync.Version)
        {
            await Refuse(stream, "This version of Campus does not understand that phone.", ct)
                .ConfigureAwait(false);
            return null;
        }

        var device = await _devices.GetAsync(hello.DeviceId, ct).ConfigureAwait(false);

        if (device?.SharedKey is not { Length: > 0 } secret)
        {
            await Refuse(stream, "That device has not been paired with this workspace.", ct)
                .ConfigureAwait(false);
            return null;
        }

        if (!device.Trusted)
        {
            await Refuse(stream, "That device is no longer trusted.", ct).ConfigureAwait(false);
            return null;
        }

        if (!PhoneSync.Verify(hello, secret))
        {
            // Being on the same network is not proof of anything, and this is where that is
            // enforced rather than assumed.
            await Refuse(stream, "That device could not prove it holds the pairing secret.", ct)
                .ConfigureAwait(false);
            return null;
        }

        await PhoneSync.WriteJsonAsync(stream, new PhoneSync.HelloAck
        {
            Accepted = true,
            WorkspaceName = workspaceName,
        }, ct).ConfigureAwait(false);

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

    private static Task Refuse(Stream stream, string reason, CancellationToken ct)
        => PhoneSync.WriteJsonAsync(stream, new PhoneSync.HelloAck
        {
            Accepted = false,
            Reason = reason,
        }, ct);

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
