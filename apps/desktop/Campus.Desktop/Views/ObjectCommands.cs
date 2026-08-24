using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views;

/// <summary>
/// The things you can do to an object, wherever it is listed.
///
/// One definition rather than one per page: a right-click on a task in the task list, in a
/// subject, in search results and in the trash should offer the same verbs and carry them out the
/// same way. Only the destructive ones ask first, and the one that cannot be undone asks properly.
/// </summary>
public static class ObjectCommands
{
    /// <summary>
    /// Builds the menu for one object. <paramref name="refresh"/> is called after anything that
    /// changes what the list should show.
    /// </summary>
    public static MenuFlyout Build(CampusObject entity, XamlRoot? root, Func<Task> refresh)
    {
        var workspace = App.GetService<WorkspaceService>();
        var router = App.GetService<ShellRouter>();
        var menu = new MenuFlyout();

        var trashed = entity.DeletedAt is not null;

        if (trashed)
        {
            // Something in the trash has exactly two futures, and neither of them is "edit".
            menu.Items.Add(Item("Put back", CampusSymbols.Undo, async () =>
            {
                await workspace.Objects.RestoreAsync(entity.Id);
                await workspace.History.RecordAsync(entity.Id, "restored");
                Notifications.Show($"“{entity.Title}” is back.", NoticeKind.Success);
                await refresh();
            }));

            menu.Items.Add(new MenuFlyoutSeparator());

            menu.Items.Add(Destructive("Delete forever", CampusSymbols.Delete, async () =>
            {
                if (!await ConfirmAsync(root,
                    "Delete forever?",
                    $"“{entity.Title}” will be gone. This cannot be undone.",
                    "Delete forever")) return;

                await workspace.Objects.DeleteForeverAsync(entity.Id);
                Notifications.Show("Deleted.", NoticeKind.Info);
                await refresh();
            }));

            return menu;
        }

        menu.Items.Add(Item("Open", CampusSymbols.OpenExternal, () =>
        {
            router.Open(entity.Id);
            return Task.CompletedTask;
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item(entity.IsPinned ? "Unpin" : "Pin", CampusSymbols.Pin, async () =>
        {
            await workspace.Objects.SetFlagAsync(entity.Id, "is_pinned", !entity.IsPinned);
            await refresh();
        }));

        menu.Items.Add(Item(entity.IsFavorite ? "Remove from favourites" : "Add to favourites",
            CampusSymbols.Star, async () =>
        {
            await workspace.Objects.SetFlagAsync(entity.Id, "is_favorite", !entity.IsFavorite);
            await refresh();
        }));

        menu.Items.Add(Item("Rename", CampusSymbols.Rename, async () =>
        {
            var title = await AskAsync(root, "Rename", entity.Title);
            if (title is null) return;

            var was = entity.Title;
            entity.Title = title;
            await workspace.Objects.SaveAsync(entity);
            await workspace.History.RecordAsync(entity.Id, "renamed", $"from “{was}”");
            await refresh();
        }));

        menu.Items.Add(Item("Duplicate", CampusSymbols.Duplicate, async () =>
        {
            var copy = Duplicate(entity, workspace.DeviceId);
            await workspace.Objects.SaveAsync(copy);
            Notifications.Show($"Copied as “{copy.Title}”.");
            await refresh();
        }));

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SubjectSubmenu(entity, workspace, refresh));

        menu.Items.Add(Item(entity.IsArchived ? "Take out of the archive" : "Archive",
            CampusSymbols.Archive, async () =>
        {
            await workspace.Objects.SetFlagAsync(entity.Id, "is_archived", !entity.IsArchived);
            await workspace.History.RecordAsync(entity.Id, entity.IsArchived ? "unarchived" : "archived");
            await refresh();
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Destructive("Move to trash", CampusSymbols.Trash, async () =>
        {
            await workspace.Objects.TrashAsync(entity.Id);
            await workspace.History.RecordAsync(entity.Id, "trashed");
            Notifications.Show($"“{entity.Title}” moved to the trash.");
            await refresh();
        }));

        return menu;
    }

    /// <summary>
    /// The submenu that moves something between subjects. Built from the subjects that exist, so
    /// there is never an option for a subject that was renamed or removed.
    /// </summary>
    private static MenuFlyoutSubItem SubjectSubmenu(
        CampusObject entity, WorkspaceService workspace, Func<Task> refresh)
    {
        var submenu = new MenuFlyoutSubItem { Text = "Subject" };
        submenu.Icon = IconFor(CampusSymbols.Subjects);

        // Populated on first open rather than up front: a right-click should not wait on a query
        // for a submenu that may never be opened.
        submenu.Loaded += async (_, _) =>
        {
            if (submenu.Items.Count > 0 || !workspace.IsUnlocked) return;

            var subjects = await workspace.Objects.QueryAsync(new CampusQuery
            {
                Kinds = { ObjectKind.Subject },
                Sort = SortField.Manual,
                Descending = false,
            });

            submenu.Items.Add(Item("None", CampusSymbols.Close, async () =>
            {
                entity.SubjectId = null;
                await workspace.Objects.SaveAsync(entity);
                await refresh();
            }));

            if (subjects.Count > 0) submenu.Items.Add(new MenuFlyoutSeparator());

            foreach (var subject in subjects)
            {
                var id = subject.Id;
                submenu.Items.Add(Item(subject.Title, CampusSymbols.Subjects, async () =>
                {
                    entity.SubjectId = id;
                    await workspace.Objects.SaveAsync(entity);
                    await workspace.History.RecordAsync(entity.Id, "moved", $"to {subject.Title}");
                    await refresh();
                }));
            }
        };

        return submenu;
    }

    /// <summary>
    /// A copy, with a new id and a title that says it is one. The payload is round-tripped through
    /// its serialised form so nothing is shared by reference with the original — otherwise editing
    /// the copy's checklist would edit the original's too.
    /// </summary>
    public static CampusObject Duplicate(CampusObject entity, string deviceId) => new()
    {
        Title = entity.Title + " copy",
        Kind = entity.Kind,
        Summary = entity.Summary,
        SubjectId = entity.SubjectId,
        ParentId = entity.ParentId,
        Status = ObjectStatus.NotStarted,
        Priority = entity.Priority,
        DueAt = entity.DueAt,
        AcademicYear = entity.AcademicYear,
        Term = entity.Term,
        Source = CaptureSource.Desktop,
        SourceDeviceId = deviceId,
        Payload = Storage.PayloadSerializer.Deserialize(
            entity.Kind, Storage.PayloadSerializer.Serialize(entity.Payload)),
        Tags = [.. entity.Tags],
    };

    // -------------------------------------------------------------------- menu parts

    public static MenuFlyoutItem Item(string text, string symbol, Func<Task> invoke)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = IconFor(symbol) };
        item.Click += (_, _) => _ = invoke();
        return item;
    }

    private static MenuFlyoutItem Destructive(string text, string symbol, Func<Task> invoke)
    {
        var item = Item(text, symbol, invoke);
        item.Foreground = (Brush)Application.Current.Resources[ThemeTokens.Destructive.Primary];
        return item;
    }

    /// <summary>
    /// Wraps a Campus icon so it can sit in a platform menu, which expects an IconElement rather
    /// than an arbitrary control.
    /// </summary>
    private static IconElement IconFor(string symbol) => new IconSourceElement
    {
        IconSource = new PathIconSource
        {
            Data = IconRegistry.Resolve(symbol).Geometry,
        },
    };

    // ------------------------------------------------------------------------ dialogs

    public static async Task<bool> ConfirmAsync(
        XamlRoot? root, string title, string message, string confirmLabel)
    {
        if (root is null) return false;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> AskAsync(
        XamlRoot? root, string title, string initial = "", string? placeholder = null)
    {
        if (root is null) return null;

        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder ?? "",
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        input.Loaded += (_, _) => input.SelectAll();

        var result = await dialog.ShowAsync();
        var text = input.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }
}
