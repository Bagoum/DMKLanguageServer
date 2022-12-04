using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using BagoumLib.Expressions;
using Danmokou.Reflection;
using LanguageServer.VsCode.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DMKLanguageServer; 

public class Documentation {
    private static readonly Regex xref = new("<xref href=\"([^\"(]+)(\\([^)]*\\))?\"[^>]*></xref>");
    private static readonly MatchEvaluator xrefReplace = m => m.Groups[1].Value;
    public record Parameter(string Id, string Type, string Description) {
        public Parameter() : this("", "", "") { }
        
        public Parameter Simplify() => 
            this with { Description = xref.Replace(Description.Trim(), xrefReplace) };
    }

    public record Signature(string Content, Parameter[]? Parameters, Parameter? Return) {
        public Signature() : this("", null, null) { }

        public Signature Simplify() => Parameters == null ?
            this :
            this with { Parameters = Parameters.Select(p => p.Simplify()).ToArray() };
    }

    public record Object(string Uid, string CommentId, string Id, string Parent, string[] Children, string[] Langs,
        string Name, string NameWithType, string FullName, string Type, string Namespace, string Summary,
        string[] Example, Signature? Syntax, string Overload) {
        public Object() : this("", "", "", "", Array.Empty<string>(), Array.Empty<string>(), "", "", "", "", "", "",
            Array.Empty<string>(), null, "") { }

        public MarkupContent? AsMarkup =>
            Summary is { Length: > 0 } s ? new MarkupContent(MarkupKind.PlainText, s) : null;

        public Object Simplify() =>
            this with { Summary = xref.Replace(Summary.Trim(), xrefReplace), 
                Syntax = Syntax?.Simplify() };
    }

    private record DocfxFile(Object[] Items) {
        public DocfxFile() : this(Array.Empty<Object>()) { }
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, Object> MethodsByFullName = new();
    private readonly Dictionary<string, Object> ClassesByFullName = new();
    private readonly HashSet<string> EnumRoots = new();
    private readonly Dictionary<string, Object> EnumsByFullName = new();
    public Documentation(string[] ymlContents) {
        foreach (var yml in ymlContents) {
            var data = Deserializer.Deserialize<DocfxFile>(yml);
            foreach (var _obj in data.Items) {
                var obj = _obj.Simplify();
                if (obj.Type == "Method") {
                    var splitOn = obj.Name.Contains('<') ? 
                        //check if it's a generic like Method<T>(T arg)
                        // or a constructed argument like Method(List<float> arg)
                        Math.Min(obj.Name.IndexOf('<'), obj.Name.IndexOf('('))
                        : obj.Name.IndexOf('(');
                    var key = $"{obj.Parent}.{obj.Name[..splitOn]}";
                    MethodsByFullName[key] = obj;
                } else if (obj.Type is "Class" or "Struct") {
                    var splitOn = obj.Name.Contains('<') ?
                        obj.Name.IndexOf('<') :
                        obj.Name.Length;
                    var key = $"{obj.Parent}.{obj.Name[..splitOn]}";
                    ClassesByFullName[key] = obj;
                } else if (obj.Type == "Enum")
                    EnumRoots.Add(obj.FullName);
                else if (obj.Type == "Field" && EnumRoots.Contains(obj.Parent)) {
                    //The enum class, eg. PhaseType, is marked as type Enum,
                    // but its components, eg. PhaseType.Nonspell, are marked as type field
                    EnumsByFullName[obj.FullName] = obj;
                }
            }
        }
    }

    private static readonly CSharpTypePrinter TypePrinter = new() {
        PrintTypeNamespace = _ => true
    };

    public Object? FindBySignature(Reflector.MethodSignature sig) {
        if (sig.Mi is MethodInfo mi) {
            return FindBySignature(mi);
        } else {
            var name = TypePrinter.Print(sig.Mi.DeclaringType!);
            return ClassesByFullName.TryGetValue(name, out var v) ? v : null;
        }
    }
    public Object? FindBySignature(MethodInfo mi) {
        var name = $"{TypePrinter.Print(mi.DeclaringType!)}.{mi.Name}";
        return MethodsByFullName.TryGetValue(name, out var v) ? v : null;
    }

    public Object? FindEnum(object enumValue) {
        var name = $"{TypePrinter.Print(enumValue.GetType())}.{enumValue.ToString()}";
        return EnumsByFullName.TryGetValue(name, out var v) ? v : null;
    }
}