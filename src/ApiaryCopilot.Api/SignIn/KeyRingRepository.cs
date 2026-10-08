using System.Xml;
using System.Xml.Linq;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The Data Protection key ring's repository over the app's PostgreSQL (D-19), so the keys - and the cookies and sign-in
/// state they protect - survive a restart. The key manager encrypts each element with the certificate before it gets
/// here; this only moves the XML. An element that does not parse is skipped, so one bad row cannot stop every key loading.
/// </summary>
public sealed class KeyRingRepository(IDataProtectionKeyStore store) : IXmlRepository
{
    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        foreach (var xml in store.GetAll())
        {
            try
            {
                elements.Add(XElement.Parse(xml));
            }
            catch (XmlException)
            {
                // Skipped: the key manager makes a new key if no usable one is left.
            }
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        store.Add(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }
}
