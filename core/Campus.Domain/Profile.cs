namespace Campus.Domain;

/// <summary>Who the workspace belongs to, academically. Drives the Home greeting, planner and archive.</summary>
public sealed class AcademicProfile
{
    public string DisplayName { get; set; } = string.Empty;
    public string? School { get; set; }
    public string? Grade { get; set; }
    public string? Section { get; set; }
    public int AcademicYear { get; set; } = DateTimeOffset.Now.Year;
    public TermKind CurrentTerm { get; set; } = TermKind.Term1;

    public DateTimeOffset? YearStart { get; set; }
    public DateTimeOffset? YearEnd { get; set; }

    public string? StudyStyle { get; set; }
    public List<string> PersonalRules { get; init; } = [];
    public string? About { get; set; }
}

/// <summary>A recurring class slot in the weekly timetable.</summary>
public sealed class ScheduleSlot
{
    public CampusId Id { get; init; } = CampusId.New();
    public CampusId SubjectId { get; set; }
    public DayOfWeek Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public string? Room { get; set; }
    public TermKind? Term { get; set; }
    public int? AcademicYear { get; set; }
}

/// <summary>Everything the user can change about how Campus behaves. Persisted in the encrypted database.</summary>
public sealed class WorkspaceSettings
{
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    public string? ThemeId { get; set; }

    public AutoLockPolicy AutoLock { get; set; } = AutoLockPolicy.After10Minutes;
    public bool LockOnSystemLock { get; set; } = true;
    public bool LockOnAppClose { get; set; } = true;
    public bool RequireAuthForExport { get; set; } = true;
    public bool SensitiveMode { get; set; }

    public bool RunServiceAtStartup { get; set; } = true;
    public bool EnableUsbSync { get; set; } = true;
    public bool EnableLocalNetworkSync { get; set; } = true;

    public BackupSettings Backup { get; init; } = new();
    public AccessibilitySettings Accessibility { get; init; } = new();
    public EditorSettings Editor { get; init; } = new();

    public string Language { get; set; } = "en";
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Sunday;
}

public enum AppearanceMode { System = 0, Light = 1, Dark = 2 }

public enum AutoLockPolicy
{
    Never = 0,
    After5Minutes = 5,
    After10Minutes = 10,
    After30Minutes = 30,
}

public sealed class BackupSettings
{
    public bool Automatic { get; set; } = true;
    public BackupCadence Cadence { get; set; } = BackupCadence.Daily;
    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 6;
    public string? Destination { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

public enum BackupCadence { Manual = 0, Daily = 1, Weekly = 2 }

public sealed class AccessibilitySettings
{
    public double UiScale { get; set; } = 1.0;
    public double TextScale { get; set; } = 1.0;
    public bool ReduceMotion { get; set; }
    public bool ReduceTransparency { get; set; }
    public bool IncreaseContrast { get; set; }
    public bool LargeHitTargets { get; set; }
    public bool LargeCursor { get; set; }
    public bool AlwaysShowFocusRing { get; set; }
    public bool DyslexiaFriendlyReading { get; set; }
    public double ReadingLineSpacing { get; set; } = 1.0;
    public bool ReadingRuler { get; set; }
}

public sealed class EditorSettings
{
    public bool WordWrap { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowMinimap { get; set; }
    public int TabSize { get; set; } = 4;
    public MarkdownViewMode MarkdownView { get; set; } = MarkdownViewMode.LivePreview;
    public bool PreviewTabs { get; set; } = true;
}

public enum MarkdownViewMode { Editor = 0, Preview = 1, Split = 2, LivePreview = 3 }
