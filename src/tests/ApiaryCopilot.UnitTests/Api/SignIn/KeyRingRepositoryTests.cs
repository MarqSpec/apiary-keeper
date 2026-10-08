using System.Xml.Linq;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The key ring's repository moves the key manager's XML to and from PostgreSQL's store, unchanged.
public sealed class KeyRingRepositoryTests
{
    private readonly IDataProtectionKeyStore _store = A.Fake<IDataProtectionKeyStore>();

    [Fact] // regression risk: what the key manager stores is what it reads back, element for element
    public void GetAllElements_ShouldReturnEveryStoredElement_WhenTheStoreHoldsSome()
    {
        A.CallTo(() => _store.GetAll()).Returns(["<key id=\"1\"><a/></key>", "<key id=\"2\" />"]);

        var elements = new KeyRingRepository(_store).GetAllElements();

        elements.Select(e => (string?)e.Attribute("id")).Should().Equal("1", "2");
    }

    [Fact] // boundary: one unreadable row is skipped rather than stopping every key from loading
    public void GetAllElements_ShouldSkipAnElement_WhenItIsNotXml()
    {
        A.CallTo(() => _store.GetAll()).Returns(["<key id=\"1\" />", "<key id=", "<key id=\"3\" />"]);

        var elements = new KeyRingRepository(_store).GetAllElements();

        elements.Select(e => (string?)e.Attribute("id")).Should().Equal("1", "3");
    }

    [Fact] // regression risk: an element is stored whole, under its friendly name
    public void StoreElement_ShouldStoreTheElementUnderItsFriendlyName_WhenStored()
    {
        new KeyRingRepository(_store).StoreElement(XElement.Parse("<key id=\"9\">\n  <encryptedSecret />\n</key>"), "key-9");

        A.CallTo(() => _store.Add("key-9", "<key id=\"9\"><encryptedSecret /></key>")).MustHaveHappenedOnceExactly();
    }
}
