using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Shared;
using Shared.Attributes;
using Shared.Models.Base;
using Shared.Services;
using SynoDLNA.Models;
using SynoDLNA.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace SynoDLNA;

public class SynoDLNAController : BaseController
{
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext<SynoDLNAController>();

    #region syno_dlna.js
    [HttpGet, AllowAnonymous]
    [Staticache(cacheMinutes: 10, always: true, setHeadersNoCache: true)]
    [Route("syno_dlna.js")]
    [Route("syno_dlna/js/{token}")]
    public ActionResult Plugin(string token)
    {
        // Плагин раздаётся анонимно и одинаков для всех - он не содержит токенов доступа
        // (пользователь вводит свой токен в настройках клиента), поэтому Staticache безопасен.
        var plugin = FileCache.ReadAllText($"{ModInit.modpath}/plugin.js", "syno_dlna.js", saveCache: false)
            .Replace("{localhost}", host)
            .Replace("{token}", HttpUtility.UrlEncode(token));

        return ContentTo(plugin, "application/javascript; charset=utf-8");
    }
    #endregion

    #region Authorization
    /// <summary>
    /// Возвращает null, если доступ разрешён (userName получает имя записи из users, либо "local"
    /// для доступа из LAN без токена). Иначе возвращает готовый ActionResult с ошибкой, который
    /// нужно вернуть из экшена как есть.
    /// </summary>
    ActionResult Authorize(out string userName)
    {
        userName = null;

        if (!ModInit.conf.enable)
            return new JsonResult(new { error = "disabled" }) { StatusCode = 403 };

        if (requestInfo.IsLocalIp && ModInit.conf.allowLocalWithoutToken)
        {
            userName = "local";
            return null;
        }

        string token = ExtractToken();

        if (!string.IsNullOrEmpty(token))
        {
            byte[] presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));

            foreach (var user in ModInit.conf.users ?? new List<SynoUser>())
            {
                if (!user.enable || string.IsNullOrEmpty(user.token))
                    continue;

                byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(user.token));

