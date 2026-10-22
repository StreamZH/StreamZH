// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Requests;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>
/// 1.0 RC: the "Get started" checklist for owners and admins. Every step's state comes from the real configuration
/// (nothing is ticked by hand), so taking over Radarr/Sonarr with the migration wizard fills the steps as well. The
/// checklist can be dismissed for this instance; it also disappears once every required step is done.
/// </summary>
public static class SetupGuideEndpoints
{
    public static IEndpointRouteBuilder MapSetupGuideEndpoints(this IEndpointRouteBuilder app)
    {
        var guide = app.MapGroup("/api/v1/setup/guide").WithTags("Setup guide").RequireAuthorization(AuthSchemes.AdminPolicy);

        guide.MapGet("/", (HttpContext http, IServiceProvider services) => Build(services, GeneralSettingsEndpoints.IsOwner(http.User), http.Request));

        guide.MapPut("/", (SetupGuideUpdate update, HttpContext http, IServiceProvider services, ConfigRepository config) =>
        {
            if (update.Dismissed is not { } dismissed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["dismissed"] = ["Send true or false."] });
            }

            config.Set(ConfigKeys.SetupGuideDismissed, dismissed ? "true" : "false");
            return Results.Ok(Build(services, GeneralSettingsEndpoints.IsOwner(http.User), http.Request));
        });

        // The release language: sets the default quality profile. any/original switch "HD-1080p" to that language; a
        // language (native + its ISO 639-1 code, or german) uses "<Language> 1080p", created like "Deutsch 1080p" when
        // missing ("Deutsch 1080p" itself for German).
        guide.MapPut("/language", (SetupLanguageUpdate update, HttpContext http, IServiceProvider services, ConfigRepository config) =>
        {
            var choice = update.Language?.Trim().ToLowerInvariant();
            Language? wanted = choice switch
            {
                LanguageGerman => Language.German,
                LanguageNative => Language.FromIsoCode(update.Code) is var code && LanguageRules.IsReal(code) ? code : null,
                _ => null,
            };

            if (choice is not (LanguageAny or LanguageOriginal or LanguageGerman or LanguageNative))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["language"] = ["Choose any, original, german or native."] });
            }

            if (choice == LanguageNative && wanted is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["Send the ISO 639-1 code of a language StreamZH knows, e.g. tr or fr."],
                });
            }

            var repository = services.GetRequiredService<QualityProfileRepository>();
            var profiles = services.GetRequiredService<QualityProfileService>();
            QualityProfile? profile;
            if (wanted is null)
            {
                const string name = "HD-1080p";
                profile = repository.Find(QualityDefaults.DefaultProfileId) is { } byId && byId.Name == name ? byId : repository.FindByName(name);
                if (profile is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["language"] = [$"The quality profile {name} no longer exists. Choose the default profile in Settings → Profiles."],
                    });
                }
            }
            else
            {
                profile = LanguageProfile(wanted, repository, profiles);
            }

            var languageId = wanted?.Id ?? (choice == LanguageAny ? Language.Any.Id : Language.Original.Id);
            if (profile.LanguageId != languageId)
            {
                profile = profiles.Update(profile.Id, profile with { LanguageId = languageId });
            }

            profiles.SetDefault(profile.Id);
            config.Set(ConfigKeys.SetupReleaseLanguage, wanted is null ? choice : wanted == Language.German ? LanguageGerman : wanted.IsoCode!);
            return Results.Ok(Build(services, GeneralSettingsEndpoints.IsOwner(http.User), http.Request));
        });

        return app;
    }

    public const string LanguageAny = "any";
    public const string LanguageOriginal = "original";
    public const string LanguageGerman = "german";

    /// <summary>A specific language other than German, sent with its ISO 639-1 code (the user's own language in the web app).</summary>
    public const string LanguageNative = "native";

    /// <summary>
    /// The 1080p profile for a release language: "Deutsch 1080p" (id 4, or by name) for German, "&lt;Language&gt; 1080p"
    /// by name for the others. Created like "Deutsch 1080p" when it does not exist, so choosing a language twice never
    /// adds a second profile.
    /// </summary>
    public static QualityProfile LanguageProfile(Language language, QualityProfileRepository repository, QualityProfileService profiles)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(profiles);
        var name = QualityDefaults.LanguageProfileName(language);
        if (language == Language.German && repository.Find(QualityDefaults.GermanProfileId) is { } german && german.Name == name)
        {
            return german;
        }

        return repository.FindByName(name) ?? profiles.Create(QualityDefaults.LanguageProfile(language));
    }

    /// <summary>What the default profile asks for: any, original, german, native (another language with an ISO code), or other.</summary>
    private static string ReleaseLanguage(QualityProfile? profile) => profile?.LanguageId switch
    {
        var l when l == Language.Any.Id => LanguageAny,
        var l when l == Language.Original.Id => LanguageOriginal,
        var l when l == Language.German.Id => LanguageGerman,
        { } l when Language.FindById(l).IsoCode is not null => LanguageNative,
        _ => "other",
    };

    /// <summary>
    /// The language to offer next to any and original: the first language of the browser (Accept-Language, by
    /// preference) that StreamZH knows and that is not English, otherwise the TMDB metadata language if it is not
    /// English. English is left out: it is already the original language of most titles, so "Original language" covers
    /// it. Null if nothing fits.
    /// </summary>
    public static Language? SuggestedLanguage(HttpRequest? request, string? metadataLanguage)
    {
        var browser = request?.GetTypedHeaders().AcceptLanguage
            .Where(h => h.Quality is not 0.0)
            .OrderByDescending(h => h.Quality ?? 1)
            .Select(h => h.Value.Value)
            ?? [];
        return browser.Append(metadataLanguage)
            .Select(Language.FromLanguageTag)
            .FirstOrDefault(l => LanguageRules.IsReal(l) && l != Language.English && l.IsoCode is not null);
    }

    public static SetupGuideResource Build(IServiceProvider services, bool isOwner, HttpRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var paths = services.GetRequiredService<AppPaths>();
        var config = services.GetRequiredService<ConfigRepository>();

        // Storage: a folder of the owner's besides the default download folder in the data folder.
        var locations = services.GetRequiredService<StorageLocationService>().Locations
            .Where(l => !DataFolderGuard.IsSameOrInside(PathIdentity.Normalize(l), PathIdentity.Normalize(paths.DataDirectory)))
            .ToList();

        var roots = services.GetRequiredService<RootFolderRepository>().All();
        var indexers = services.GetRequiredService<IndexerRepository>().All()
            .Count(i => i.EnableRss || i.EnableAutomaticSearch || i.EnableInteractiveSearch);
        var usenet = services.GetRequiredService<UsenetServerRepository>().All().Count(s => s.Enabled);
        var clients = services.GetRequiredService<DownloadClientRepository>().All().Where(c => c.Enable).ToList();
        var builtInTorrent = clients.Any(c => c.Implementation == DownloadClientDefinition.BuiltInTorrent);
        var torrentClients = clients.Count(c => c.Implementation != DownloadClientDefinition.BuiltInTorrent);
        var migrated = services.GetRequiredService<MigrationRunRepository>().All()
            .Any(r => !r.DryRun && string.Equals(r.Status, "done", StringComparison.OrdinalIgnoreCase));
        var users = services.GetRequiredService<UserRepository>().Count();
        var invites = services.GetRequiredService<InviteRepository>().All().Count;
        var connections = services.GetRequiredService<ConnectionRepository>().All().Count(c => c.Enable);

        // Release language: chosen here, or an installation that already chose (another default profile, or movies and
        // series that use it).
        var defaultProfile = services.GetRequiredService<QualityProfileRepository>().Find(services.GetRequiredService<QualityProfileService>().DefaultProfileId());
        var languageChosen = config.Get(ConfigKeys.SetupReleaseLanguage) is { Length: > 0 }
            || defaultProfile?.Id != QualityDefaults.DefaultProfileId
            || services.GetRequiredService<QualityProfileRepository>().UsageCount(defaultProfile.Id) > 0;

        var defaultLanguage = defaultProfile is null ? null : Language.FindById(defaultProfile.LanguageId) is var own && LanguageRules.IsReal(own) ? own : null;
        var suggested = SuggestedLanguage(request, config.Get(ConfigKeys.TmdbLanguage));

        var movieRoots = roots.Count(r => r.MediaType == MediaType.Movie);
        var seriesRoots = roots.Count(r => r.MediaType == MediaType.Series);
        var steps = new List<SetupGuideStep>
        {
            new("storage", locations.Count > 0, locations.Count),
            new("rootFolders", movieRoots + seriesRoots > 0, movieRoots + seriesRoots),
            new("tmdb", services.GetRequiredService<ITmdbClient>().IsConfigured, 0),
            new("language", languageChosen, 0),
            new("indexers", indexers > 0, indexers),
            new("downloads", usenet + clients.Count > 0, usenet + clients.Count),
        };
        var done = steps.Count(s => s.Done);

        return new SetupGuideResource(
            config.Get(ConfigKeys.SetupGuideDismissed) == "true",
            done == steps.Count,
            done,
            steps.Count,
            isOwner,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            steps,
            new SetupGuideDetails(
                locations,
                movieRoots,
                seriesRoots,
                usenet,
                torrentClients,
                builtInTorrent,
                migrated,
                users,
                invites,
                connections,
                services.GetRequiredService<RequestSettings>().Values.Enabled,
                config.Get(ConfigKeys.DiscordEnabled) == "true",
                ReleaseLanguage(defaultProfile),
                defaultLanguage?.IsoCode,
                defaultLanguage?.Name,
                defaultProfile?.Name,
                suggested is null ? null : new SetupLanguageOption(suggested.IsoCode!, suggested.Name)));
    }
}

