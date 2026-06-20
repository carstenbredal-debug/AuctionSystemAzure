using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace KSeF.Functions.Services;

public class FaValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Validates generated invoice XML against the official KSeF FA(3) XSD
/// (namespace http://crd.gov.pl/wzor/2025/06/25/13775/) before submission.
/// Schemas are embedded resources under Schemas/FA3/.
/// </summary>
public class FaValidator
{
    private const string MainSchemaResource = "KSeF.Functions.Schemas.FA3.schemat.xsd";
    private const string MainSchemaBaseUri = "http://crd.gov.pl/wzor/2025/06/25/13775/schemat.xsd";

    private static readonly Lazy<XmlSchemaSet> SchemaSet = new(LoadSchemas);

    // FA(3) schema dependency chain (resolved from embedded resources):
    //   schemat.xsd -> StrukturyDanych_v10-0E -> ElementarneTypyDanych_v10-0E -> KodyKrajow_v10-0E
    private const string EdBase = "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/";
    private static readonly Dictionary<string, string> UrlToResource = new()
    {
        [EdBase + "StrukturyDanych_v10-0E.xsd"] = "KSeF.Functions.Schemas.FA3.StrukturyDanych_v10-0E.xsd",
        [EdBase + "ElementarneTypyDanych_v10-0E.xsd"] = "KSeF.Functions.Schemas.FA3.ElementarneTypyDanych_v10-0E.xsd",
        [EdBase + "KodyKrajow_v10-0E.xsd"] = "KSeF.Functions.Schemas.FA3.KodyKrajow_v10-0E.xsd",
    };

    private static XmlSchemaSet LoadSchemas()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resolver = new EmbeddedResourceResolver(assembly);

        var schemaSet = new XmlSchemaSet { XmlResolver = resolver };

        using var mainStream = assembly.GetManifestResourceStream(MainSchemaResource)
            ?? throw new InvalidOperationException($"FA(3) XSD schema not found in embedded resources: {MainSchemaResource}");

        using var reader = XmlReader.Create(mainStream, new XmlReaderSettings { XmlResolver = resolver }, MainSchemaBaseUri);
        schemaSet.Add(null, reader);
        schemaSet.Compile();

        return schemaSet;
    }

    /// <summary>
    /// Validate an FA(3) invoice XML document. Returns IsValid=false with a list of
    /// schema violation messages when the document does not conform to the XSD.
    /// </summary>
    public FaValidationResult Validate(string xml)
    {
        var result = new FaValidationResult { IsValid = true };

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

        using var stringReader = new StringReader(xml);
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
