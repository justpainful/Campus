using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// The weekly timetable.
///
/// Slots are separate from events because they are not events: a class at 08:15 on Sundays is one
/// fact, not thirty-six calendar entries, and it should be editable as one fact. The planner
/// projects them onto real dates when it draws a week.
/// </summary>
public sealed class ScheduleRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<IReadOnlyList<ScheduleSlot>> AllAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM schedule_slots ORDER BY day, start_minutes;");
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleSlot>> ForDayAsync(
        DayOfWeek day, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM schedule_slots WHERE day = @day ORDER BY start_minutes;");
        command.Parameters.AddWithValue("@day", (int)day);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleSlot>> ForSubjectAsync(
        CampusId subjectId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM schedule_slots WHERE subject_id = @id ORDER BY day, start_minutes;");
        command.Parameters.AddWithValue("@id", subjectId.Value);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(ScheduleSlot slot, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO schedule_slots
                (id, subject_id, day, start_minutes, end_minutes, room, term, academic_year)
            VALUES (@id, @subject, @day, @start, @end, @room, @term, @year)
            ON CONFLICT(id) DO UPDATE SET
                subject_id = excluded.subject_id,
                day = excluded.day,
                start_minutes = excluded.start_minutes,
                end_minutes = excluded.end_minutes,
                room = excluded.room,
                term = excluded.term,
                academic_year = excluded.academic_year;
            """);

        command.Parameters.AddWithValue("@id", slot.Id.Value);
        command.Parameters.AddWithValue("@subject", slot.SubjectId.Value);
        command.Parameters.AddWithValue("@day", (int)slot.Day);
        // Stored as minutes past midnight so no time zone or daylight change can move a lesson.
        command.Parameters.AddWithValue("@start", Minutes(slot.Start));
        command.Parameters.AddWithValue("@end", Minutes(slot.End));
        command.Parameters.AddWithValue("@room", (object?)slot.Room ?? DBNull.Value);
        command.Parameters.AddWithValue("@term", slot.Term is { } t ? (int)t : DBNull.Value);
        command.Parameters.AddWithValue("@year", (object?)slot.AcademicYear ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM schedule_slots WHERE id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ScheduleSlot>> ReadAllAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var slots = new List<ScheduleSlot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var termIndex = reader.GetOrdinal("term");
            var yearIndex = reader.GetOrdinal("academic_year");
            var roomIndex = reader.GetOrdinal("room");

            slots.Add(new ScheduleSlot
            {
                Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                SubjectId = CampusId.Parse(reader.GetString(reader.GetOrdinal("subject_id"))),
                Day = (DayOfWeek)reader.GetInt32(reader.GetOrdinal("day")),
                Start = FromMinutes(reader.GetInt32(reader.GetOrdinal("start_minutes"))),
                End = FromMinutes(reader.GetInt32(reader.GetOrdinal("end_minutes"))),
                Room = reader.IsDBNull(roomIndex) ? null : reader.GetString(roomIndex),
                Term = reader.IsDBNull(termIndex) ? null : (TermKind)reader.GetInt32(termIndex),
                AcademicYear = reader.IsDBNull(yearIndex) ? null : reader.GetInt32(yearIndex),
            });
        }

        return slots;
    }

    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;

    private static TimeOnly FromMinutes(int minutes)
        => new(Math.Clamp(minutes / 60, 0, 23), Math.Clamp(minutes % 60, 0, 59));
}

/// <summary>Small conveniences the timetable views want and the model does not need to carry.</summary>
public static class ScheduleSlotExtensions
{
    public static int StartMinutes(this ScheduleSlot slot) => slot.Start.Hour * 60 + slot.Start.Minute;
    public static int EndMinutes(this ScheduleSlot slot) => slot.End.Hour * 60 + slot.End.Minute;

    public static int DurationMinutes(this ScheduleSlot slot)
        => Math.Max(0, slot.EndMinutes() - slot.StartMinutes());
}
