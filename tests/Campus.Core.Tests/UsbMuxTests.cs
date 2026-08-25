using Campus.Sync;
using Xunit;

namespace Campus.Core.Tests;

/// <summary>
/// The property list writer and reader, checked against the shapes usbmux actually sends.
///
/// The conversation with a device needs a device, so that part is not tested here. The framing
/// and the plist are, because they are where a mistake is silent: a wrong integer encoding or a
/// mis-parsed reply looks like "no phone attached" rather than like an error.
/// </summary>
public sealed class PropertyListTests
{
    [Fact]
    public void WritesTheRequestUsbmuxExpects()
    {
        var bytes = PropertyList.Write(new Dictionary<string, object>
        {
            ["MessageType"] = "ListDevices",
            ["kLibUSBMuxVersion"] = 3,
        });

        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("<key>MessageType</key><string>ListDevices</string>", text);
        Assert.Contains("<key>kLibUSBMuxVersion</key><integer>3</integer>", text);
        Assert.Contains("plist version=\"1.0\"", text);
    }

    [Fact]
    public void ReadsADeviceListReply()
    {
        // Exactly what a device attached over the cable looks like coming back.
        const string reply = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>DeviceList</key>
              <array>
                <dict>
                  <key>DeviceID</key><integer>7</integer>
                  <key>MessageType</key><string>Attached</string>
                  <key>Properties</key>
                  <dict>
                    <key>ConnectionType</key><string>USB</string>
                    <key>SerialNumber</key><string>00008120-001E312C3CF0201E</string>
                  </dict>
                </dict>
              </array>
            </dict></plist>
            """;

        var parsed = PropertyList.Read(System.Text.Encoding.UTF8.GetBytes(reply));
        var list = PropertyList.Array(parsed, "DeviceList");

        Assert.NotNull(list);
        Assert.Single(list);

        var entry = list[0];
        Assert.Equal(7L, PropertyList.Integer(entry, "DeviceID"));

        var properties = PropertyList.AsDictionary(entry)!["Properties"];
        Assert.Equal("USB", PropertyList.String(properties, "ConnectionType"));
        Assert.Equal("00008120-001E312C3CF0201E", PropertyList.String(properties, "SerialNumber"));
    }

    [Fact]
    public void ReadsAResultReply()
    {
        const string reply = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>MessageType</key><string>Result</string>
              <key>Number</key><integer>0</integer>
            </dict></plist>
            """;

        var parsed = PropertyList.Read(System.Text.Encoding.UTF8.GetBytes(reply));

        Assert.Equal("Result", PropertyList.String(parsed, "MessageType"));
        Assert.Equal(0L, PropertyList.Integer(parsed, "Number"));
    }

    [Fact]
    public void AShortenedSerialIsTheTailOfTheUdid()
    {
        var device = new UsbDevice(7, "00008120-001E312C3CF0201E", OverCable: true);
        Assert.Equal("3CF0201E", device.ShortSerial);
    }

    /// <summary>
    /// A plist carries a DTD that points at apple.com. Reading one must not fetch it — a sync
    /// that reaches out to the internet is not a sync over a cable.
    /// </summary>
    [Fact]
    public void DoesNotResolveApplesDtd()
    {
        const string reply = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>Number</key><integer>0</integer></dict></plist>
            """;

        var parsed = PropertyList.Read(System.Text.Encoding.UTF8.GetBytes(reply));
        Assert.Equal(0L, PropertyList.Integer(parsed, "Number"));
    }
}
