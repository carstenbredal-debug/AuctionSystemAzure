using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace KSeF.Functions.Services;

public class JpkValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class JpkValidator
{
    private static readonly Lazy<XmlSchemaSet> SchemaSet = new(LoadSchemas);

    private static readonly Dictionary<string, string> UrlToResource = new()
    {
        ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2023/09/06/eD/KodyKrajow/KodyKrajow_v13-0E.xsd"] =
            "KSeF.Functions.Schemas.KodyKrajow_v13-0E.xsd",
        ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/KodyUrzedowSkarbowych/KodyUrzedowSkarbowych_v8-0E.xsd"] =
            "KSeF.Functions.Schemas.KodyUrzedowSkarbowych_v8-0E.xsd",
        ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/09/13/eD/DefinicjeTypy/StrukturyDanych_v12-0E.xsd"] =
            "KSeF.Functions.Schemas.StrukturyDanych_v12-0E.xsd",
    };

    private static XmlSchemaSet LoadSchemas()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resolver = new EmbeddedResourceResolver(assembly);

        var schemaSet = new XmlSchemaSet { XmlResolver = resolver };

        using var mainStream = assembly.GetManifestResourceStream("KSeF.Functions.Schemas.schemat.xsd")
            ?? throw new InvalidOperationException("Main XSD schema not found in embedded resources.");

        using var reader = XmlReader.Create(mainStream, new XmlReaderSettings(), "http://crd.gov.pl/wzor/2025/12/19/14090/schemat.xsd");
        schemaSet.Add(null, reader);
        schemaSet.Compile();

        return schemaSet;
    }

    public JpkValidationResult Validate(string xml)
    {
        var result = new JpkValidationResult { IsValid = true };

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = SchemaSet.Value
        };

        settings.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
            {
                result.IsValid = false;
                result.Errors.Add(e.Message);
            }
        };

        using var stringReader = new System.IO.StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);

        while (reader.Read()) { }

        return result;
    }

    private class EmbeddedResourceResolver : XmlResolver
    {
        private readonly Assembly _assembly;

        public EmbeddedResourceResolver(Assembly assembly) => _assembly = assembly;

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var url = absoluteUri.ToString();
            if (UrlToResource.TryGetValue(url, out var resourceName))
            {
                return _assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
            }

            throw new FileNotFoundException($"Schema not found for URI: {url}");
        }
    }
}
