using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>
/// What the sidebar and the workspace agree they are looking at. The sidebar narrows; the page
/// renders. Keeping the filter here rather than inside either one means switching destination
/// and coming back does not silently lose the narrowing.
/// </summary>
public sealed class NavigationState
{
    private CampusId? _subjectId;
    private string? _tag;

    /// <summary>Raised when the filter changes and the current list should be re-read.</summary>
    public event EventHandler? FilterChanged;

    /// <summary>The subject the workspace is narrowed to, or null for all subjects.</summary>
    public CampusId? SubjectId
    {
        get => _subjectId;
        set
        {
            if (Nullable.Equals(_subjectId, value)) return;
            _subjectId = value;
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The tag the workspace is narrowed to, or null for all tags.</summary>
    public string? Tag
    {
        get => _tag;
        set
        {
            if (string.Equals(_tag, value, StringComparison.Ordinal)) return;
            _tag = value;
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasFilter => _subjectId is not null || _tag is not null;

    public void Clear()
    {
        if (!HasFilter) return;
        _subjectId = null;
        _tag = null;
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the current narrowing to a query built by a collection segment.</summary>
    public void Apply(CampusQuery query)
    {
        if (_subjectId is { } subject) query.SubjectIds.Add(subject);
        if (_tag is { } tag) query.TagsAll.Add(tag);
    }
}
