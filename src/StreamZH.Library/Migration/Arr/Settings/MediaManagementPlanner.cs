// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StreamZH.Library.Import;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Builds one preview item per Media Management value of the old app (naming, importing, file management) and, in the
/// run, collects the chosen ones into one draft saved at the end. Values StreamZH would reject keep StreamZH's value with
/// a warning. StreamZH has one value for movies and series where Radarr and Sonarr have one each (e.g. the colon
/// replacement); when StreamZH's value is no longer its default it probably came from the other app, which the item says.
/// </summary>
public sealed class MediaManagementPlanner
{
    private static readonly MediaManagementValues Defaults = new();

    private readonly SettingsPlan _plan;
    private readonly MediaManagementSettings _settings;
    private readonly string _section;
    private readonly string _other;
    private readonly MediaManagementValues _current;
    private readonly Dictionary<string, (string Label, Func<MediaManagementValues, MediaManagementValues, MediaManagementValues> Restore)> _fields = new(StringComparer.Ordinal);
    private MediaManagementValues _draft;

    public MediaManagementPlanner(SettingsPlan plan, MediaManagementSettings settings, string section, string otherApp)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        _plan = plan;
        _settings = settings;
        _section = section;
        _other = otherApp;
        _current = settings.Current;
        _draft = _current;
        plan.Commit = Commit;
    }

    public MediaManagementValues Current => _current;

    /// <summary>
    /// One value of the old app. <paramref name="field"/> is the camelCase name (item key and validation key),
    /// <paramref name="shared"/> marks values StreamZH uses for movies and series alike.
    /// </summary>
    public void Value<T>(
        string field,
        string label,
        T value,
        Func<MediaManagementValues, T> get,
        Func<MediaManagementValues, T, MediaManagementValues> set,
        bool shared = false,
        string? note = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        var key = $"{_section}:{field}";
        var now = get(_current);
        if (EqualityComparer<T>.Default.Equals(now, value))
        {
            _plan.Skip(key, label, "Already the same.", Show(now));
            return;
        }

        // Checked without touching the disk: the recycle bin only gets the checks that need no write (saving checks it fully).
        var probe = set(_current with { RecycleBin = string.Empty }, value);
        var errors = MediaManagementSettings.Validate(field == RecycleBinField ? probe with { RecycleBin = string.Empty } : probe);
        if (field == RecycleBinField && RecycleBinProblem(value as string) is { } binProblem)
        {
            errors[field] = [binProblem];
        }

        if (errors.TryGetValue(field, out var problems))
        {
            _plan.Skip(
                key,
                label,
                "StreamZH cannot use this value.",
                Show(value),
                $"StreamZH cannot use {Show(value)} for {label.ToLowerInvariant()}: {string.Join(" ", problems)} It keeps {Show(now)}.");
            return;
        }

        var warning = note;
        if (shared && !EqualityComparer<T>.Default.Equals(now, get(Defaults)))
        {
            var sharedNote = $"StreamZH has one value for movies and series; it was {Show(now)} (perhaps from {_other}) and becomes {Show(value)}.";
            warning = warning is null ? sharedNote : $"{warning} {sharedNote}";
        }

        _fields[field] = (label, (draft, current) => set(draft, get(current)));
        _plan.Add(new MigrationItem(key, label, MigrationAction.Update, $"{Show(now)} → {Show(value)}", Warning: warning), _ => _draft = set(_draft, value));
    }

    private const string RecycleBinField = "recycleBin";

    /// <summary>A recycle bin must be a full path to a folder that exists (Radarr and Sonarr ask the same).</summary>
    private static string? RecycleBinProblem(string? path)
    {
        var bin = path?.Trim() ?? string.Empty;
        if (bin.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(bin))
        {
            return "Enter a full path.";
        }

        try
        {
            return Directory.Exists(bin) ? null : "StreamZH cannot see this folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "StreamZH cannot see this folder.";
        }
    }

    /// <summary>Saves the draft; a value StreamZH still rejects (e.g. a recycle bin it cannot write to) goes back to StreamZH's.</summary>
    private void Commit(MigrationSectionRun run)
    {
        if (ReferenceEquals(_draft, _current))
        {
            return;
        }

        try
        {
            _settings.Update(_draft);
        }
        catch (ValidationFailedException ex)
        {
            var draft = _draft;
            foreach (var (field, messages) in ex.Errors)
            {
                if (_fields.TryGetValue(field, out var entry))
                {
                    draft = entry.Restore(draft, _current);
                    run.Warn($"StreamZH keeps its {entry.Label.ToLowerInvariant()}: {string.Join(" ", messages)}", entry.Label);
                }
            }

            _settings.Update(draft);
        }
    }

    /// <summary>Lists a value of the old app StreamZH does not have, as a skipped item.</summary>
    public void Unsupported(string field, string label, string reason, string? detail = null, string? warning = null) =>
        _plan.Skip($"{_section}:{field}", label, reason, detail, warning);

    public static string Show<T>(T value) => value switch
    {
        null => "(none)",
        bool b => b ? "on" : "off",
        string { Length: 0 } => "(empty)",
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        Enum e => JsonNamingPolicy.CamelCase.ConvertName(e.ToString()),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
