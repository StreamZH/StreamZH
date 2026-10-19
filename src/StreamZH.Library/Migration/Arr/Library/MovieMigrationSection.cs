// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Radarr's movies: every movie becomes a StreamZH movie (matched by TMDB id) with its monitoring, quality profile, root
/// folder, folder and added date. The movie file is linked where it is — never moved or renamed — with Radarr's quality,
/// languages, release group and original release name, so it shows as on disk right away and custom formats score it
/// like Radarr did. Metadata comes from TMDB as usual, one movie at a time; nothing is searched or grabbed.
/// Options: "movies.monitor" = "asIs" (default) or "none" (every new movie unmonitored, handy side by side);
/// "movies.overwrite" (default off) also takes monitoring and quality profile over for movies already in StreamZH.
/// </summary>
public sealed class MovieMigrationSection(ArrLibraryReader reader, ILogger<MovieMigrationSection> logger) : IMigrationSection
{
    public const string MonitorOption = "movies.monitor";
    public const string OverwriteOption = "movies.overwrite";

    public string Source => MigrationSources.Radarr;

    public string Key => "movies";

    public string Label => "Movies";

    public string Description => "Every movie with its monitoring, quality profile and folder. Files are linked where they are: nothing is moved, renamed or downloaded.";

    public int Order => 500;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var plan = await PlanAsync(context, scope.ServiceProvider, cancellationToken);
        return new MigrationSectionPlan([.. plan.Entries.Select(e => e.Item)], plan.Warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var tmdb = services.GetRequiredService<ITmdbClient>();
        if (!tmdb.IsConfigured)
        {
            run.Failed(Label, TmdbMissing);
            return;
        }

        var plan = await PlanAsync(context, services, cancellationToken);
        var library = services.GetRequiredService<LibraryService>();
        var movies = services.GetRequiredService<MovieRepository>();
        var details = services.GetRequiredService<MediaFileDetailsRepository>();
        var store = services.GetRequiredService<LibraryMigrationStore>();
        var events = services.GetRequiredService<IEventBus>();
        var targets = plan.Targets(services, allowCreate: true);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        void WarnOnce(string text, string? item = null)
        {
            if (warned.Add(text))
            {
                run.Warn(text, item);
            }
        }

        var monitorNone = context.Option(MonitorOption, "asIs").Equals("none", StringComparison.OrdinalIgnoreCase);
        var overwrite = context.OptionOn(OverwriteOption, false);
        var total = plan.Entries.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = plan.Entries[i];
            run.Progress(i, total, $"Movie {(i + 1).ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}");
            var label = entry.Item.Label;
            if (context.IsExcluded(entry.Item.Key))
            {
                run.Skipped(label, "Left out in the wizard.");
                continue;
            }

            if (entry.Item.Action == MigrationAction.Skip)
            {
                run.Skipped(label, entry.Item.Reason ?? "Not taken over.");
                continue;
            }

            try
            {
                var existing = movies.FindByTmdbId(entry.Movie.TmdbId);
                var profile = targets.Profile(entry.Movie.QualityProfileId);
                if (existing is null)
                {
                    var root = targets.RootFolder(entry.Movie.RootFolderPath, entry.LocalFolder, allowCreate: true);
                    if (root.Id is not { } rootId)
                    {
                        run.Skipped(label, root.Warning ?? "No root folder.");
                        continue;
                    }

                    if (root.Warning is not null)
                    {
                        WarnOnce(root.Warning);
                    }

                    if (profile.Warning is not null)
                    {
                        WarnOnce(profile.Warning);
                    }

                    var movie = await TmdbRetry.RunAsync(
                        () => library.AddMovieAsync(entry.Movie.TmdbId, rootId, entry.Movie.Monitored && !monitorNone, profile.Id, entry.LocalFolder, cancellationToken),
                        cancellationToken);
                    if (entry.Movie.Added != default)
                    {
                        store.SetMovieAdded(movie.Id, entry.Movie.Added);
                    }

                    LinkFile(context, run, entry, movie, movies, details, replaceDetails: true);
                    context.Ids.Set("movie", entry.Movie.Id, movie.Id);
                    await events.PublishAsync(new LibraryChanged(MediaType.Movie, movie.Id), cancellationToken);
                    run.Created(label);
                }
                else
                {
                    if (overwrite)
                    {
                        if (profile.Warning is not null)
                        {
                            WarnOnce(profile.Warning);
                        }

                        existing.Monitored = entry.Movie.Monitored && !monitorNone;
                        existing.QualityProfileId = profile.Id ?? existing.QualityProfileId;
                        movies.Update(existing);
                    }

                    LinkFile(context, run, entry, existing, movies, details, replaceDetails: overwrite);
                    context.Ids.Set("movie", entry.Movie.Id, existing.Id);
                    await events.PublishAsync(new LibraryChanged(MediaType.Movie, existing.Id), cancellationToken);
                    if (overwrite)
                    {
                        run.Updated(label);
                    }
                    else
                    {
                        run.Linked(label);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TmdbException or LibraryException or IOException or UnauthorizedAccessException or ValidationFailedException)
            {
                logger.LogWarning("Could not take over the movie {Title}: {Message}", label, ex.Message);
                run.Failed(label, ex is TmdbException { StatusCode: 404 } ? $"TMDB does not know the movie {entry.Movie.TmdbId} any more." : ex.Message);
            }
        }

        run.Progress(total, total, $"{total.ToString(CultureInfo.InvariantCulture)} movies");
        foreach (var warning in plan.Warnings.Where(w => w != TmdbMissing))
        {
            run.Info(warning);
        }
    }

    /// <summary>
    /// Links Radarr's file to the movie when it is on disk: the file record of a movie without one (or with the same file;
    /// with <paramref name="replaceDetails"/> — new movie or "overwrite" — Radarr's file wins), plus the file details
    /// (Radarr's quality and release name) unless StreamZH already has its own.
    /// </summary>
    private static void LinkFile(
        MigrationContext context, MigrationSectionRun run, MovieEntry entry, Movie movie, MovieRepository movies, MediaFileDetailsRepository details, bool replaceDetails)
    {
        if (entry.File is null || entry.LocalFile is null)
        {
            return;
        }

        var file = new FileInfo(entry.LocalFile);
        if (!file.Exists)
        {
            run.Warn($"The file {entry.LocalFile} was not found; the movie shows as missing. Check the path mapping.", entry.Item.Label);
            return;
        }

        if (!LibraryTargets.IsInside(file.FullName, movie.FolderPath))
        {
            run.Warn($"Radarr's file {entry.LocalFile} is outside StreamZH's folder {movie.FolderPath}; StreamZH keeps its own.", entry.Item.Label);
            return;
        }

        var relative = Path.GetRelativePath(movie.FolderPath, file.FullName);
        var current = movie.File is null ? null : Path.GetFullPath(Path.Combine(movie.FolderPath, movie.File.RelativePath));
        if (!replaceDetails && current is not null && !PathComparer.Instance.Equals(current, file.FullName) && File.Exists(current))
        {
            // StreamZH already has another file for this movie; keep its data (a new movie or "overwrite" takes Radarr's).
            return;
        }

        movie.File = new MediaFile(relative, file.Length, file.LastWriteTimeUtc);
        movies.Update(movie);
        if (replaceDetails || details.Find(file.FullName) is null)
        {
            details.Save(ArrLibraryMapping.DetailsOf(context.Source, entry.File, file.FullName, entry.File.Edition, file));
        }
    }

    internal const string TmdbMissing = "TMDB is not set up: add your TMDB API key in Settings → Metadata before taking over the library.";

    private async Task<MoviePlan> PlanAsync(MigrationContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var arrMovies = await reader.MoviesAsync(context, cancellationToken);
        var roots = await reader.RootFoldersAsync(context, cancellationToken);
        var profiles = await reader.QualityProfilesAsync(context, cancellationToken);
        var files = await reader.MovieFilesAsync(context, cancellationToken);
        foreach (var root in roots)
        {
            context.Paths.Use(root.Path ?? string.Empty);
        }

        var movies = services.GetRequiredService<MovieRepository>();
        var inLibrary = movies.TmdbIdToId();
        var folders = movies.FolderPaths();
        var plan = new MoviePlan(context, roots, profiles);
        var targets = plan.Targets(services, allowCreate: false);
        var overwrite = context.OptionOn(OverwriteOption, false);
        var warnings = new List<string>();
        if (!services.GetRequiredService<ITmdbClient>().IsConfigured)
        {
            warnings.Add(TmdbMissing);
        }

        var seen = new HashSet<int>();
        foreach (var movie in arrMovies.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            var label = movie.Year > 0 ? $"{movie.Title} ({movie.Year.ToString(CultureInfo.InvariantCulture)})" : movie.Title ?? $"Movie {movie.Id}";
            if (!string.IsNullOrEmpty(movie.RootFolderPath))
            {
                context.Paths.Use(movie.RootFolderPath);
            }

            var localFolder = context.Paths.Map(movie.Path ?? string.Empty);
            files.TryGetValue(movie.Id, out var file);
            var localFile = file is null ? null : LocalFilePath(context, movie.Path, file.Path, file.RelativePath);
            var key = $"movies:{(movie.TmdbId > 0 ? movie.TmdbId : movie.Id).ToString(CultureInfo.InvariantCulture)}";
            if (movie.TmdbId <= 0)
            {
                plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(
                    $"movies:radarr{movie.Id.ToString(CultureInfo.InvariantCulture)}", label, MigrationAction.Skip, Reason: "Radarr has no TMDB id for this movie.")));
                continue;
            }

            if (!seen.Add(movie.TmdbId))
            {
                plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(
                    $"movies:radarr{movie.Id.ToString(CultureInfo.InvariantCulture)}", label, MigrationAction.Skip, Reason: "Radarr lists this movie twice.")));
                continue;
            }

            if (string.IsNullOrWhiteSpace(movie.Path))
            {
                plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(key, label, MigrationAction.Skip, Reason: "Radarr has no folder for this movie.")));
                continue;
            }

            var exists = inLibrary.ContainsKey(movie.TmdbId);
            if (!exists && folders.Contains(localFolder))
            {
                plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(
                    key, label, MigrationAction.Skip, Reason: $"Another StreamZH movie already uses the folder {localFolder}.")));
                continue;
            }

            var problems = new List<string>();
            if (!exists)
            {
                var root = targets.RootFolder(movie.RootFolderPath, localFolder, allowCreate: false);
                if (root.Id is null && !root.WillCreate)
                {
                    plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(key, label, MigrationAction.Skip, Reason: root.Warning)));
                    continue;
                }

                if (root.Warning is not null)
                {
                    problems.Add(root.Warning);
                }
            }

            if (!exists || overwrite)
            {
                if (targets.Profile(movie.QualityProfileId).Warning is { } profileWarning)
                {
                    problems.Add(profileWarning);
                }
            }

            if (!Directory.Exists(localFolder))
            {
                problems.Add($"StreamZH cannot see the folder {localFolder}" + (context.Paths.IsMapped(movie.Path) ? "." : "; add a path mapping."));
            }
            else if (localFile is not null && !File.Exists(localFile))
            {
                problems.Add($"The file {localFile} was not found; the movie will show as missing.");
            }

            var quality = file is null ? null : ArrLibraryMapping.QualityOf(context.Source, file.Quality?.Quality).Name;
            var detail = file is null
                ? $"No file · {localFolder}"
                : $"{quality} · {ArrLibraryMapping.Size(file.Size)} · {localFolder}";
            if (exists)
            {
                detail = (overwrite ? "Already in StreamZH: monitoring and profile are updated, the file is linked. " : "Already in StreamZH: its data stays, the file is linked. ") + detail;
            }

            var action = exists ? (overwrite ? MigrationAction.Update : MigrationAction.Link) : MigrationAction.Create;
            plan.Entries.Add(new MovieEntry(movie, file, localFolder, localFile, new MigrationItem(
                key, label, action, detail, Warning: problems.Count == 0 ? null : string.Join(" ", problems.Distinct()))));
        }

        if (arrMovies.Any(m => m.Tags is { Count: > 0 }))
        {
            warnings.Add("StreamZH has no tags yet; Radarr's movie tags are not taken over.");
        }

        if (arrMovies.Select(m => m.MinimumAvailability).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            warnings.Add("Radarr sets the minimum availability per movie; StreamZH has one setting for all movies (Settings → Indexers).");
        }

        plan.Warnings.AddRange(warnings);
        return plan;
    }

    /// <summary>The local path of an old app's file: its full path mapped, else the item folder plus the relative path.</summary>
    internal static string? LocalFilePath(MigrationContext context, string? itemPath, string? filePath, string? relativePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return context.Paths.Map(filePath);
        }

        if (!string.IsNullOrWhiteSpace(itemPath) && !string.IsNullOrWhiteSpace(relativePath))
        {
            return context.Paths.Map(itemPath.TrimEnd('/', '\\') + "/" + relativePath.Replace('\\', '/'));
        }

        return null;
    }

    private sealed record MovieEntry(ArrMovie Movie, ArrMovieFile? File, string LocalFolder, string? LocalFile, MigrationItem Item);

    private sealed class MoviePlan(MigrationContext context, IReadOnlyList<ArrRootFolder> roots, IReadOnlyList<ArrQualityProfile> profiles)
    {
        public List<MovieEntry> Entries { get; } = [];

        public List<string> Warnings { get; } = [];

        public LibraryTargets Targets(IServiceProvider services, bool allowCreate) => new(
            context,
            MediaType.Movie,
            roots,
            profiles,
            services.GetRequiredService<RootFolderRepository>(),
            services.GetRequiredService<QualityProfileService>(),
            allowCreate ? services.GetService<RootFolderService>() : null);
    }
}
