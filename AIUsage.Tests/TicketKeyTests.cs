using AIUsage.Data;

namespace AIUsage.Tests;

/// <summary>
/// The shared ticket-key validator. Every writer of <c>SessionTicketLinks.ticket_key</c> goes
/// through this so no path (the Live Code one used to) can persist an unconstrained key that
/// later renders in the UI.
/// </summary>
public class TicketKeyTests
{
    [Theory]
    [InlineData("ABC-1")]
    [InlineData("SFTY-1234")]
    [InlineData("AB1-999999")]
    [InlineData("ABCDEFGHIJ-1")]
    public void IsValid_accepts_real_keys(string key) => Assert.True(TicketKey.IsValid(key));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("A-1")]                 // project key too short
    [InlineData("ABCDEFGHIJK-1")]       // project key too long
    [InlineData("ABC-1234567")]         // number too long
    [InlineData("ABC-")]
    [InlineData("abc-1")]               // must already be uppercased
    [InlineData("X'+alert(1)+'Y")]
    [InlineData("ABC-1; calc")]
    [InlineData("ABC-1\nABC-2")]
    public void IsValid_rejects_anything_else(string? key) => Assert.False(TicketKey.IsValid(key));

    [Fact]
    public void Require_trims_and_uppercases()
    {
        Assert.Equal("ABC-12", TicketKey.Require("  abc-12 "));
    }

    [Fact]
    public void Require_throws_on_an_invalid_key()
    {
        var ex = Assert.Throws<ArgumentException>(() => TicketKey.Require("X'+alert(1)+'Y"));
        Assert.Contains("not a valid ticket key", ex.Message);
    }
}
