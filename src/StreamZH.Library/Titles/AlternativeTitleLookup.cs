// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Library.Titles;

/// <summary>
/// The alternative titles of the library grouped by item, read once for a run that matches many release or file names
/// (RSS sync, renamer preview) instead of one query per item.
/// </summary>
public sealed class AlternativeTitleLookup
{
    public static readonly AlternativeTitleLookup Empty = new([]);

    private readonly Dictionary<(MediaType, int), List<AlternativeTitle>> _byItem = [];

    public AlternativeTitleLookup(IEnumerable<AlternativeTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        foreach (var title in titles)
        {
            var key = (title.MediaType, title.ItemId);
            if (!_byItem.TryGetValue(key, out var list))
            {
                list = [];
                _byItem[key] = list;
            }

            list.Add(title);
        }
    }

    public IReadOnlyList<AlternativeTitle> For(MediaType mediaType, int itemId) =>
        _byItem.TryGetValue((mediaType, itemId), out var list) ? list : [];
}
