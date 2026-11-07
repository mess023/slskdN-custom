// <copyright file="DerivedOptionsMonitor.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>

namespace slskd.Common.Configuration;

using System;
using Microsoft.Extensions.Options;

/// <summary>
///     Exposes a value derived from another <see cref="IOptionsMonitor{TOptions}"/> as its own monitor,
///     so nested options (e.g. a sub-section of <c>slskd.Options</c>) stay live-reloadable.
/// </summary>
/// <typeparam name="TSource">The source options type.</typeparam>
/// <typeparam name="TDerived">The derived options type.</typeparam>
public sealed class DerivedOptionsMonitor<TSource, TDerived> : IOptionsMonitor<TDerived>
    where TSource : class
    where TDerived : class
{
    private readonly IOptionsMonitor<TSource> source;
    private readonly Func<TSource, TDerived> map;
    private readonly object sync = new();
    private TSource? lastSource;
    private TDerived? lastDerived;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DerivedOptionsMonitor{TSource, TDerived}"/> class.
    /// </summary>
    /// <param name="source">The monitor to derive from.</param>
    /// <param name="map">Maps a source value to the derived value.</param>
    public DerivedOptionsMonitor(IOptionsMonitor<TSource> source, Func<TSource, TDerived> map)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.map = map ?? throw new ArgumentNullException(nameof(map));
    }

    /// <inheritdoc />
    public TDerived CurrentValue => Get(Options.DefaultName);

    /// <inheritdoc />
    public TDerived Get(string? name)
    {
        var current = source.Get(name);

        // Re-map only when the source instance changed (OptionsMonitor hands out the same instance until reload).
        lock (sync)
        {
            if (!ReferenceEquals(current, lastSource) || lastDerived is null)
            {
                lastDerived = map(current);
                lastSource = current;
            }

            return lastDerived;
        }
    }

    /// <inheritdoc />
    public IDisposable? OnChange(Action<TDerived, string?> listener)
        => source.OnChange((value, name) => listener(map(value), name));
}
