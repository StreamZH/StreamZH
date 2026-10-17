// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;

namespace StreamZH.Host.General;

/// <summary>
/// The web app's index.html with &lt;base href="{URL base}/"&gt; added, so the relative asset paths of the build and the
/// app's routes and API calls work below a URL base. Re-read when the file changes (new frontend build).
/// </summary>
public sealed class IndexPage(IWebHostEnvironment environment, RunningHost running)
{
    private readonly Lock _lock = new();
    private (DateTimeOffset Modified, string Html)? _cached;

    public string? Html()
    {
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists || file.IsDirectory)
        {
            return null;
        }

        lock (_lock)
        {
            if (_cached is { } cached && cached.Modified == file.LastModified)
            {
                return cached.Html;
            }

            using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            var html = Inject(reader.ReadToEnd(), running.Values.UrlBase);
            _cached = (file.LastModified, html);
            return html;
        }
    }

    /// <summary>Adds the base element right after &lt;head&gt; (or replaces an existing one).</summary>
    public static string Inject(string html, string urlBase)
    {
        ArgumentNullException.ThrowIfNull(html);
        var tag = $"<base href=\"{WebUtility.HtmlEncode(urlBase)}/\" />";
        var existing = html.IndexOf("<base ", StringComparison.OrdinalIgnoreCase);
        if (existing >= 0)
        {
            var end = html.IndexOf('>', existing);
            return html[..existing] + tag + html[(end + 1)..];
        }

        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? tag + html : html.Insert(head + "<head>".Length, "\n    " + tag);
    }

    public static IResult Serve(HttpContext context, IndexPage page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page);
        if (page.Html() is not { } html)
        {
            return Results.NotFound("The web app is not built. Run \"npm run build\" in the frontend folder.");
        }

        // Always the newest index.html, it names the hashed asset files of the current build.
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
