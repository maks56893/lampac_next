using System.Collections.Generic;

namespace SynoDLNA.Models;

/// <summary>
/// Папка или файл из ответа UPnP ContentDirectory Browse.
/// В кеше и внутри UpnpClient/SubtitleResolver url всегда "сырой" (адрес на NAS);
/// подстановка публичной/проксированной ссылки происходит только в контроллере,
/// на каждый запрос отдельно.
/// </summary>
public class SynoItem
{
    public string id { get; set; }

    public string parentId { get; set; }

    public string title { get; set; }

    /// <summary>
    /// upnp:class, например object.container.storageFolder, object.item.videoItem
    /// </summary>
    public string type { get; set; }

    public string url { get; set; }

    public long? size { get; set; }

    public string duration { get; set; }

    public string resolution { get; set; }

    public string protocolInfo { get; set; }

    public List<SynoSubtitle> subtitles { get; set; } = new();
}
