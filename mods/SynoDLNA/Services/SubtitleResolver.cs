using Shared.Services;
using SynoDLNA.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SynoDLNA.Services;

/// <summary>
/// Порт клиентской логики субтитров из test_plugins/synology_dlna.js (константы SUB_*,
/// разбор имён файлов, привязка "осиротевших" файлов субтитров к видео по имени,
/// угадывание внешних субтитров по URL видео).
/// </summary>
public static class SubtitleResolver
{
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(SubtitleResolver));

    static readonly string[] SubAll =
    {
        "srt", "vtt", "webvtt", "ass", "ssa", "smi", "sami", "sub", "idx", "sup"
    };

    static readonly string[] SubMimes =
    {
        "text/srt", "application/x-srt", "application/x-subrip", "text/vtt", "text/webvtt",
        "text/smi", "application/smil", "text/ssa", "text/x-ssa", "text/ass", "text/sub"
    };

    static readonly Dictionary<string, string> SubLangs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "Русские", ["rus"] = "Русские", ["russian"] = "Русские", ["russkie"] = "Русские",
        ["en"] = "English", ["eng"] = "English", ["english"] = "English",
        ["uk"] = "Українські", ["ukr"] = "Українські", ["ukrainian"] = "Українські",
        ["be"] = "Беларускія", ["bel"] = "Беларускія",
        ["de"] = "Deutsch", ["ger"] = "Deutsch", ["deu"] = "Deutsch",
        ["fr"] = "Français", ["fra"] = "Français", ["fre"] = "Français",
        ["es"] = "Español", ["spa"] = "Español",
        ["it"] = "Italiano", ["ita"] = "Italiano",
        ["pl"] = "Polski", ["pol"] = "Polski",
        ["zh"] = "中文", ["chi"] = "中文", ["zho"] = "中文",
        ["ja"] = "日本語", ["jpn"] = "日本語"
    };

    static readonly Regex TokenSplitRegex = new(@"[^0-9a-zа-яё]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // srt, vtt или ass - по первым строкам файла
    static readonly Regex SubContentRegex = new(
        @"^\s*(WEBVTT|\[Script Info\]|\d+\s*[\r\n]+\s*\d{1,2}:\d{2}:\d{2}|\d{1,2}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.Compiled);

    #region url/filename helpers

    public static string UrlFileName(string url)
    {
        string clean = (url ?? string.Empty).Split('?')[0].Split('#')[0];
        var parts = clean.Split('/');
        string name = parts.Length > 0 ? parts[^1] : string.Empty;

        try
        {
            name = Uri.UnescapeDataString(name);
        }
        catch
        {
            // оставляем как есть, если строка не является корректной percent-encoded последовательностью
        }

        return name;
    }

    public static string FileExt(string name)
    {
        int dot = (name ?? string.Empty).LastIndexOf('.');
        return dot == -1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    public static string FileBase(string name)
    {
        int dot = (name ?? string.Empty).LastIndexOf('.');
        return (dot == -1 ? (name ?? string.Empty) : name[..dot]).ToLowerInvariant();
    }

    public static bool IsSubUrl(string url)
        => Array.IndexOf(SubAll, FileExt(UrlFileName(url))) != -1;

    public static bool IsSubMime(string protocolInfo)
    {
        if (string.IsNullOrEmpty(protocolInfo))
            return false;

        var parts = protocolInfo.Split(':');
        if (parts.Length < 3 || string.IsNullOrEmpty(parts[2]))
            return false;

        string mime = parts[2].ToLowerInvariant();
        return SubMimes.Any(m => mime.Contains(m));
    }

    public static string MimeFamily(string protocolInfo)
    {
        if (string.IsNullOrEmpty(protocolInfo))
            return string.Empty;

        var parts = protocolInfo.Split(':');
        if (parts.Length < 3 || string.IsNullOrEmpty(parts[2]))
            return string.Empty;

        var slash = parts[2].Split('/');
        return slash.Length > 0 ? slash[0].ToLowerInvariant() : string.Empty;
    }

    // "srt" -> "SRT", "webvtt" -> "VTT"
    static string SubType(string url)
    {
        string ext = FileExt(UrlFileName(url));
        if (string.IsNullOrEmpty(ext))
            return string.Empty;

        if (ext == "webvtt")
            ext = "vtt";

        return ext.ToUpperInvariant();
    }

    // "Movie.2020.rus.forced.srt" при видео "Movie.2020.mkv" -> "Русские forced"
    static string SubLabel(string url, int index, string videoBase)
    {
        string name = UrlFileName(url);
        string bas = FileBase(name);

        if (!string.IsNullOrEmpty(videoBase) && bas.StartsWith(videoBase, StringComparison.Ordinal))
            bas = bas[videoBase.Length..];

        var tokens = TokenSplitRegex.Split(bas).Where(t => t.Length > 0).ToList();

        string lang = string.Empty;
        var extra = new List<string>();

        foreach (var token in tokens)
        {
            string low = token.ToLowerInvariant();

            if (string.IsNullOrEmpty(lang) && SubLangs.TryGetValue(low, out string mapped))
            {
                lang = mapped;
                continue;
            }

            if (low is "forced" or "sdh" or "full" or "hi")
                extra.Add(low);
        }

        var labelParts = new List<string>();
        if (!string.IsNullOrEmpty(lang))
            labelParts.Add(lang);
        labelParts.AddRange(extra);

        string label = string.Join(" ", labelParts);

        if (string.IsNullOrEmpty(label))
            label = tokens.Count > 0 ? string.Join(" ", tokens) : string.Empty;

        if (string.IsNullOrEmpty(label))
            label = !string.IsNullOrEmpty(name) ? name : "Sub";

        if (label.Length > 40)
            label = label[..40];

        string type = SubType(url);
        if (!string.IsNullOrEmpty(type))
            label += $" [{type}]";

        return $"{label} #{index + 1}";
    }

    public static void AddSubtitle(List<SynoSubtitle> list, string url, string videoBase)
    {
        if (string.IsNullOrEmpty(url))
            return;

        // DIDL приходит уже раскодированным, но субтитры Лампа тянет через XHR -
        // пробелы в пути такой запрос ломают, в отличие от тега video
        url = url.Trim().Replace(" ", "%20");

        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.Ordinal))
            return;

        if (list.Any(s => s.url == url))
            return;

        list.Add(new SynoSubtitle
        {
            url = url,
            label = SubLabel(url, list.Count, videoBase)
        });
    }

    #endregion

    #region folder/file split + orphan subtitle attachment (test_plugins/synology_dlna.js:171-207)

    /// <summary>
    /// Порт разбиения плоского списка Browse-элементов на папки/файлы и привязки
    /// "осиротевших" файлов субтитров к видео по имени (test_plugins/synology_dlna.js:171-207).
    /// Результат по-прежнему содержит сырые (NAS) url - подстановка публичной ссылки происходит
    /// в контроллере отдельно на каждый запрос.
    /// </summary>
    public static SynoBrowseResult BuildBrowseResult(List<SynoItem> items)
    {
        items ??= new List<SynoItem>();

        var folders = items.Where(i => i.type == "object.container.storageFolder").ToList();

        var files = items.Where(i =>
            i.type == "object.item.videoItem" ||
            i.type == "object.item.audioItem.musicTrack" ||
            i.type == "object.item.imageItem.photo").ToList();

        // некоторые сервера показывают субтитры отдельными файлами - привязываем их к видео по имени
        var fileSet = new HashSet<SynoItem>(files);
        var subfiles = items.Where(i =>
            !fileSet.Contains(i) &&
            !string.IsNullOrEmpty(i.url) &&
            (IsSubUrl(i.url) || IsSubUrl(i.title))).ToList();

        AttachOrphanSubtitles(files, subfiles);

        return new SynoBrowseResult { folders = folders, files = files };
    }

    static void AttachOrphanSubtitles(List<SynoItem> files, List<SynoItem> subfiles)
    {
        if (subfiles.Count == 0)
            return;

        foreach (var file in files)
        {
            if (file.type != "object.item.videoItem")
                continue;

            // имя файла в ссылке может быть идентификатором (Synology: /v/NDLNA/993.mkv),
            // поэтому сверяем и с dc:title
            var bases = new List<string>();
            string b1 = FileBase(UrlFileName(file.url));
            string b2 = FileBase(file.title ?? string.Empty);

            if (!string.IsNullOrEmpty(b1))
                bases.Add(b1);
            if (!string.IsNullOrEmpty(b2) && b2 != b1)
                bases.Add(b2);

            if (bases.Count == 0)
                continue;

            file.subtitles ??= new List<SynoSubtitle>();

            foreach (var sub in subfiles)
            {
                string subName = UrlFileName(sub.url);
                if (string.IsNullOrEmpty(subName))
                    subName = sub.title ?? string.Empty;

                string subBase = FileBase(subName);

                string matched = bases.FirstOrDefault(videoBase =>
                    subBase == videoBase ||
                    subBase.StartsWith(videoBase + ".", StringComparison.Ordinal) ||
                    subBase.StartsWith(videoBase + "_", StringComparison.Ordinal));

                if (matched == null)
                    continue;

                AddSubtitle(file.subtitles, sub.url, matched);
            }
        }
    }

    #endregion

    #region probing (test_plugins/synology_dlna.js:308-347)

    /// <summary>
    /// Synology не публикует субтитры в DIDL, но отдаёт их по ссылке видео со сменой расширения:
    /// /v/NDLNA/993.mkv -> /v/NDLNA/993.srt. Прежде чем выполнить любой GET, проверяем, что хост
    /// кандидата совпадает с хостом ModInit.baseUri - url приходит от NAS и не является доверенным
    /// настолько, чтобы сервер ходил по нему куда угодно.
    /// </summary>
    public static async Task<List<SynoSubtitle>> ProbeSubtitlesAsync(string videoUrl, int timeoutSeconds = 5)
    {
        var found = new List<SynoSubtitle>();

        if (string.IsNullOrEmpty(videoUrl) || !IsSameHostAsBase(videoUrl))
            return found;

        string noExt = Regex.Replace(videoUrl, @"\.[^./?#]+$", string.Empty);
        var candidates = new List<string>();

        foreach (var ext in new[] { "ass", "srt", "vtt" })
        {
            if (!string.IsNullOrEmpty(noExt) && noExt != videoUrl)
                candidates.Add($"{noExt}.{ext}");

            candidates.Add($"{videoUrl}.{ext}");
        }

        foreach (var candidate in candidates)
        {
            if (!IsSameHostAsBase(candidate))
                continue;

            string body;
            try
            {
                body = await Http.Get(candidate, timeoutSeconds: timeoutSeconds, weblog: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CatchId={CatchId}", "id_syno_dlna_probe_get");
                continue;
            }

            // на несуществующий файл сервер может ответить и страницей с ошибкой, поэтому смотрим содержимое
            if (string.IsNullOrEmpty(body) || !SubContentRegex.IsMatch(body))
                continue;

            AddSubtitle(found, candidate, string.Empty);

            if (found.Count > 0)
            {
                string type = SubType(candidate);
                found[0].label = $"Sub [{(string.IsNullOrEmpty(type) ? "SRT" : type)}] #1";
            }

            return found;
        }

        return found;
    }

    static bool IsSameHostAsBase(string url)
    {
        var baseUri = ModInit.baseUri;
        if (baseUri == null)
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        return string.Equals(parsed.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
