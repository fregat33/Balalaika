using FluentAssertions;
using Balalaika.Index;

namespace Balalaika.Tests;

public sealed class EnclosingDisplayTests
{
    [Theory]
    [InlineData("T:Demo.Foo", 0)]
    [InlineData("M:Demo.Foo.Run", 0)]
    [InlineData("M:Demo.Foo.Run(System.Int32)", 1)]
    [InlineData("M:Demo.Foo.Run(System.Collections.Generic.Dictionary{System.String,System.Int32},System.Int32)", 2)]
    [InlineData("M:Demo.Foo.#ctor(Demo.Foo,System.String[])", 2)]
    public void ParameterCount_CountsTopLevelCommasInsideTheParentheses(string docId, int expected)
    {
        // Act / Assert
        EnclosingDisplay.ParameterCount(docId).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "Foo", "Foo")]
    [InlineData("Foo", "Run", "Foo.Run")]
    [InlineData("Foo", ".ctor", "Foo.ctor")]
    [InlineData("Outer", "Inner", "Outer.Inner")]
    public void Format_JoinsContainingTypeAndNameWithoutLeadingDot(string? containing, string name, string expected)
    {
        // Act / Assert
        EnclosingDisplay.Format(containing, name).Should().Be(expected);
    }

    [Fact]
    public void Disambiguate_SuffixesOnlyNamesSharedByDistinctDocIds()
    {
        // Act
        IReadOnlyDictionary<string, string> names = EnclosingDisplay.Disambiguate(
        [
            ("M:A.Run", "A.Run"),
            ("M:A.Run(System.Int32)", "A.Run"),
            ("M:A.Run", "A.Run"),
            ("M:A.Stop", "A.Stop"),
        ]);

        // Assert
        names.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["M:A.Run"] = "A.Run(0 params)",
            ["M:A.Run(System.Int32)"] = "A.Run(1 params)",
            ["M:A.Stop"] = "A.Stop",
        });
    }

    [Fact]
    public void Disambiguate_QualifiesNamesSharedAcrossContainingTypes_InsteadOfCountingParameters()
    {
        // Act: two controllers of one short name in different namespaces, an action in each, and an overload pair inside one of them
        IReadOnlyDictionary<string, string> names = EnclosingDisplay.Disambiguate(
        [
            ("T:Api.V1.Orders.InvoicesController", "InvoicesController"),
            ("T:Api.V1.InvoicesController", "InvoicesController"),
            ("M:Api.V1.Orders.InvoicesController.Get", "InvoicesController.Get"),
            ("M:Api.V1.InvoicesController.Get", "InvoicesController.Get"),
            ("M:Api.V1.InvoicesController.Get(System.Int32)", "InvoicesController.Get"),
            ("M:Api.V1.InvoicesController.#ctor", "InvoicesController.ctor"),
        ]);

        // Assert
        names.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["T:Api.V1.Orders.InvoicesController"] = "Api.V1.Orders.InvoicesController",
            ["T:Api.V1.InvoicesController"] = "Api.V1.InvoicesController",
            ["M:Api.V1.Orders.InvoicesController.Get"] = "Api.V1.Orders.InvoicesController.Get",
            ["M:Api.V1.InvoicesController.Get"] = "Api.V1.InvoicesController.Get(0 params)",
            ["M:Api.V1.InvoicesController.Get(System.Int32)"] = "Api.V1.InvoicesController.Get(1 params)",
            ["M:Api.V1.InvoicesController.#ctor"] = "InvoicesController.ctor",
        });
    }

    [Theory]
    [InlineData("M:Ns.Foo`1.#ctor(System.Int32)", "Ns.Foo.ctor")]
    [InlineData("T:Ns.Outer`2.Inner", "Ns.Outer.Inner")]
    [InlineData("P:Ns.Foo.Count", "Ns.Foo.Count")]
    [InlineData("<top-level>", "<top-level>")]
    public void QualifiedNameOf_StripsPrefixParametersAndArity(string docId, string expected)
    {
        // Act / Assert
        EnclosingDisplay.QualifiedNameOf(docId).Should().Be(expected);
    }
}
