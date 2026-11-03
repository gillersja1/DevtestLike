using System.Xml.Linq;

namespace PolicyService;

/// <summary>SOAP 1.1 envelopes, the way the legacy systems this stands in for speak.</summary>
public static class Soap
{
    public static readonly XNamespace Env = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Policy = "http://devtestlike.example/policy";
    public static readonly XNamespace Rating = "http://devtestlike.example/rating";
    public static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public const string ContentType = "text/xml; charset=utf-8";

    public static XDocument Envelope(XElement body, XElement? header = null) => new(
        new XElement(Env + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Env),
            header is null ? null : new XElement(Env + "Header", header),
            new XElement(Env + "Body", body)));

    /// <summary>A SOAP fault: faultcode is soap:Client for the caller's mistakes, soap:Server for ours.</summary>
    public static XDocument Fault(bool client, string reason, string detailCode) => Envelope(
        new XElement(Env + "Fault",
            new XElement("faultcode", client ? "soap:Client" : "soap:Server"),
            new XElement("faultstring", reason),
            new XElement("detail", new XElement(Policy + "code", detailCode))));

    /// <summary>The single operation element inside soap:Body, or null if the envelope isn't one.</summary>
    public static XElement? Operation(XDocument request) =>
        request.Root?.Name == Env + "Envelope" ? request.Root.Element(Env + "Body")?.Elements().FirstOrDefault() : null;

    public static IResult Xml(XDocument document, int status = 200) =>
        Results.Text("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + document.ToString(SaveOptions.DisableFormatting),
            ContentType, statusCode: status);
}
