using Shared.Models.Module;
using System.Collections.Generic;

namespace SynoDLNA;

public class ModuleConf : ModuleBaseConf
{
    public bool enable { get; set; }

    /// <summary>
    /// Базовый адрес DLNA-сервера, например http://10.1.1.100:50001
    /// </summary>
    public string url { get; set; }

    /// <summary>
    /// ObjectID корневой папки для Browse ("0" по спецификации UPnP ContentDirectory)
    /// </summary>
    public string rootObjectId { get; set; } = "0";

    /// <summary>
    /// Подбирать внешние субтитры (по DIDL и по угадыванию соседних файлов)
    /// </summary>
    public bool subtitles { get; set; } = true;

    /// <summary>
    /// Для клиентов из локальной сети отдавать прямую ссылку на NAS, а не через /proxy.
    /// По умолчанию выключено: плеер Lampa запрашивает видео в режиме с проверкой CORS,
    /// а DLNA-сервер Synology (старый pupnp SDK) заголовка Access-Control-Allow-Origin
    /// не отдаёт - прямая ссылка приводит к "blocked by CORS policy" прямо при старте
    /// воспроизведения. Включать только если ваш DLNA-сервер отдаёт заголовки CORS.
    /// </summary>
    public bool directLocalIp { get; set; }

    /// <summary>
    /// Разрешить доступ из локальной сети без токена
    /// </summary>
    public bool allowLocalWithoutToken { get; set; } = true;

    public int timeoutSeconds { get; set; } = 10;

    public int cacheMinutes { get; set; } = 5;

    public List<SynoUser> users { get; set; } = new();
}

public class SynoUser
{
    /// <summary>
    /// Только для логов/аудита, в авторизации не участвует
    /// </summary>
    public string name { get; set; }

    public string token { get; set; }

    public bool enable { get; set; } = true;
}
