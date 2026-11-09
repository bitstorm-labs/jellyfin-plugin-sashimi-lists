using Jellyfin.Plugin.SashimiLists.Core.Watchlist;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.SashimiLists.Watchlist;

/// <summary>The Core plex.tv client over Jellyfin's HTTP client factory, identified by this server's id.</summary>
public sealed class PlexTvClient : PlexTvApi
{
    public PlexTvClient(IHttpClientFactory httpClientFactory, IServerApplicationHost applicationHost)
        : base(() => httpClientFactory.CreateClient(NamedClient.Default), "sashimi-lists-" + applicationHost.SystemId)
    {
    }
}