public sealed record SetupGuideUpdate(bool? Dismissed);

/// <param name="Language">
/// any (any language), original (the original language), german (German, dual language preferred) or native (the
/// language in <paramref name="Code"/>, dual language preferred).
/// </param>
/// <param name="Code">For native: the ISO 639-1 code of the language, e.g. tr; "de" is the same as german.</param>
public sealed record SetupLanguageUpdate(string? Language, string? Code = null);

/// <param name="Code">ISO 639-1 code, e.g. tr.</param>
/// <param name="Name">English name, e.g. Turkish.</param>
public sealed record SetupLanguageOption(string Code, string Name);

/// <param name="Dismissed">The owner or an admin hid the checklist ("Hide the guide").</param>
/// <param name="Complete">Every required step is done.</param>
/// <param name="IsOwner">Storage locations are owner-only: an admin sees that step as "ask the owner".</param>
/// <param name="IsWindows">For the examples in the texts (D:\Media or /data/media).</param>
/// <param name="IsDocker">In a container the folders are the container paths of the volumes.</param>
/// <param name="Steps">storage, rootFolders, tmdb, language, indexers, downloads, in this order.</param>
public sealed record SetupGuideResource(
    bool Dismissed,
    bool Complete,
    int Done,
    int Total,
    bool IsOwner,
    bool IsWindows,
    bool IsDocker,
    IReadOnlyList<SetupGuideStep> Steps,
    SetupGuideDetails Details);

