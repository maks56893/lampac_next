using Shared.Models.Base;
using Shared.Services;
using SynoDLNA.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace SynoDLNA.Services;

/// <summary>
/// Порт серверной части test_plugins/synology_dlna.js: SOAP Browse к UPnP ContentDirectory
/// и разбор DIDL-Lite ответа. Выполняется сервер-сервер (Lampac -> NAS), поэтому CORS не проблема
/// и клиенту никогда не нужно знать адрес NAS.
/// </summary>
public static class UpnpClient
{
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(UpnpClient));

    /// <summary>
    /// Бросает исключение при сбое запроса или разбора ответа - вызывающий код (Controller)
    /// решает, что вернуть клиенту.
    /// </summary>
    public static async Task<List<SynoItem>> BrowseAsync(string objectId, string flag, int timeoutSeconds)
    {
        string baseUrl = ModInit.conf?.url;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("SynoDLNA.url is not configured");

        string serviceUrl = baseUrl.TrimEnd('/') + "/ContentDirectory/control";

        // objectId приходит из query клиента (в оригинальном JS подставлялся в SOAP-тело без
        // экранирования - XML-инъекция в конверт). Экранируем обязательно.
        string safeObjectId = SecurityElement.Escape(string.IsNullOrEmpty(objectId) ? "0" : objectId);
        string safeFlag = SecurityElement.Escape(string.IsNullOrEmpty(flag) ? "BrowseDirectChildren" : flag);

        string soapBody =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                    "<u:Browse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\">" +
                        $"<ObjectID>{safeObjectId}</ObjectID>" +
                        $"<BrowseFlag>{safeFlag}</BrowseFlag>" +
                        "<Filter>*</Filter>" +
                        "<StartingIndex>0</StartingIndex>" +
                        "<RequestedCount>1000</RequestedCount>" +
                        "<SortCriteria></SortCriteria>" +
                    "</u:Browse>" +
                "</s:Body>" +
            "</s:Envelope>";

        var headers = new List<HeadersModel>
        {
            new HeadersModel("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"")
        };

        var content = new StringContent(soapBody, Encoding.UTF8, "text/xml");

        string response;
        try
        {
            response = await Http.Post(
                serviceUrl,
                content,
                headers: headers,
                timeoutSeconds: Math.Max(1, timeoutSeconds),
                disposeData: true
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatchId={CatchId}", "id_syno_dlna_soap_request");
            throw new InvalidOperationException("SOAP request to the DLNA server failed", ex);
        }

        if (string.IsNullOrEmpty(response))
            throw new InvalidOperationException("empty or non-200 response from the DLNA server");

        return ParseBrowseResponse(response);
    }

    /// <summary>
    /// Только XmlReader с запрещённым DTD и отключённым резолвером - защита от XXE.
    /// Применяется и к внешнему SOAP-конверту, и к вложенному DIDL-Lite документу.
    /// </summary>
    static XDocument SafeParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var stringReader = new System.IO.StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader);
    }

    /// <summary>
    /// Порт parseXmlResponse (test_plugins/synology_dlna.js:479-560).
    /// </summary>
    static List<SynoItem> ParseBrowseResponse(string xmlResponse)
    {
        XDocument envelope;
        try
        {
            envelope = SafeParseXml(xmlResponse);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to parse SOAP envelope", ex);
        }

        string result = envelope.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Result", StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrEmpty(result))
            return new List<SynoItem>();

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(result);
        }
        catch
        {
            decoded = result;
        }

        XDocument resultDoc;
        try
        {
            resultDoc = SafeParseXml(decoded);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to parse DIDL-Lite result", ex);
        }

        var items = new List<SynoItem>();

        foreach (var node in resultDoc.Descendants().Where(e =>
            string.Equals(e.Name.LocalName, "container", StringComparison.Ordinal) ||
            string.Equals(e.Name.LocalName, "item", StringComparison.Ordinal)))
        {
            items.Add(ParseNode(node));
        }

        return items;
    }

    static SynoItem ParseNode(XElement node)
    {
        var item = new SynoItem
        {
            id = (string)node.Attribute("id"),
            parentId = (string)node.Attribute("parentID")
        };

        var resources = new List<Dictionary<string, string>>();
        var captions = new List<string>();
        string title = null;
        string type = null;

        foreach (var child in node.Elements())
        {
            string local = child.Name.LocalName.ToLowerInvariant();

            // все <res> собираем отдельно - у видео их может быть несколько (видео + субтитры)
            if (local == "res")
            {
                var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["url"] = (child.Value ?? string.Empty).Trim()
                };

                foreach (var attr in child.Attributes())
                {
                    res[attr.Name.LocalName] = attr.Value;

                    // некоторые сервера кладут ссылку на субтитры прямо в атрибут res
                    if (string.Equals(attr.Name.LocalName, "subtitleFileUri", StringComparison.OrdinalIgnoreCase))
                        captions.Add(attr.Value);
                }

                resources.Add(res);
                continue;
            }

            // расширения Samsung (sec:CaptionInfo/CaptionInfoEx) и Plex/pv:subtitleFileUri
            if (local is "captioninfo" or "captioninfoex" or "subtitlefileuri")
            {
                captions.Add(child.Value);
                continue;
            }

            if (local == "title" && title == null)
                title = child.Value;
            else if (local == "class" && type == null)
                type = child.Value;
        }

        item.title = title;
        item.type = type;

        string family = !string.IsNullOrEmpty(type) && type.Contains("videoItem") ? "video"
            : !string.IsNullOrEmpty(type) && type.Contains("audioItem") ? "audio"
            : !string.IsNullOrEmpty(type) && type.Contains("imageItem") ? "image" : string.Empty;

        Dictionary<string, string> media = null;

        foreach (var res in resources)
        {
            res.TryGetValue("url", out string resUrl);
            res.TryGetValue("protocolInfo", out string protocolInfo);

            // text/plain у srt встречается не реже, чем text/srt - смотрим и на расширение
            if (SubtitleResolver.IsSubMime(protocolInfo) || SubtitleResolver.IsSubUrl(resUrl))
            {
                captions.Add(resUrl);
                continue;
            }

            if (media != null)
                continue;

            string resFamily = SubtitleResolver.MimeFamily(protocolInfo);
            if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(resFamily) && resFamily != family)
                continue;

            media = res;
        }

        if (media == null && resources.Count > 0)
            media = resources[0];

        if (media != null)
        {
            media.TryGetValue("url", out string mediaUrl);
            item.url = mediaUrl;
            media.TryGetValue("protocolInfo", out string protocolInfo);
            item.protocolInfo = protocolInfo;

            if (media.TryGetValue("size", out string sizeStr) && long.TryParse(sizeStr, out long size))
                item.size = size;

            if (media.TryGetValue("duration", out string duration))
                item.duration = duration;

            if (media.TryGetValue("resolution", out string resolution))
                item.resolution = resolution;
        }

        string videoBaseName = SubtitleResolver.UrlFileName(item.url);
        string videoBase = SubtitleResolver.FileBase(!string.IsNullOrEmpty(videoBaseName) ? videoBaseName : (item.title ?? string.Empty));

        foreach (var captionUrl in captions)
            SubtitleResolver.AddSubtitle(item.subtitles, captionUrl, videoBase);

        return item;
    }
}
