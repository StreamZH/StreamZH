// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Root folders (/rootfolder): each one becomes a StreamZH root folder of the app's media type at the mapped path. A root
/// folder StreamZH already has is linked; one StreamZH cannot see is skipped until a path mapping is added.
/// </summary>
public sealed class RootFoldersSection(string source) : ArrSettingsSection(source)
{
    public const string IdKind = "rootFolder";

    public override string Key => "rootFolders";

    public override string Label => "Root folders";

    public override string Description => IsRadarr
        ? "The folders your movies live in, as StreamZH sees them (through the path mappings)."
        : "The folders your series live in, as StreamZH sees them (through the path mappings).";

    public override int Order => 100;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var roots = reader.Service<RootFolderRepository>();
        var existing = roots.All();
        var paths = reader.Context.Paths;
        var planned = new List<string>();

        foreach (var root in await reader.GetListAsync("/rootfolder", cancellationToken))
        {
            var id = ArrJson.Int(root, "id") ?? 0;
            var remote = ArrJson.Str(root, "path")?.Trim() ?? string.Empty;
            var key = ItemKey(id);
            if (remote.Length == 0)
            {
                continue;
            }

            paths.Use(remote);
            var local = paths.Map(remote).Trim();
            var label = remote;
            var detail = local == remote ? local : $"{remote} → {local}";

            var match = existing.FirstOrDefault(r => PathComparer.Instance.Equals(r.Path, local));
            if (match is not null)
            {
                if (match.MediaType != reader.MediaType)
                {
                    plan.Skip(key, label, $"StreamZH uses this folder for {(match.MediaType == MediaType.Movie ? "movies" : "series")} already.", detail);
                    continue;
                }

                var matchId = match.Id;
                plan.Add(new MigrationItem(key, label, MigrationAction.Link, detail, "StreamZH has this root folder already."), _ => reader.Context.Ids.Set(IdKind, id, matchId));
                continue;
            }

            if (Path.IsPathFullyQualified(local) && paths.StorageProblem(local) is { } outside)
            {
                // 0.12 (SV3 W-03): not looked at before the owner allows it (the wizard offers that in one click).
                plan.Skip(key, label, outside, detail);
                continue;
            }

            if (!Path.IsPathFullyQualified(local) || !paths.FolderExists(local))
            {
                plan.Skip(
                    key,
                    label,
                    $"StreamZH cannot see {local}.",
                    detail,
                    paths.IsMapped(remote)
                        ? $"The folder {local} does not exist for StreamZH. Check the path mapping for {remote}."
                        : $"StreamZH cannot see {remote}. Add a path mapping (e.g. {remote} → the same folder on this computer) and preview again.");
                continue;
            }

            var nested = existing.Select(r => r.Path).Concat(planned).FirstOrDefault(p => IsInside(local, p) || IsInside(p, local));
            if (nested is not null)
            {
                plan.Skip(key, label, $"Root folders cannot be inside each other ({nested}).", detail);
                continue;
            }

            planned.Add(local);
            plan.Add(new MigrationItem(key, label, MigrationAction.Create, detail), _ =>
            {
                var created = reader.Service<RootFolderService>().Add(local, reader.MediaType);
                reader.Context.Ids.Set(IdKind, id, created.Id);
            });
        }

        if (plan.Items.Count == 0)
        {
            plan.Warnings.Add($"{reader.AppName} has no root folders.");
        }

        return plan;
    }


    private static bool IsInside(string path, string parent)
    {
        if (PathComparer.Instance.Equals(path, parent))
        {
            return false;
        }

        var relative = Path.GetRelativePath(parent, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