/// <param name="Count">How many things this step has (locations, root folders, indexers, download clients); 0 for TMDB.</param>
public sealed record SetupGuideStep(string Key, bool Done, int Count);

/// <param name="StorageLocations">The owner's storage locations besides the default download folder.</param>
/// <param name="Users">Accounts, the owner included.</param>
/// <param name="Connections">Enabled Connect entries (Plex, Jellyfin, Emby, notifications).</param>
/// <param name="ReleaseLanguage">
/// any, original, german, native (a default profile with another language that has an ISO code, see
/// <paramref name="ReleaseLanguageCode"/>), or other.
/// </param>
/// <param name="ReleaseLanguageCode">ISO 639-1 code of the default profile's language ("de", "tr"); null for any and original.</param>
/// <param name="ReleaseLanguageName">Name of the default profile's language ("German", "Turkish"); null for any and original.</param>
/// <param name="DefaultProfile">Name of the default quality profile.</param>
/// <param name="SuggestedLanguage">
/// The language "Get started" offers besides any and original: the browser's (Accept-Language), else the TMDB
/// metadata language; never English. Null if none fits.
/// </param>
public sealed record SetupGuideDetails(
    IReadOnlyList<string> StorageLocations,
    int MovieRootFolders,
    int SeriesRootFolders,
    int UsenetServers,
    int TorrentClients,
    bool BuiltInTorrent,
    bool Migrated,
    int Users,
    int Invites,
    int Connections,
    bool RequestsEnabled,
    bool DiscordEnabled,
    string ReleaseLanguage,
    string? ReleaseLanguageCode,
    string? ReleaseLanguageName,
    string? DefaultProfile,
    SetupLanguageOption? SuggestedLanguage);
