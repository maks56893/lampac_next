using System.Collections.Generic;

namespace SynoDLNA.Models;

public class SynoBrowseResult
{
    public List<SynoItem> folders { get; set; } = new();

    public List<SynoItem> files { get; set; } = new();
}
