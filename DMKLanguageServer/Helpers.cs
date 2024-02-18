using BagoumLib;
using Danmokou.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DMKLanguageServer;

internal static class Helpers {
    public static readonly JsonSerializer CamelCaseJsonSerializer = new JsonSerializer {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static string GetTimeStamp() {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    public static string EscapeTypeMD(this string typ) => typ.Replace("<", "\\<");

    public static readonly Dictionary<Type, List<(string name, object val)>> bdsl2EnumResolvers = new();
    static Helpers() {
        foreach (var (k, vs) in Reflector.bdsl2EnumResolvers)
            foreach (var (t, v) in vs)
                bdsl2EnumResolvers.AddToList(t, (k, v));
    }
}