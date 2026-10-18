// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Host.Auth;
using StreamZH.Library.Renamer;

namespace StreamZH.Host.Api;

/// <summary>
/// Tools → File renamer: scan a folder, preview the rules, rename as a job, history with undo, presets and a regex tester.
/// Admins only: it renames files on the StreamZH machine.
/// </summary>
public static class RenamerEndpoints
{
    public const int MaxRegexInput = 1000;
    public const int MaxRegexMatches = 50;

    public static IEndpointRouteBuilder MapRenamerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/renamer").WithTags("File renamer").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapPost("/scan", async (HttpRequest http, RenamerService renamer) =>
        {
            var body = await ReadAsync<ScanRequest>(http);
            return body is null ? Invalid() : Results.Ok(renamer.Scan(body.Source ?? new RenamerSource(), body.Filter ?? new RenamerFilter()));
        });

        group.MapPost("/preview", async (HttpRequest http, RenamerService renamer, CancellationToken ct) =>
        {
            var body = await ReadAsync<RenamerRequest>(http);
            return body is null ? Invalid() : Results.Ok(await renamer.PreviewAsync(body, ct));
        });

        group.MapPost("/apply", async (HttpRequest http, RenamerService renamer, CancellationToken ct) =>
        {
            var body = await ReadAsync<RenamerRequest>(http);
            return body is null ? Invalid() : Results.Accepted(value: new RunQueued(await renamer.QueueApplyAsync(body, ct)));
        });

        group.MapGet("/history", (int? limit, RenamerService renamer) => renamer.History(limit ?? 50));
        group.MapGet("/history/{id:int}", (int id, RenamerService renamer) => renamer.Run(id));
        group.MapPost("/undo/{id:int}", (int id, RenamerService renamer) => Results.Accepted(value: new RunQueued(renamer.QueueUndo(id))));

        group.MapGet("/presets", (RenamerService renamer) => renamer.Presets());
        group.MapPost("/presets", async (HttpRequest http, RenamerService renamer) =>
        {
            var body = await ReadAsync<PresetRequest>(http);
            return body is null ? Invalid() : Results.Ok(renamer.SavePreset(null, body.Name, body.Content ?? new RenamerPresetContent()));
        });
        group.MapPut("/presets/{id:int}", async (int id, HttpRequest http, RenamerService renamer) =>
        {
            var body = await ReadAsync<PresetRequest>(http);
            return body is null ? Invalid() : Results.Ok(renamer.SavePreset(id, body.Name, body.Content ?? new RenamerPresetContent()));
        });
        group.MapDelete("/presets/{id:int}", (int id, RenamerService renamer) =>
        {
            renamer.DeletePreset(id);
            return Results.NoContent();
        });

        group.MapPost("/regex-test", (RegexTestRequest request) => Test(request));
        return app;
    }

    /// <summary>Matches and groups of a pattern on a sample name (the first file), with the replacement result.</summary>
    public static RegexTestResult Test(RegexTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = (request.Input ?? string.Empty)[..Math.Min(MaxRegexInput, request.Input?.Length ?? 0)];
        var (regex, error) = SafeRegex.TryCreate(request.Pattern ?? string.Empty, !request.CaseSensitive);
        if (regex is null)
        {
            return new RegexTestResult(error, [], null);
        }

        try
        {
            var matches = new List<RegexTestMatch>();
            foreach (Match match in regex.Matches(input))
            {
                if (matches.Count >= MaxRegexMatches)
                {
                    break;
                }

                var groups = new List<RegexTestGroup>();
                foreach (var number in regex.GetGroupNumbers().Where(n => n > 0))
                {
                    var g = match.Groups[number];
                    var name = regex.GroupNameFromNumber(number);
                    groups.Add(new RegexTestGroup(number, name, g.Success ? g.Value : null, g.Success ? g.Index : null));
                }

                matches.Add(new RegexTestMatch(match.Index, match.Length, match.Value, groups));
            }

            var result = request.Replacement is null ? null : regex.Replace(input, request.Replacement);
            return new RegexTestResult(null, matches, result);
        }
        catch (RegexMatchTimeoutException)
        {
            return new RegexTestResult("The regular expression took too long on this name. Simplify it.", [], null);
        }
    }

    /// <summary>Bodies with rules: the "type" of a rule may come anywhere in the object.</summary>
    private static async Task<T?> ReadAsync<T>(HttpRequest http)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(http.Body, RenamerJson.Options, http.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static IResult Invalid() =>
        Results.Problem("The request could not be read. Check the rules (each needs a known \"type\").", statusCode: StatusCodes.Status400BadRequest);
}

public sealed record ScanRequest(RenamerSource? Source, RenamerFilter? Filter);

public sealed record PresetRequest(string? Name, RenamerPresetContent? Content);

public sealed record RunQueued(int RunId);

public sealed record RegexTestRequest(string? Pattern, bool CaseSensitive, string? Input, string? Replacement);

/// <param name="Number">$1, $2 … (.NET numbers the unnamed groups first, then the named ones).</param>
/// <param name="Name">The group name, or its number for unnamed groups.</param>
/// <param name="Index">Null when the group did not take part in the match.</param>
public sealed record RegexTestGroup(int Number, string Name, string? Value, int? Index);

public sealed record RegexTestMatch(int Index, int Length, string Value, IReadOnlyList<RegexTestGroup> Groups);

public sealed record RegexTestResult(string? Error, IReadOnlyList<RegexTestMatch> Matches, string? Result);
