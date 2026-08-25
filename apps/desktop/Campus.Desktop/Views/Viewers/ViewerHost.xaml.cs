using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// A viewer knows how to draw one kind of content and nothing else. The host owns everything
/// around it — the title, the toolbar, exporting, opening elsewhere — so adding a viewer for a
/// new format means writing only the part that is genuinely new.
/// </summary>
public interface IContentViewer
{
    /// <summary>Loads a file from the vault. The stream is seekable and decrypted.</summary>
    Task LoadAsync(Stream content, CampusObject entity, FilePayload payload);

    /// <summary>Controls this viewer contributes to the host's toolbar.</summary>
    IEnumerable<FrameworkElement> BuildTools();
}

/// <summary>
/// Opens a file. Which viewer that means is decided by what the file actually is, which was
/// worked out at import time from its bytes rather than from its name.
/// </summary>
public sealed partial class ViewerHost : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    private CampusObject? _entity;
    private FilePayload? _payload;
    private Stream? _content;

    public ViewerHost()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _entity = await _workspace.Objects.GetAsync(id);
        _payload = _entity?.PayloadAs<FilePayload>();

        if (_entity is null || _payload is null)
        {
            ShowMessage("This is not a file", "There is nothing here to open.", offerExternal: false);
            return;
        }

        TitleText.Text = _entity.Title;
        KindIcon.Symbol = new ObjectItem(_entity).Symbol;
        DetailText.Text = Describe(_payload);

        await _workspace.Objects.MarkOpenedAsync(id);
        await OpenAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // The stream holds a decrypted view of the file; it closes with the page rather than
        // lingering until a garbage collection happens to notice.
        _content?.Dispose();
        _content = null;
    }

    private static string Describe(FilePayload payload)
    {
        var parts = new List<string> { ObjectItem.FormatSize(payload.SizeBytes) };
        if (payload.PageCount is { } pages) parts.Add($"{pages} page{(pages == 1 ? "" : "s")}");
        if (payload.PixelWidth is { } w && payload.PixelHeight is { } h) parts.Add($"{w}×{h}");
        if (payload.Extension.Length > 0) parts.Add(payload.Extension.TrimStart('.').ToUpperInvariant());
        return string.Join(" · ", parts);
    }

    private async Task OpenAsync()
    {
        if (_entity is null || _payload is null) return;

        try
        {
            _content = _workspace.Vault.Objects.OpenRead(_payload.ContentHash);
        }
        catch (FileNotFoundException)
        {
            ShowMessage("The file is missing",
                "Its record is here but its contents are not in the vault.", offerExternal: false);
            return;
        }

        var viewer = Match(_payload.Media, _payload.Extension);
        if (viewer is null)
        {
            ShowMessage("No viewer for this kind of file",
                $"Campus does not know how to show a {_payload.Extension.TrimStart('.').ToUpperInvariant()} "
                + "file yet. It is still stored, and still safe.",
                offerExternal: true);
            return;
        }

        ViewerSurface.Content = viewer;
        Message.Visibility = Visibility.Collapsed;

        await viewer.LoadAsync(_content, _entity, _payload);

        // Tools are built after loading, not before: what a viewer offers can depend on what it
        // found — a workbook with one sheet needs no sheet picker.
        ViewerTools.Children.Clear();
        foreach (var tool in viewer.BuildTools()) ViewerTools.Children.Add(tool);
    }

    /// <summary>
    /// Picks a viewer. Kept as one expression so the set of formats Campus can show is a single
    /// readable list rather than something to reconstruct from a dozen registrations.
    /// </summary>
    private static IContentViewer? Match(MediaKind media, string extension) => media switch
    {
        MediaKind.Pdf => new PdfViewer(),
        MediaKind.Image => new ImageViewer(),
        MediaKind.Video or MediaKind.Audio => new MediaViewer(),
        MediaKind.Markdown => new MarkdownViewer(),
        MediaKind.Text or MediaKind.Web => new TextViewer(),
        MediaKind.Document when extension is ".docx" => new OfficeViewer(),
        MediaKind.Presentation when extension is ".pptx" => new OfficeViewer(),
        MediaKind.Spreadsheet when extension is ".xlsx" or ".csv" or ".tsv" => new SheetViewer(),
        _ => null,
    };

    private void ShowMessage(string title, string body, bool offerExternal)
    {
        ViewerSurface.Content = null;
        Message.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageBody.Text = body;
        MessageAction.Visibility = offerExternal ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------------ actions

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
    }

    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_entity is not null) Frame?.Navigate(typeof(ObjectDetailPage), _entity.Id);
    }

    /// <summary>
    /// Writes a plaintext copy wherever the user asks. This is the only sanctioned way bytes
    /// leave the vault, and it is deliberately an explicit act rather than a side effect.
    /// </summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_entity is null || _payload is null) return;

        var picker = new FileSavePicker();
        InitialiseWithWindow(picker);
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_payload.OriginalFileName);

        var extension = _payload.Extension.Length > 0 ? _payload.Extension : ".dat";
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await _workspace.Vault.Objects.ExportAsync(_payload.ContentHash, file.Path);
        Notifications.Show($"Exported to {file.Name}");
    }

    /// <summary>
    /// Hands the file to whatever Windows normally opens it with. The copy goes to a temporary
    /// folder, because the other application has no way to read the vault.
    /// </summary>
    private async void OnOpenExternallyClick(object sender, RoutedEventArgs e)
    {
        if (_payload is null) return;

        var temporary = Path.Combine(Path.GetTempPath(), "Campus", _payload.OriginalFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        await _workspace.Vault.Objects.ExportAsync(_payload.ContentHash, temporary);

        await Windows.System.Launcher.LaunchUriAsync(new Uri($"file:///{temporary.Replace('\\', '/')}"));
        Notifications.Show(L.T("opened.a.copy.outside.campus.that.copy.is.not.62bdce"));
    }

    private static void InitialiseWithWindow(object picker)
    {
        // A picker in a desktop app has no window of its own and has to be told which one it
        // belongs to, or it never appears.
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }
}
