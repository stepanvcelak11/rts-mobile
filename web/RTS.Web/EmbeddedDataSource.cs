using System.Reflection;
using RTS.Data;

namespace RTS.Web;

/// <summary>Reads the JSON definitions embedded into this assembly (see RTS.Web.csproj).</summary>
public sealed class EmbeddedDataSource : IDataSource
{
    public IEnumerable<DataFile> ReadAll()
    {
        Assembly asm = typeof(EmbeddedDataSource).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("Data/") || !name.EndsWith(".json")) continue;
            using Stream s = asm.GetManifestResourceStream(name);
            using var reader = new StreamReader(s);
            yield return new DataFile(name.Substring("Data/".Length), reader.ReadToEnd());
        }
    }
}