                // Сравнение обязано быть constant-time и не зависеть от длины строк:
                // сравниваем не сами токены, а их SHA256 фиксированной длины через FixedTimeEquals.
                if (CryptographicOperations.FixedTimeEquals(presented, expected))
                {
                    userName = string.IsNullOrEmpty(user.name) ? "(unnamed)" : user.name;
                    return null;
                }
            }
        }

        Log.Warning("SynoDLNA unauthorized request from {IP}", requestInfo.IP);
        return new JsonResult(new { error = "unauthorized" }) { StatusCode = 401 };
    }

    string ExtractToken()
    {
        if (Request.Headers.TryGetValue("X-Syno-Token", out var headerValue) && !string.IsNullOrEmpty(headerValue))
            return headerValue.ToString();

        if (Request.Query.TryGetValue("syno_token", out var queryValue) && !string.IsNullOrEmpty(queryValue))
            return queryValue.ToString();

        return null;
    }
    #endregion

    #region ping
    [HttpGet]
    [Route("syno_dlna/ping")]
    public ActionResult Ping()
    {
        if (Authorize(out _) is ActionResult denied)
            return denied;

        return Json(new { ok = true });
    }
    #endregion

    #region browse
    [HttpGet]
    [Route("syno_dlna/browse")]
    async public Task<ActionResult> Browse(string id, string flag)
    {
        if (Authorize(out _) is ActionResult denied)
            return denied;

        if (ModInit.baseUri == null)
            return new JsonResult(new { error = "not_configured" }) { StatusCode = 503 };

        string objectId = string.IsNullOrEmpty(id) ? ModInit.conf.rootObjectId : id;
        string browseFlag = string.IsNullOrEmpty(flag) ? "BrowseDirectChildren" : flag;

        string cacheKey = $"syno_dlna:browse:{objectId}:{browseFlag}";

        // В кеше лежат только распарсенные элементы с сырыми (NAS) url. Проксирование
        // выполняется ниже на каждый запрос отдельно - закешированная /proxy/ ссылка может быть
        // привязана к IP другого клиента и иметь ограниченный срок жизни.
        if (!memoryCache.TryGetValue(cacheKey, out SynoBrowseResult raw))
        {
            List<SynoItem> items;

            try
            {
                items = await UpnpClient.BrowseAsync(objectId, browseFlag, ModInit.conf.timeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CatchId={CatchId}", "id_syno_dlna_browse");
                return new JsonResult(new { error = "upstream" }) { StatusCode = 502 };
            }

            raw = SubtitleResolver.BuildBrowseResult(items);
            memoryCache.Set(cacheKey, raw, DateTime.Now.AddMinutes(Math.Max(1, ModInit.conf.cacheMinutes)));
        }

        var response = new SynoBrowseResult
        {
            folders = raw.folders.Select(i => ApplyPublicUrl(i, dropIfUrlInvalid: false)).Where(i => i != null).ToList(),
            files = raw.files.Select(i => ApplyPublicUrl(i, dropIfUrlInvalid: true)).Where(i => i != null).ToList()
        };

        return Json(response);
    }
    #endregion

    #region subtitles
    [HttpGet]
    [Route("syno_dlna/subtitles")]
    async public Task<ActionResult> Subtitles(string id)
    {
        if (Authorize(out _) is ActionResult denied)
            return denied;

        if (string.IsNullOrEmpty(id))
            return Json(new { subtitles = Array.Empty<object>() });

        if (ModInit.baseUri == null)
            return new JsonResult(new { error = "not_configured" }) { StatusCode = 503 };

        string cacheKey = $"syno_dlna:meta:{id}";

        if (!memoryCache.TryGetValue(cacheKey, out SynoItem meta))
        {
            List<SynoItem> items;

            try
            {
                items = await UpnpClient.BrowseAsync(id, "BrowseMetadata", ModInit.conf.timeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CatchId={CatchId}", "id_syno_dlna_meta");
                return new JsonResult(new { error = "upstream" }) { StatusCode = 502 };
            }

            meta = items?.FirstOrDefault();

            if (meta != null)
                memoryCache.Set(cacheKey, meta, DateTime.Now.AddMinutes(Math.Max(1, ModInit.conf.cacheMinutes)));
        }

        // часть серверов отдает информацию о субтитрах только в ответе на BrowseMetadata
        var subtitles = new List<SynoSubtitle>(meta?.subtitles ?? new List<SynoSubtitle>());

        // если DIDL ничего не дал - угадываем субтитры по ссылке видео (только если хост совпадает
        // с настроенным NAS, проверка внутри ProbeSubtitlesAsync)
        if (subtitles.Count == 0 && ModInit.conf.subtitles && !string.IsNullOrEmpty(meta?.url))
        {
            try
            {
                subtitles = await SubtitleResolver.ProbeSubtitlesAsync(meta.url, 5).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CatchId={CatchId}", "id_syno_dlna_probe");
            }
        }

        var mapped = subtitles
            .Select(s => new SynoSubtitle { label = s.label, url = PublicUrl(s.url, allowDirect: false) })
            .Where(s => s.url != null)
            .ToList();

        return Json(new { subtitles = mapped });
    }
    #endregion

    #region PublicUrl / ApplyPublicUrl
    /// <summary>
    /// Единственное место, где url с NAS превращается в ссылку, которую можно отдать клиенту.
    /// Клиент никогда не передаёт серверу ни хост, ни url, ни "путь до прокси" - всё резолвится
    /// здесь из conf.url (через ModInit.baseUri) и объекта, полученного от NAS.
    /// </summary>
    /// <param name="allowDirect">
    /// Разрешает отдать LAN-клиенту прямую ссылку на NAS (при directLocalIp). Допустимо ТОЛЬКО
    /// для видео и картинок: их грузят теги video/img, которым CORS не нужен. Для субтитров
    /// обязан быть false - Лампа качает их через XHR, а DLNA-сервер Synology заголовков CORS
    /// не отдаёт, поэтому прямая ссылка будет заблокирована браузером. Именно ради этого
    /// случая раньше существовал внешний CORS-прокси; через /proxy проблема снимается.
    /// </param>
    string PublicUrl(string raw, bool allowDirect)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return null;

        if (ModInit.baseUri == null || !string.Equals(parsed.Host, ModInit.baseUri.Host, StringComparison.OrdinalIgnoreCase))
            return null;

        if (allowDirect && requestInfo.IsLocalIp && ModInit.conf.directLocalIp)
            return raw;

        return HostStreamProxy(new BaseSettings { streamproxy = true, plugin = "syno_dlna" }, raw);
    }

    SynoItem ApplyPublicUrl(SynoItem source, bool dropIfUrlInvalid)
    {
        string publicUrl = PublicUrl(source.url, allowDirect: true);

        if (publicUrl == null && dropIfUrlInvalid)
            return null;

        var clone = new SynoItem
        {
            id = source.id,
            parentId = source.parentId,
            title = source.title,
            type = source.type,
            size = source.size,
            duration = source.duration,
            resolution = source.resolution,
            protocolInfo = source.protocolInfo,
            url = publicUrl,
            subtitles = new List<SynoSubtitle>()
        };

        foreach (var sub in source.subtitles ?? new List<SynoSubtitle>())
        {
            string subUrl = PublicUrl(sub.url, allowDirect: false);
            if (subUrl == null)
                continue;

            clone.subtitles.Add(new SynoSubtitle { label = sub.label, url = subUrl });
        }

        return clone;
    }
    #endregion
}
