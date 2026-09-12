using Shared;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System;
using System.Collections.Generic;

namespace SynoDLNA;

public class ModInit : IModuleLoaded
{
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ModInit>();

    public static string modpath;
    public static ModuleConf conf;

    /// <summary>
    /// Валидированный conf.url. Null, если url пуст или не парсится в абсолютный http/https URI.
    /// Контроллер сверяет хост медиа-ссылок именно с этим значением.
    /// </summary>
    public static Uri baseUri;

    public void Loaded(InitspaceModel baseconf)
    {
        modpath = baseconf.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;

        foreach (var m in conf.limit_map)
            CoreInit.conf.WAF.limit_map.Insert(0, m);
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("SynoDLNA", new ModuleConf()
        {
            enable = false,
            rootObjectId = "0",
            subtitles = true,
            directLocalIp = true,
            allowLocalWithoutToken = true,
            timeoutSeconds = 10,
            cacheMinutes = 5,
            users = new List<SynoUser>(),
            limit_map = new List<WafLimitRootMap>()
            {
                new("^/syno_dlna/", new WafLimitMap { limit = 20, second = 1 })
            }
        });

        baseUri = ParseBaseUri(conf.url);
        ValidateConf(conf, baseUri);
    }

    static Uri ParseBaseUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return null;

        return parsed;
    }

    static void ValidateConf(ModuleConf conf, Uri baseUri)
    {
        if (!conf.enable)
            return;

        if (baseUri == null)
        {
            Log.Warning("SynoDLNA enabled but 'url' is empty or is not a valid absolute http/https URI");
        }

        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        bool hasValidUser = false;

        foreach (var user in conf.users ?? new List<SynoUser>())
        {
            if (!user.enable || string.IsNullOrEmpty(user.token))
                continue;

            hasValidUser = true;

            if (user.token.Length < 16)
                Log.Warning("SynoDLNA user {Name} has a token shorter than 16 characters", user.name ?? "(unnamed)");

            if (!seenTokens.Add(user.token))
                Log.Warning("SynoDLNA has a duplicate token configured for user {Name}", user.name ?? "(unnamed)");
        }

        if (!hasValidUser && !conf.allowLocalWithoutToken)
            Log.Warning("SynoDLNA is enabled but has no enabled user with a non-empty token, and allowLocalWithoutToken is false - the module is unreachable by anyone");
    }
}
