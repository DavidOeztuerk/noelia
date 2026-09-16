using UserService.Domain;

namespace Demo.Tests;

public class EmailAddressTests
{
    /// <summary>
    /// Capitalisation must not create a second account for the same person.
    /// </summary>
    [Fact]
    public void Addresses_are_normalised()
    {
        EmailAddress.Parse("  Someone@Example.COM ").Value.Should().Be("someone@example.com");
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("someone@")]
    [InlineData("someone@nodot")]
    [InlineData("two@at@example.com")]
    [InlineData("with space@example.com")]
    public void What_cannot_be_an_address_is_refused(string input)
    {
        var parse = () => EmailAddress.Parse(input);

        parse.Should().Throw<ArgumentException>();
    }
}
